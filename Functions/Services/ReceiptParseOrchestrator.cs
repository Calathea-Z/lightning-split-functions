using Api.Abstractions.Receipts;
using Api.Abstractions.Transport;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Functions.Contracts.HeuristicExtractor;
using Functions.Contracts.Parsing;
using Functions.Infrastructure.Logging;
using Functions.Infrastructure.Resilience;
using Functions.Services.Abstractions;
using Functions.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Functions.Services;

public class ReceiptParseOrchestrator : IReceiptParseOrchestrator
{
    private readonly ILogger<IReceiptParseOrchestrator> _log;
    private readonly BlobServiceClient _blobSvc;
    private readonly IReceiptOcr _ocr;
    private readonly IImagePreprocessor _imagePrep;
    private readonly IReceiptApiClient _api;
    private readonly IReceiptNormalizer _normalizer;
    private readonly IConfiguration _cfg;
    private readonly string _llmModelTag;

    public ReceiptParseOrchestrator(
        ILogger<IReceiptParseOrchestrator> log,
        BlobServiceClient blobSvc,
        IReceiptOcr ocr,
        IImagePreprocessor imagePrep,
        IReceiptApiClient api,
        IReceiptNormalizer normalizer,
        IConfiguration cfg)
    {
        _log = log;
        _blobSvc = blobSvc;
        _ocr = ocr;
        _imagePrep = imagePrep;
        _api = api;
        _normalizer = normalizer;
        _cfg = cfg;

        _llmModelTag =
            _cfg["AOAI_DEPLOYMENT"] ??
            Environment.GetEnvironmentVariable("AOAI_DEPLOYMENT") ??
            "unknown";
    }

    private const long MaxBlobBytes = 50L * 1024 * 1024; // 50 MB
    private const string LockContainer = "receipt-parse-locks";

    // Resilience
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);

    private static decimal Round2(decimal d)
        => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    private static decimal? Round2Opt(decimal? d)
        => d.HasValue ? Math.Round(d.Value, 2, MidpointRounding.AwayFromZero) : (decimal?)null;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string ParserVersionTag =
        typeof(ReceiptParseOrchestrator).Assembly.GetName().Version?.ToString() ?? "unknown";

    public async Task RunAsync(ReceiptParseMessage req, CancellationToken ct)
    {
        var rid = Guid.Parse(req.ReceiptId!);

        // Idempotency lock
        var locks = _blobSvc.GetBlobContainerClient(LockContainer);
        await locks.CreateIfNotExistsAsync(cancellationToken: ct);
        var lockBlob = locks.GetBlobClient($"{rid}.lock");

        try
        {
            await lockBlob.UploadAsync(
                BinaryData.FromString(""),
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = new BlobHttpHeaders { ContentType = "text/plain" },
                    Metadata = new Dictionary<string, string> { ["createdUtc"] = DateTime.UtcNow.ToString("O") }
                },
                ct
            );
        }
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.ErrorCode == BlobErrorCode.ConditionNotMet)
        {
            _log.LogInformation("Duplicate/retry: lock exists for {ReceiptId}. Skipping.", rid);
            return;
        }

        try
        {
            // 1) Blob fetch + size guard
            var blobClient = _blobSvc.GetBlobContainerClient(req.Container).GetBlobClient(req.Blob);

            var props = await Retry.RetryAsync<Response<BlobProperties>>(
                op: "blob.getProperties",
                perAttemptTimeout: ShortTimeout,
                action: ct2 => blobClient.GetPropertiesAsync(cancellationToken: ct2),
                isTransient: IsTransientBlob,
                maxAttempts: 3,
                log: _log,
                outer: ct);

            if (props.Value.ContentLength > MaxBlobBytes)
                throw new InvalidOperationException($"Blob too large ({props.Value.ContentLength} bytes). Max {MaxBlobBytes}.");

            var original = await Retry.RetryAsync<Stream>(
                op: "blob.openRead",
                perAttemptTimeout: ShortTimeout,
                action: ct2 => blobClient.OpenReadAsync(cancellationToken: ct2),
                isTransient: IsTransientBlob,
                maxAttempts: 3,
                log: _log,
                outer: ct);

            await using (original)
            {
                _log.BlobUploaded(rid, $"{req.Container}/{req.Blob}", props.Value.ContentLength);

                // 2) Preprocess
                await using var pre = await _imagePrep.PrepareAsync(original, ct);

                // 2.1) Buffer preprocessed image to bytes so each retry gets a fresh stream
                byte[] preBytes;
                {
                    if (pre.CanSeek) pre.Position = 0;
                    using var ms = new MemoryStream();
                    await pre.CopyToAsync(ms, 81920, ct);
                    preBytes = ms.ToArray();
                }

                // 3) OCR
                _log.OcrRequested(rid, _ocr.GetType().Name);
                string rawText = await Retry.RetryAsync(
                    op: "ocr.read",
                    perAttemptTimeout: OcrTimeout,
                    action: ct2 =>
                    {
                        var fresh = new MemoryStream(preBytes, writable: false);
                        return _ocr.ReadAsync(fresh, ct2); // Task<string>
                    },
                    isTransient: IsTransientOcr,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                if (string.IsNullOrWhiteSpace(rawText))
                    _log.LogWarning("OCR returned empty text");

                // 4) Persist raw
                await Retry.RetryAsync(
                    op: "api.patchRaw",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchRawTextAsync(rid, new UpdateRawTextRequest(rawText), ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                // 5) Heuristics
                var heur = HeuristicExtractor.Extract(rawText);
                _log.LogInformation("Heuristics found {Count} items", heur.Items.Count);
                if (!heur.IsSane || heur.Items.Count == 0)
                    _log.NeedsReview(rid, "Weak heuristic extraction");

                // 6) Decide
                if (IsHeuristicsStrong(heur, out var strongReason))
                {
                    _log.LogInformation("Skipping LLM: heuristics strong ({Reason})", strongReason);

                    // Build & replace once
                    var (items, posted, skippedNonItem, skippedBad, skippedEmpty) =
                        BuildItemsFromHeuristics(heur, isLlm: false);

                    await Retry.RetryAsync(
                        op: "api.putItems.heur-strong",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PutItemsAsync(rid, new ReplaceReceiptItemsRequest(Items: items), ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.LogInformation("Replace summary (heur-strong): posted={Posted}, skippedNonItem={NonItem}, skippedBad={Bad}, skippedEmpty={Empty}",
                        posted, skippedNonItem, skippedBad, skippedEmpty);

                    // Totals
                    var sumOfItems = heur.Items.Sum(i => i.UnitPrice * i.Qty);
                    decimal? subOpt = heur.Subtotal ?? (heur.Items.Count > 0 ? Round2(sumOfItems) : (decimal?)null);
                    decimal? taxOpt = heur.Tax;
                    decimal? tipOpt = heur.Tip;

                    decimal totalRounded = Round2(heur.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    decimal? subRounded = Round2Opt(subOpt);
                    decimal? taxRounded = Round2Opt(taxOpt);
                    decimal? tipRounded = Round2Opt(tipOpt);

                    var totalsReq = new UpdateTotalsRequest(subRounded, taxRounded, tipRounded, totalRounded);

                    await Retry.RetryAsync(
                        op: "api.patchTotals",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, totalsReq, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchStatus",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchStatusAsync(rid, new UpdateStatusRequest(ReceiptStatus.Parsed), ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchTotals.final",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, totalsReq, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchParseMeta.heuristics",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchParseMetaAsync(
                            rid,
                            new UpdateParseMetaRequest(
                                ParsedBy: ParseEngine.Heuristics,
                                LlmAttempted: false,
                                LlmAccepted: null,
                                LlmModel: null,
                                ParserVersion: ParserVersionTag,
                                RejectReason: strongReason),
                            ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.Parsed(rid, posted, subRounded ?? 0m, taxRounded ?? 0m, totalRounded);
                    return;
                }

                // 7) LLM attempt
                ParsedReceiptV1? parsed = null;
                bool valid = false;
                decimal itemsSum = 0m;
                string? llmReason = null;

                try
                {
                    var hints = new Dictionary<string, object?>
                    {
                        ["currency"] = "USD",
                        ["candidateSubtotal"] = heur.Subtotal,
                        ["candidateTax"] = heur.Tax,
                        ["candidateTip"] = heur.Tip,
                        ["candidateTotal"] = heur.Total,
                        ["merchantName"] = null,
                        ["datetime"] = null
                    };

                    var parsedJson = await _normalizer.NormalizeAsync(rawText, hints, ct);
                    parsed = JsonSerializer.Deserialize<ParsedReceiptV1>(parsedJson, _jsonOpts);

                    if (parsed is not null)
                    {
                        valid = ParsedReceiptValidator.TryValidate(parsed, out var err, out itemsSum);
                        if (!valid)
                        {
                            _log.LogWarning("Parsed receipt failed validation: {Err}", err);
                            parsed.Issues.Add(err);
                            llmReason = err;
                        }
                    }
                    else
                    {
                        llmReason = "normalizer_null";
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Normalizer failed; will use heuristics fallback.");
                    llmReason = "normalizer_exception";
                }

                if (valid && parsed is not null)
                {
                    // Replace with LLM items
                    var (items, posted, skippedNonItem, skippedBad, skippedEmpty) =
                        BuildItemsFromParsed(parsed, isLlm: true);

                    await Retry.RetryAsync(
                        op: "api.putItems.llm",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PutItemsAsync(rid, new ReplaceReceiptItemsRequest(Items: items), ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.LogInformation("Replace summary (LLM): posted={Posted}, skippedNonItem={NonItem}, skippedBad={Bad}, skippedEmpty={Empty}",
                        posted, skippedNonItem, skippedBad, skippedEmpty);

                    // Totals
                    var itemsSumLocal = parsed.Items.Sum(i => i.LineTotal);
                    decimal? subOpt = parsed.SubTotal ?? (parsed.Items.Count > 0 ? Round2(itemsSumLocal) : (decimal?)null);
                    decimal? taxOpt = parsed.Tax;
                    decimal? tipOpt = parsed.Tip;

                    decimal totalRoundedFinal = Round2(parsed.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    decimal? subRoundedFinal = Round2Opt(subOpt);
                    decimal? taxRoundedFinal = Round2Opt(taxOpt);
                    decimal? tipRoundedFinal = Round2Opt(tipOpt);

                    var totalsReq = new UpdateTotalsRequest(subRoundedFinal, taxRoundedFinal, tipRoundedFinal, totalRoundedFinal);

                    await Retry.RetryAsync(
                        op: "api.patchTotals",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, totalsReq, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchTotals.final",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, totalsReq, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchParseMeta.llm",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchParseMetaAsync(
                            rid,
                            new UpdateParseMetaRequest(
                                ParsedBy: ParseEngine.Llm,
                                LlmAttempted: true,
                                LlmAccepted: true,
                                LlmModel: _llmModelTag,
                                ParserVersion: ParserVersionTag,
                                RejectReason: null),
                            ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchStatus.upgrade",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchStatusAsync(rid, new UpdateStatusRequest(ReceiptStatus.Parsed), ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.Parsed(rid, posted, subRoundedFinal ?? 0m, taxRoundedFinal ?? 0m, totalRoundedFinal);
                }
                else
                {
                    // Heuristics fallback
                    var (items, posted, skippedNonItem, skippedBad, skippedEmpty) =
                        BuildItemsFromHeuristics(heur, isLlm: false);

                    await Retry.RetryAsync(
                        op: "api.putItems.heur-fallback",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PutItemsAsync(rid, new ReplaceReceiptItemsRequest(Items: items), ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.LogInformation("Replace summary (heur-fallback): posted={Posted}, skippedNonItem={NonItem}, skippedBad={Bad}, skippedEmpty={Empty}",
                        posted, skippedNonItem, skippedBad, skippedEmpty);

                    var sumOfItems = heur.Items.Sum(i => i.UnitPrice * i.Qty);
                    decimal? subOpt = heur.Subtotal ?? (heur.Items.Count > 0 ? Round2(sumOfItems) : (decimal?)null);
                    decimal? taxOpt = heur.Tax;
                    decimal? tipOpt = heur.Tip;

                    decimal totalRoundedFinal = Round2(heur.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    decimal? subRoundedFinal = Round2Opt(subOpt);
                    decimal? taxRoundedFinal = Round2Opt(taxOpt);
                    decimal? tipRoundedFinal = Round2Opt(tipOpt);

                    var totalsReq = new UpdateTotalsRequest(subRoundedFinal, taxRoundedFinal, tipRoundedFinal, totalRoundedFinal);

                    await Retry.RetryAsync(
                        op: "api.patchTotals",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, totalsReq, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchTotals.final",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, totalsReq, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchParseMeta.heur-fallback",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchParseMetaAsync(
                            rid,
                            new UpdateParseMetaRequest(
                                ParsedBy: ParseEngine.Heuristics,
                                LlmAttempted: true,
                                LlmAccepted: false,
                                LlmModel: _llmModelTag,
                                ParserVersion: ParserVersionTag,
                                RejectReason: llmReason ?? "llm_rejected_or_invalid"),
                            ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchStatus.needsReview",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchStatusAsync(rid, new UpdateStatusRequest(ReceiptStatus.ParsedNeedsReview), ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.Parsed(rid, posted, subRoundedFinal ?? 0m, taxRoundedFinal ?? 0m, totalRoundedFinal);
                }
            }
        }
        finally
        {
            try { await lockBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct); }
            catch (Exception e) { _log.LogWarning(e, "Failed to delete lock for {ReceiptId}", req.ReceiptId); }
        }
    }

    #region Helpers

    private static bool IsHeuristicsStrong(ParsedReceiptList heur, out string reason)
    {
        reason = "ok";
        if (heur.Items == null || heur.Items.Count == 0) { reason = "no_items"; return false; }

        foreach (var i in heur.Items)
        {
            if (i.Qty <= 0 || i.UnitPrice < 0 || i.UnitPrice > 10000)
            { reason = "bad_item_values"; return false; }
        }

        decimal sum = 0m;
        foreach (var i in heur.Items) sum += i.UnitPrice * i.Qty;
        sum = Round2(sum);

        if (heur.Subtotal is decimal s && Math.Abs(s - sum) > 0.02m)
        { reason = "sum_mismatch"; return false; }

        decimal? sub = heur.Subtotal ?? (heur.Items.Count > 0 ? sum : (decimal?)null);
        decimal? tax = heur.Tax;
        decimal? tip = heur.Tip;
        var computed = (sub ?? 0m) + (tax ?? 0m) + (tip ?? 0m);

        return (heur.Total is decimal t && t >= 0) || computed > 0m;
    }

    private static (List<ReplaceReceiptItemDto> items, int posted, int skippedNonItem, int skippedBad, int skippedEmpty)
        BuildItemsFromHeuristics(ParsedReceiptList heur, bool isLlm)
        => BuildItems(
            heur.Items.Select(i => (desc: i.Description ?? string.Empty, qty: (decimal)i.Qty, unitPrice: i.UnitPrice)),
            isLlm);

    private static (List<ReplaceReceiptItemDto> items, int posted, int skippedNonItem, int skippedBad, int skippedEmpty)
        BuildItemsFromParsed(ParsedReceiptV1 parsed, bool isLlm)
        => BuildItems(
            parsed.Items.Select(i => (desc: i.Description ?? string.Empty, qty: i.Quantity, unitPrice: i.UnitPrice)),
            isLlm);

    private static (List<ReplaceReceiptItemDto> items, int posted, int skippedNonItem, int skippedBad, int skippedEmpty)
        BuildItems(IEnumerable<(string desc, decimal qty, decimal unitPrice)> source, bool isLlm)
    {
        int posted = 0, skippedNonItem = 0, skippedBad = 0, skippedEmpty = 0;
        var list = new List<ReplaceReceiptItemDto>();

        foreach (var (desc, qtyRaw, unitPriceRaw) in source)
        {
            var label = CleanLabel(desc);

            if (string.IsNullOrWhiteSpace(label)) { skippedEmpty++; continue; }
            if (LooksLikeNonItem(label)) { skippedNonItem++; continue; }

            if (qtyRaw <= 0 || qtyRaw > 1000) { skippedBad++; continue; }
            if (unitPriceRaw < 0 || unitPriceRaw > 10000) { skippedBad++; continue; }

            var qty = qtyRaw;
            var unitPrice = Math.Round(unitPriceRaw, 2, MidpointRounding.AwayFromZero);
            var notes = isLlm ? "parsed:llm" : "parsed:heuristics";

            list.Add(new ReplaceReceiptItemDto(label, qty, unitPrice, notes));
            posted++;
        }

        return (list, posted, skippedNonItem, skippedBad, skippedEmpty);
    }

    private static bool IsTransientBlob(Exception ex)
        => ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429);

    private static bool IsTransientOcr(Exception ex) => IsTransientApi(ex);

    private static bool IsTransientApi(Exception ex)
        => (ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429))
           || ex is HttpRequestException;

    private static readonly Regex NonItemPhrase = new(
        @"\b(subtotal|sub\s*total|total(?!\s*wine)|amount\s*due|sales?\s*tax|tax|tip|gratuity|service(\s*fee)?|discount|promo|promotion|coupon|offer|save|spend|member|loyalty|rewards|bogo|%[\s-]*off|pre[-\s]?discount\s*subtotal|discount\s*total)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TrailingMinusOrParens = new(
        @"\(\s*\d+(?:[.,]\d+)?\s*\)|\d+(?:[.,]\d+)?\s*[-–—]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string CleanLabel(string s) => Regex.Replace(s ?? string.Empty, @"\s{2,}", " ").Trim();

    private static bool LooksLikeNonItem(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return true;
        var d = desc.ToLowerInvariant();
        if (NonItemPhrase.IsMatch(d)) return true;
        if (TrailingMinusOrParens.IsMatch(d)) return true;
        return false;
    }

    #endregion
}
