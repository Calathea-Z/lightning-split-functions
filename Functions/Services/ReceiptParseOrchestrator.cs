using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Api.Abstractions.Receipts;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using Functions.Contracts.HeuristicExtractor;
using Functions.Contracts.Messages;
using Functions.Contracts.Parsing;
using Functions.Contracts.Receipts;
using Functions.Infrastructure.Logging;
using Functions.Infrastructure.Resilience;
using Functions.Services.Abstractions;
using Functions.Validation;

using Microsoft.Extensions.Logging;

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
                var rawText = await Retry.RetryAsync<string?>(
                    op: "ocr.read",
                    perAttemptTimeout: OcrTimeout,
                    action: ct2 =>
                    {
                        var fresh = new MemoryStream(preBytes, writable: false);
                        return _ocr.ReadAsync(fresh, ct2);
                    },
                    isTransient: IsTransientOcr,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct
                );
                if (string.IsNullOrWhiteSpace(rawText))
                    _log.LogWarning("OCR returned empty text");

                // 4) Persist raw (idempotent)
                await Retry.RetryAsync(
                    op: "api.patchRaw",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchRawTextAsync(rid, rawText ?? string.Empty, ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                // 5) Heuristics (used for decision + fallback)
                var heur = HeuristicExtractor.Extract(rawText ?? string.Empty);
                _log.LogInformation("Heuristics found {Count} items", heur.Items.Count);
                if (!heur.IsSane || heur.Items.Count == 0)
                    _log.NeedsReview(rid, "Weak heuristic extraction");

                // 6) Decide: heuristics strong? If yes, skip LLM and finish fast
                if (IsHeuristicsStrong(heur, out var strongReason))
                {
                    _log.LogInformation("Skipping LLM: heuristics strong ({Reason})", strongReason);

                    // Items (with guard)
                    var itemCount = 0;
                    foreach (var it in heur.Items)
                    {
                        var desc = (it.Description ?? string.Empty).Trim();
                        if (desc.Length == 0) continue;
                        if (string.Equals(desc, "Adjustment", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(desc, "Discount/Adjustment", StringComparison.OrdinalIgnoreCase)) continue;

                        if (await TryPostItemAsync(rid, desc, it.Qty, it.UnitPrice, ct)) itemCount++;
                    }

                    // Totals
                    var sumOfItems = heur.Items.Sum(i => i.UnitPrice * i.Qty);
                    decimal? subOpt = heur.Subtotal ?? (heur.Items.Count > 0 ? Round2(sumOfItems) : null);
                    decimal? taxOpt = heur.Tax;
                    decimal? tipOpt = heur.Tip;

                    var totalRounded = Round2(heur.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    decimal? subRounded = subOpt is null ? null : Round2(subOpt.Value);
                    decimal? taxRounded = taxOpt is null ? null : Round2(taxOpt.Value);
                    decimal? tipRounded = tipOpt is null ? null : Round2(tipOpt.Value);

                    await Retry.RetryAsync(
                        op: "api.patchTotals",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, subRounded, taxRounded, tipRounded, totalRounded, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchStatus",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchStatusAsync(rid, ReceiptStatus.Parsed.ToString(), ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchTotals.final",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, subRounded, taxRounded, tipRounded, totalRounded, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.Parsed(rid, itemCount, subRounded ?? 0m, taxRounded ?? 0m, totalRounded);
                    return; // done
                }

                // 7) Heuristics weak -> set NeedsReview, then try LLM
                await Retry.RetryAsync(
                    op: "api.patchStatus.needsReview",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchStatusAsync(rid, ReceiptStatus.ParsedNeedsReview.ToString(), ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                ParsedReceiptV1? parsed = null;
                bool valid = false;
                decimal itemsSum = 0m;

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
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Normalizer failed; will use heuristics fallback.");
                }

                int finalItemCount = 0;
                decimal? subRoundedFinal;
                decimal? taxRoundedFinal;
                decimal? tipRoundedFinal;
                decimal totalRoundedFinal;

                if (valid && parsed is not null)
                {
                    // LLM path
                    foreach (var it in parsed.Items)
                    {
                        var desc = (it.Description ?? string.Empty).Trim();
                        if (desc.Length == 0) continue;
                        if (string.Equals(desc, "Adjustment", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(desc, "Discount/Adjustment", StringComparison.OrdinalIgnoreCase)) continue;

                        if (await TryPostItemAsync(rid, desc, it.Quantity, it.UnitPrice, ct)) finalItemCount++;
                    }

                    var itemsSumLocal = parsed.Items.Sum(i => i.LineTotal);
                    var subOpt = parsed.SubTotal ?? (parsed.Items.Count > 0 ? Round2(itemsSumLocal) : (decimal?)null);
                    var taxOpt = parsed.Tax;
                    var tipOpt = parsed.Tip;

                    totalRoundedFinal = Round2(parsed.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    subRoundedFinal = subOpt is null ? null : Round2(subOpt.Value);
                    taxRoundedFinal = taxOpt is null ? null : Round2(taxOpt.Value);
                    tipRoundedFinal = tipOpt is null ? null : Round2(tipOpt.Value);

                    await Retry.RetryAsync(
                        op: "api.patchTotals",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, subRoundedFinal, taxRoundedFinal, tipRoundedFinal, totalRoundedFinal, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    // Upgrade status to Parsed
                    await Retry.RetryAsync(
                        op: "api.patchStatus.upgrade",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchStatusAsync(rid, ReceiptStatus.Parsed.ToString(), ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchTotals.final",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, subRoundedFinal, taxRoundedFinal, tipRoundedFinal, totalRoundedFinal, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.Parsed(rid, finalItemCount, subRoundedFinal ?? 0m, taxRoundedFinal ?? 0m, totalRoundedFinal);
                }
                else
                {
                    // Fallback: heuristics (keep status = ParsedNeedsReview)
                    foreach (var it in heur.Items)
                    {
                        var desc = (it.Description ?? string.Empty).Trim();
                        if (desc.Length == 0) continue;
                        if (string.Equals(desc, "Adjustment", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(desc, "Discount/Adjustment", StringComparison.OrdinalIgnoreCase)) continue;

                        if (await TryPostItemAsync(rid, desc, it.Qty, it.UnitPrice, ct)) finalItemCount++;
                    }

                    var sumOfItems = heur.Items.Sum(i => i.UnitPrice * i.Qty);
                    decimal? subOpt = heur.Subtotal ?? (heur.Items.Count > 0 ? Round2(sumOfItems) : null);
                    decimal? taxOpt = heur.Tax;
                    decimal? tipOpt = heur.Tip;

                    totalRoundedFinal = Round2(heur.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));
                    subRoundedFinal = subOpt is null ? null : Round2(subOpt.Value);
                    taxRoundedFinal = taxOpt is null ? null : Round2(taxOpt.Value);
                    tipRoundedFinal = tipOpt is null ? null : Round2(tipOpt.Value);

                    await Retry.RetryAsync(
                        op: "api.patchTotals",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, subRoundedFinal, taxRoundedFinal, tipRoundedFinal, totalRoundedFinal, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    await Retry.RetryAsync(
                        op: "api.patchTotals.final",
                        perAttemptTimeout: ApiTimeout,
                        action: ct2 => _api.PatchTotalsAsync(rid, subRoundedFinal, taxRoundedFinal, tipRoundedFinal, totalRoundedFinal, ct2),
                        isTransient: IsTransientApi,
                        maxAttempts: 3,
                        log: _log,
                        outer: ct);

                    _log.Parsed(rid, finalItemCount, subRoundedFinal ?? 0m, taxRoundedFinal ?? 0m, totalRoundedFinal);
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

        // math check
        decimal sum = 0m;
        foreach (var i in heur.Items) sum += i.UnitPrice * i.Qty;

        if (heur.Subtotal is decimal s && Math.Abs(s - sum) > 0.02m)
        { reason = "sum_mismatch"; return false; }

        // total present or computable is “good enough”
        decimal? sub = heur.Subtotal ?? (heur.Items.Count > 0 ? Math.Round(sum, 2, MidpointRounding.AwayFromZero) : null);
        decimal? tax = heur.Tax;
        decimal? tip = heur.Tip;
        var computed = (sub ?? 0m) + (tax ?? 0m) + (tip ?? 0m);

        return (heur.Total is decimal t && t >= 0) || computed > 0m;
    }

    // Returns true if posted, false if skipped
    private async Task<bool> TryPostItemAsync(Guid rid, string desc, decimal qty, decimal unitPrice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        if (qty <= 0 || qty > 1000) { _log.LogWarning("Skip item: bad qty {Qty} '{Desc}'", qty, desc); return false; }
        if (unitPrice < 0 || unitPrice > 10000) { _log.LogWarning("Skip item: bad price {Price} '{Desc}'", unitPrice, desc); return false; }

        unitPrice = Math.Round(unitPrice, 2, MidpointRounding.AwayFromZero);
        var payload = new CreateReceiptItemDto(Label: desc.Trim(), Qty: qty, UnitPrice: unitPrice);
        await _api.PostItemAsync(rid, payload, ct);
        return true;
    }

    private static bool IsTransientBlob(Exception ex)
        => ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429);

    private static bool IsTransientOcr(Exception ex) => IsTransientApi(ex);

    private static bool IsTransientApi(Exception ex)
        => (ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429))
           || ex is HttpRequestException;

    #endregion
}
