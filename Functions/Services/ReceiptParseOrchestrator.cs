using Api.Abstractions.Receipts;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Functions.Dtos.Receipts;
using Functions.Helpers;
using Functions.Infrastructure.Logging;
using Functions.Models;
using Functions.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace Functions.Services;

public class ReceiptParseOrchestrator : IReceiptParseOrchestrator
{
    private readonly ILogger<IReceiptParseOrchestrator> _log;
    private readonly BlobServiceClient _blobSvc;
    private readonly IReceiptOcr _ocr;
    private readonly IImagePreprocessor _imagePrep;
    private readonly IReceiptApiClient _api;

    public ReceiptParseOrchestrator(
        ILogger<IReceiptParseOrchestrator> log,
        BlobServiceClient blobSvc,
        IReceiptOcr ocr,
        IImagePreprocessor imagePrep,
        IReceiptApiClient api)
    {
        _log = log;
        _blobSvc = blobSvc;
        _ocr = ocr;
        _imagePrep = imagePrep;
        _api = api;
    }

    private const long MaxBlobBytes = 50L * 1024 * 1024; // 50 MB
    private const string LockContainer = "receipt-parse-locks";

    // Resilience
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(10);
    private static decimal Round2(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

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

            var props = await RetryAsync<Response<BlobProperties>>(
                op: "blob.getProperties",
                perAttemptTimeout: ShortTimeout,
                action: ct2 => blobClient.GetPropertiesAsync(cancellationToken: ct2),
                isTransient: IsTransientBlob,
                maxAttempts: 3,
                log: _log,
                outer: ct);

            if (props.Value.ContentLength > 50L * 1024 * 1024)
                throw new InvalidOperationException($"Blob too large ({props.Value.ContentLength} bytes). Max {50L * 1024 * 1024}.");

            var original = await RetryAsync<Stream>(
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
                var rawText = await RetryAsync<string?>(
                    op: "ocr.read",
                    perAttemptTimeout: OcrTimeout,
                    action: ct2 =>
                    {
                        // fresh, non-disposed stream per attempt
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
                await RetryAsync(
                    op: "api.patchRaw",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchRawTextAsync(rid, rawText ?? string.Empty, ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                // 5) Heuristics
                var parsed = HeuristicExtractor.Extract(rawText ?? string.Empty);
                _log.LogInformation("Heuristics found {Count} items", parsed.Items.Count);
                if (!parsed.IsSane || parsed.Items.Count == 0)
                    _log.NeedsReview(rid, "Weak heuristic extraction");

                // 6) Create items (skip any adjustment-like lines; API owns that)
                foreach (var it in parsed.Items)
                {
                    var desc = (it.Description ?? string.Empty).Trim();
                    if (desc.Length == 0) continue;
                    if (string.Equals(desc, "Adjustment", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(desc, "Discount/Adjustment", StringComparison.OrdinalIgnoreCase)) continue;

                    var payload = new CreateReceiptItemDto(Label: desc, Qty: it.Qty, UnitPrice: it.UnitPrice);
                    await _api.PostItemAsync(rid, payload, ct);
                }

                // 7) Build totals for API call (ensure TOTAL is non-nullable decimal)
                var sumOfItems = parsed.Items.Sum(i => i.UnitPrice * i.Qty);

                decimal? subOpt = parsed.Subtotal;
                decimal? taxOpt = parsed.Tax;
                decimal? tipOpt = parsed.Tip;

                if (subOpt is null && parsed.Items.Count > 0)
                    subOpt = Round2(sumOfItems);

                // Prefer printed total if present; else compute
                var totalRounded = Round2(parsed.Total ?? ((subOpt ?? 0m) + (taxOpt ?? 0m) + (tipOpt ?? 0m)));

                // Round nullable pieces
                decimal? subRounded = subOpt is null ? null : Round2(subOpt.Value);
                decimal? taxRounded = taxOpt is null ? null : Round2(taxOpt.Value);
                decimal? tipRounded = tipOpt is null ? null : Round2(tipOpt.Value);

                await RetryAsync(
                    op: "api.patchTotals",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchTotalsAsync(rid, subRounded, taxRounded, tipRounded, totalRounded, ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                // 8) Flip out of PendingParse, then re-patch totals to trigger final reconcile
                await RetryAsync(
                    op: "api.patchStatus",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchStatusAsync(rid, ReceiptStatus.Parsed.ToString(), ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                await RetryAsync(
                    op: "api.patchTotals.final",
                    perAttemptTimeout: ApiTimeout,
                    action: ct2 => _api.PatchTotalsAsync(rid, subRounded, taxRounded, tipRounded, totalRounded, ct2),
                    isTransient: IsTransientApi,
                    maxAttempts: 3,
                    log: _log,
                    outer: ct);

                _log.Parsed(rid, parsed.Items.Count, subRounded ?? 0m, taxRounded ?? 0m, totalRounded);
            }
        }
        finally
        {
            try { await lockBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct); }
            catch (Exception e) { _log.LogWarning(e, "Failed to delete lock for {ReceiptId}", req.ReceiptId); }
        }
    }

    #region Helpers
    public static async Task<T> RetryAsync<T>(
        string op,
        TimeSpan perAttemptTimeout,
        Func<CancellationToken, Task<T>> action,
        Func<Exception, bool> isTransient,
        int maxAttempts = 3,
        ILogger? log = null,
        CancellationToken outer = default)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            attemptCts.CancelAfter(perAttemptTimeout);

            try
            {
                log?.LogInformation("{Op}: attempt {Attempt}/{Max} (timeout {Timeout}ms)", op, attempt, maxAttempts, perAttemptTimeout.TotalMilliseconds);
                var result = await action(attemptCts.Token);
                return result;
            }
            catch (Exception ex) when (attempt < maxAttempts && isTransient(ex))
            {
                log?.LogWarning(ex, "{Op}: transient failure on attempt {Attempt}; retrying…", op, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), outer);
                continue;
            }
            catch (OperationCanceledException oce) when (attemptCts.IsCancellationRequested && !outer.IsCancellationRequested)
            {
                if (attempt < maxAttempts)
                {
                    log?.LogWarning("{Op}: attempt {Attempt} timed out after {Timeout}ms; retrying…", op, attempt, perAttemptTimeout.TotalMilliseconds);
                    continue;
                }
                throw new TimeoutException($"Operation '{op}' timed out after {perAttemptTimeout} (attempt {attempt}).", oce);
            }
        }
        throw new TimeoutException($"Operation '{op}' exhausted retries.");
    }

    private Task RetryAsync(
        string op,
        TimeSpan perAttemptTimeout,
        Func<CancellationToken, Task> action,
        Func<Exception, bool> isTransient,
        int maxAttempts = 3,
        ILogger? log = null,
        CancellationToken outer = default)
        => RetryAsync<object?>(
            op,
            perAttemptTimeout,
            async ct2 => { await action(ct2); return null; },
            isTransient,
            maxAttempts,
            log,
            outer);

    private static bool IsTransientBlob(Exception ex)
        => ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429);

    private static bool IsTransientOcr(Exception ex) => IsTransientApi(ex);

    private static bool IsTransientApi(Exception ex)
        => (ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429))
           || ex is HttpRequestException;
    #endregion
}
