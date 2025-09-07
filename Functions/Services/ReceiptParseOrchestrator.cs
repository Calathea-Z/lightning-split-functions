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
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;
// alias for brevity
using ParseEngine = Api.Abstractions.Receipts.ParseEngine;

namespace Functions.Services;

public class ReceiptParseOrchestrator : IReceiptParseOrchestrator
{
    private readonly ILogger<IReceiptParseOrchestrator> _log;
    private readonly BlobServiceClient _blobSvc;
    private readonly IReceiptOcr _ocr;
    private readonly IImagePreprocessor _imagePrep;
    private readonly IReceiptApiClient _api;
    private readonly IReceiptNormalizer _normalizer;

    public ReceiptParseOrchestrator(
        ILogger<IReceiptParseOrchestrator> log,
        BlobServiceClient blobSvc,
        IReceiptOcr ocr,
        IImagePreprocessor imagePrep,
        IReceiptApiClient api,
        IReceiptNormalizer normalizer)
    {
        _log = log;
        _blobSvc = blobSvc;
        _ocr = ocr;
        _imagePrep = imagePrep;
        _api = api;
        _normalizer = normalizer;
    }

    private const long MaxBlobBytes = 50L * 1024 * 1024; // 50 MB
    private const string LockContainer = "receipt-parse-locks";

    // Resilience
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);
    private static decimal Round2(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

                // 3) OCR (each retry uses a NEW MemoryStream over the same bytes)
                _log.OcrRequested(rid, _ocr.GetType().Name);
                string rawText = await Retry.RetryAsync(
                    op: "ocr.read",
                    perAttemptTimeout: OcrTimeout,
                    action: ct2 =>
                    {
                        var fresh = new MemoryStream(preBytes, writable: false);
                        return _ocr.ReadAsync(fresh, ct2); // returns Task<string>
                    },
                    isTransient: IsTransientOcr,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct
);

                if (string.IsNullOrWhiteSpace(rawText))
                    _log.LogWarning("OCR returned empty text");

                // 4) Persist raw (idempotent) — DTO ctor
                await Retry.RetryAsync(
                    op: "api.patchRaw",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchRawTextAsync(rid, new UpdateRawTextRequest(rawText ?? string.Empty), ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                // 5) Heuristics (used for decision + fallback)
                var heur = HeuristicExtractor.Extract(rawText ?? string.Empty);
                _log.LogInformation("Heuristics found {Count} items", heur.Items.Count);
                if (!heur.IsSane || heur.Items.Count == 0)
                    _log.NeedsReview(rid, "Weak heuristic extraction");

                bool llmAttempted = false;
                bool? llmAccepted = null;
                string? llmModel = null;

                // 6) Decide: heuristics strong? If yes, skip LLM and finish fast
                if (IsHeuristicsStrong(heur, out var strongReason))
                {
                    _log.LogInformation("Skipping LLM: heuristics strong ({Reason})", strongReason);

                    // Items (with guard + counters)
                    int posted = 0, skippedNonItem = 0, skippedBad = 0, skippedEmpty = 0;
                    foreach (var it in heur.Items)
                    {
                        var desc = (it.Description ?? string.Empty).Trim();
                        if (desc.Length == 0) { skippedEmpty++; continue; }
                        if (string.Equals(desc, "Adjustment", StringComparison.OrdinalIgnoreCase)) { skippedNonItem++; continue; }
                        if (string.Equals(desc, "Discount/Adjustment", StringComparison.OrdinalIgnoreCase)) { skippedNonItem++; continue; }

                        var outcome = await TryPostItemAsync(rid, desc, it.Qty, it.UnitPrice, isLlm: false, ct);
                        switch (outcome)
                        {
                            case PostOutcome.Posted: posted++; break;
                            case PostOutcome.SkippedNonItem: skippedNonItem++; break;
                            case PostOutcome.SkippedBadValue: skippedBad++; break;
                            case PostOutcome.SkippedEmpty: skippedEmpty++; break;
                        }
                    }
                    _log.LogInformation("Post summary (heur-strong): posted={Posted}, skippedNonItem={NonItem}, skippedBad={Bad}, skippedEmpty={Empty}",
                        posted, skippedNonItem, skippedBad, skippedEmpty);

                    // Totals -> DTO ctor
                    var sumOfItems = heur.Items.Sum(i => i.UnitPrice * i.Qty);
                    decimal? subOpt = heur.Subtotal ?? (heur.Items.Count > 0 ? Round2(sumOfItems) : null);
                    decimal? taxOpt = heur.Tax;
                    decimal? tipOpt = heur.Tip;

                    var totalRounded = Round2(heur.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    decimal? subRounded = subOpt is null ? null : Round2(subOpt.Value);
                    decimal? taxRounded = taxOpt is null ? null : Round2(taxOpt.Value);
                    decimal? tipRounded = tipOpt is null ? null : Round2(tipOpt.Value);

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
                                 LlmAttempted: llmAttempted,
                                 LlmAccepted: llmAccepted,
                                 LlmModel: llmModel,
                                 ParserVersion: null,
                                 RejectReason: strongReason),
                             ct2),
                         isTransient: IsTransientApi,
                         maxAttempts: 3,
                         log: _log,
                         outer: ct);

                    _log.Parsed(rid, posted, subRounded ?? 0m, taxRounded ?? 0m, totalRounded);
                    return; // done
                }

                // 7) Heuristics weak -> set NeedsReview, then try LLM
                await Retry.RetryAsync(
                    op: "api.patchStatus.needsReview",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchStatusAsync(rid, new UpdateStatusRequest(ReceiptStatus.ParsedNeedsReview), ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                llmAttempted = true;

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

                    var parsedJson = await _normalizer.NormalizeAsync(rawText ?? string.Empty, hints, ct);
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
                        // If your normalizer exposes a model name, set it here:
                        // llmModel = _normalizer.ModelName;
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

                int finalPosted = 0, finalSkippedNonItem = 0, finalSkippedBad = 0, finalSkippedEmpty = 0;
                decimal? subRoundedFinal;
                decimal? taxRoundedFinal;
                decimal? tipRoundedFinal;
                decimal totalRoundedFinal;

                if (valid && parsed is not null)
                {
                    llmAccepted = true;

                    // LLM path
                    foreach (var it in parsed.Items)
                    {
                        var desc = (it.Description ?? string.Empty).Trim();
                        if (desc.Length == 0) { finalSkippedEmpty++; continue; }
                        if (string.Equals(desc, "Adjustment", StringComparison.OrdinalIgnoreCase)) { finalSkippedNonItem++; continue; }
                        if (string.Equals(desc, "Discount/Adjustment", StringComparison.OrdinalIgnoreCase)) { finalSkippedNonItem++; continue; }

                        var outcome = await TryPostItemAsync(rid, desc, it.Quantity, it.UnitPrice, isLlm: true, ct);
                        switch (outcome)
                        {
                            case PostOutcome.Posted: finalPosted++; break;
                            case PostOutcome.SkippedNonItem: finalSkippedNonItem++; break;
                            case PostOutcome.SkippedBadValue: finalSkippedBad++; break;
                            case PostOutcome.SkippedEmpty: finalSkippedEmpty++; break;
                        }
                    }
                    _log.LogInformation("Post summary (LLM): posted={Posted}, skippedNonItem={NonItem}, skippedBad={Bad}, skippedEmpty={Empty}",
                        finalPosted, finalSkippedNonItem, finalSkippedBad, finalSkippedEmpty);

                    var itemsSumLocal = parsed.Items.Sum(i => i.LineTotal);
                    var subOpt = parsed.SubTotal ?? (parsed.Items.Count > 0 ? Round2(itemsSumLocal) : (decimal?)null);
                    var taxOpt = parsed.Tax;
                    var tipOpt = parsed.Tip;

                    totalRoundedFinal = Round2(parsed.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    subRoundedFinal = subOpt is null ? null : Round2(subOpt.Value);
                    taxRoundedFinal = taxOpt is null ? null : Round2(taxOpt.Value);
                    tipRoundedFinal = tipOpt is null ? null : Round2(tipOpt.Value);

                    var totalsReq = new UpdateTotalsRequest(subRoundedFinal, taxRoundedFinal, tipRoundedFinal, totalRoundedFinal);

                    await Retry.RetryAsync(
                        op: "api.patchTotals",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, totalsReq, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    // Upgrade status to Parsed
                    await Retry.RetryAsync(
                        op: "api.patchStatus.upgrade",
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
                        op: "api.patchParseMeta.llm",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchParseMetaAsync(
                            rid,
                            new UpdateParseMetaRequest(
                                ParsedBy: ParseEngine.Llm,
                                LlmAttempted: llmAttempted,
                                LlmAccepted: llmAccepted,
                                LlmModel: llmModel,
                                ParserVersion: null,
                                RejectReason: null),
                            ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.Parsed(rid, finalPosted, subRoundedFinal ?? 0m, taxRoundedFinal ?? 0m, totalRoundedFinal);
                }
                else
                {
                    llmAccepted = false;

                    // Fallback: heuristics (keep status = ParsedNeedsReview)
                    foreach (var it in heur.Items)
                    {
                        var desc = (it.Description ?? string.Empty).Trim();
                        if (desc.Length == 0) { finalSkippedEmpty++; continue; }
                        if (string.Equals(desc, "Adjustment", StringComparison.OrdinalIgnoreCase)) { finalSkippedNonItem++; continue; }
                        if (string.Equals(desc, "Discount/Adjustment", StringComparison.OrdinalIgnoreCase)) { finalSkippedNonItem++; continue; }

                        var outcome = await TryPostItemAsync(rid, desc, it.Qty, it.UnitPrice, isLlm: false, ct);
                        switch (outcome)
                        {
                            case PostOutcome.Posted: finalPosted++; break;
                            case PostOutcome.SkippedNonItem: finalSkippedNonItem++; break;
                            case PostOutcome.SkippedBadValue: finalSkippedBad++; break;
                            case PostOutcome.SkippedEmpty: finalSkippedEmpty++; break;
                        }
                    }
                    _log.LogInformation("Post summary (heur-fallback): posted={Posted}, skippedNonItem={NonItem}, skippedBad={Bad}, skippedEmpty={Empty}",
                        finalPosted, finalSkippedNonItem, finalSkippedBad, finalSkippedEmpty);

                    var sumOfItems = heur.Items.Sum(i => i.UnitPrice * i.Qty);
                    decimal? subOpt = heur.Subtotal ?? (heur.Items.Count > 0 ? Round2(sumOfItems) : null);
                    decimal? taxOpt = heur.Tax;
                    decimal? tipOpt = heur.Tip;

                    totalRoundedFinal = Round2(heur.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    subRoundedFinal = subOpt is null ? null : Round2(subOpt.Value);
                    taxRoundedFinal = taxOpt is null ? null : Round2(taxOpt.Value);
                    tipRoundedFinal = tipOpt is null ? null : Round2(tipOpt.Value);

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
                                LlmAttempted: llmAttempted,
                                LlmAccepted: llmAccepted,
                                LlmModel: llmModel,
                                ParserVersion: null,
                                RejectReason: llmReason ?? "llm_rejected_or_invalid"),
                            ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.Parsed(rid, finalPosted, subRoundedFinal ?? 0m, taxRoundedFinal ?? 0m, totalRoundedFinal);
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

    // Gate for “heuristics strong enough to skip LLM?”
    private static bool IsHeuristicsStrong(ParsedReceiptList heur, out string reason)
    {
        reason = "ok";
        if (heur.Items == null || heur.Items.Count == 0) { reason = "no_items"; return false; }

        // validate items
        foreach (var i in heur.Items)
        {
            if (i.Qty <= 0 || i.UnitPrice < 0 || i.UnitPrice > 10000)
            { reason = "bad_item_values"; return false; }
        }

        // math check (rounded to 2dp to avoid penny drift)
        decimal sum = 0m;
        foreach (var i in heur.Items) sum += i.UnitPrice * i.Qty;
        sum = Round2(sum);

        if (heur.Subtotal is decimal s && Math.Abs(s - sum) > 0.02m)
        { reason = "sum_mismatch"; return false; }

        // total present or computable is “good enough”
        decimal? sub = heur.Subtotal ?? (heur.Items.Count > 0 ? sum : null);
        decimal? tax = heur.Tax;
        decimal? tip = heur.Tip;
        var computed = (sub ?? 0m) + (tax ?? 0m) + (tip ?? 0m);

        return (heur.Total is decimal t && t >= 0) || computed > 0m;
    }

    private enum PostOutcome { Posted, SkippedNonItem, SkippedBadValue, SkippedEmpty }

    // Returns outcome (posted vs skipped reason)
    private async Task<PostOutcome> TryPostItemAsync(Guid rid, string desc, decimal qty, decimal unitPrice, bool isLlm, CancellationToken ct)
    {
        var label = CleanLabel(desc);

        if (string.IsNullOrWhiteSpace(label)) return PostOutcome.SkippedEmpty;
        if (LooksLikeNonItem(label)) { _log.LogInformation("Skip item: non-item phrase '{Desc}'", label); return PostOutcome.SkippedNonItem; }

        if (qty <= 0 || qty > 1000) { _log.LogWarning("Skip item: bad qty {Qty} '{Desc}'", qty, label); return PostOutcome.SkippedBadValue; }
        if (unitPrice < 0 || unitPrice > 10000) { _log.LogWarning("Skip item: bad price {Price} '{Desc}'", unitPrice, label); return PostOutcome.SkippedBadValue; }

        // Normalize
        unitPrice = Math.Round(unitPrice, 2, MidpointRounding.AwayFromZero);

        var payload = new CreateReceiptItemRequest(Label: label, Qty: qty, UnitPrice: unitPrice)
        {
            Notes = isLlm ? "parsed:llm" : "parsed:heuristics"
        };

        await _api.PostItemAsync(rid, payload, ct);
        return PostOutcome.Posted;
    }

    private static bool IsTransientBlob(Exception ex)
        => ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429);

    private static bool IsTransientOcr(Exception ex) => IsTransientApi(ex);

    private static bool IsTransientApi(Exception ex)
        => (ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429))
           || ex is HttpRequestException;

    // Non-item guards for posting
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
        if (TrailingMinusOrParens.IsMatch(d)) return true; // e.g., "(5.16)" or "5.16-"
        return false;
    }

    #endregion
}
