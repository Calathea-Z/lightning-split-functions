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
using System.Diagnostics;

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
    private const int MaxAttempts = 4;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(250);
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
                "blob.getProperties",
                ShortTimeout,
                () => blobClient.GetPropertiesAsync(cancellationToken: ct),
                IsTransientBlob);

            if (props.Value.ContentLength > MaxBlobBytes)
                throw new InvalidOperationException($"Blob too large ({props.Value.ContentLength} bytes). Max {MaxBlobBytes}.");

            var original = await RetryAsync<Stream>(
                "blob.openRead",
                ShortTimeout,
                () => blobClient.OpenReadAsync(cancellationToken: ct),
                IsTransientBlob);

            await using (original)
            {
                _log.BlobUploaded(rid, $"{req.Container}/{req.Blob}", props.Value.ContentLength);

                // 2) Preprocess
                await using var pre = await _imagePrep.PrepareAsync(original, ct);

                // 3) OCR
                _log.OcrRequested(rid, _ocr.GetType().Name);
                var rawText = await RetryAsync<string?>("ocr.read", OcrTimeout, () => _ocr.ReadAsync(pre, ct), IsTransientOcr);
                if (string.IsNullOrWhiteSpace(rawText))
                    _log.LogWarning("OCR returned empty text");

                // 4) Persist raw (idempotent)
                await RetryAsync("api.patchRaw", ApiTimeout, () => _api.PatchRawTextAsync(rid, rawText ?? string.Empty, ct), IsTransientApi);

                // 5) Heuristics
                var parsed = HeuristicExtractor.Extract(rawText ?? string.Empty);
                _log.LogInformation("Heuristics found {Count} items", parsed.Items.Count);
                if (!parsed.IsSane || parsed.Items.Count == 0)
                    _log.NeedsReview(rid, "Weak heuristic extraction");

                // 6) Create items (no retries to avoid dupes)
                foreach (var it in parsed.Items)
                {
                    var payload = new CreateReceiptItemDto(Label: it.Description, Qty: it.Qty, UnitPrice: it.UnitPrice);
                    await _api.PostItemAsync(rid, payload, ct);
                }

                // 7) Auto-adjustment + totals
                var sumOfItems = parsed.Items.Sum(i => i.UnitPrice * i.Qty);
                var declaredTotal = parsed.Total ?? 0m;
                var delta = Math.Round(declaredTotal - sumOfItems, 2, MidpointRounding.AwayFromZero);
                var allow = Math.Max(1.00m, Math.Round(declaredTotal * 0.03m, 2, MidpointRounding.AwayFromZero));

                if (Math.Abs(delta) > allow && declaredTotal > 0)
                {
                    await _api.PostItemAsync(rid, new CreateReceiptItemDto(
                        Label: delta < 0 ? "Discount/Adjustment" : "Adjustment",
                        Qty: 1m,
                        UnitPrice: Math.Round(delta, 2, MidpointRounding.AwayFromZero),
                        Unit: null,
                        Category: "Adjustment",
                        Notes: "Auto-reconcile",
                        Position: parsed.Items.Count + 1
                    ), ct);
                    sumOfItems += delta;
                }

                var sub = parsed.Subtotal ?? 0m;
                var tax = parsed.Tax;
                var tip = parsed.Tip;
                var total = parsed.Total ?? 0m;

                var norm = ReceiptTotalsSanitizer.Normalize(sub, tax, tip, total, sumOfItems);
                var residual = Math.Abs(sumOfItems - norm.total);
                var hardAllow = Math.Max(1.00m, Math.Round(norm.total * 0.05m, 2, MidpointRounding.AwayFromZero));
                if (residual > hardAllow)
                    norm = (Round2(sumOfItems), norm.tax, norm.tip, Round2(sumOfItems + (norm.tax ?? 0m) + (norm.tip ?? 0m)));

                await RetryAsync("api.patchTotals", ApiTimeout,
                    () => _api.PatchTotalsAsync(rid, norm.sub, norm.tax, norm.tip, norm.total, ct), IsTransientApi);

                var needsReview = Math.Abs(delta) > allow && declaredTotal > 0m;
                await RetryAsync("api.patchReview", ApiTimeout,
                    () => _api.PatchReviewAsync(rid, delta, needsReview, ct), IsTransientApi);

                _log.Parsed(rid, parsed.Items.Count, norm.sub, norm.tax ?? 0m, norm.total);
                if (needsReview) _log.NeedsReview(rid, "Delta exceeded threshold");

                // 8) Final status
                var final = parsed.Items.Count > 0
                    ? (needsReview ? ReceiptStatus.ParsedNeedsReview : ReceiptStatus.Parsed)
                    : ReceiptStatus.Parsed;

                await RetryAsync("api.patchStatus", ApiTimeout,
                    () => _api.PatchStatusAsync(rid, final.ToString(), ct), IsTransientApi);
            }
        }
        finally
        {
            try { await lockBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct); }
            catch (Exception e) { _log.LogWarning(e, "Failed to delete lock for {ReceiptId}", req.ReceiptId); }
        }
    }

    #region Helpers
    private async Task<T> RetryAsync<T>(string op, TimeSpan perAttemptTimeout, Func<Task<T>> action, Func<Exception, bool> isTransient)
    {
        var sw = Stopwatch.StartNew();
        var attempt = 0;
        Exception? last = null;

        while (attempt < MaxAttempts)
        {
            attempt++;
            try
            {
                var result = await action().WaitAsync(perAttemptTimeout);
                if (attempt > 1) _log.LogInformation("{Op} succeeded on attempt {Attempt} after {Elapsed}ms", op, attempt, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                if (attempt >= MaxAttempts || !isTransient(ex))
                {
                    _log.LogError(ex, "{Op} failed on attempt {Attempt}/{Max}. Elapsed {Elapsed}ms", op, attempt, MaxAttempts, sw.ElapsedMilliseconds);
                    throw;
                }

                var delay = JitterDelay(attempt);
                _log.LogWarning(ex, "{Op} transient error on attempt {Attempt}. Retrying in {Delay}ms…", op, attempt, (int)delay.TotalMilliseconds);
                await Task.Delay(delay);
            }
        }

        throw new InvalidOperationException("RetryAsync<T> should not reach here.", last);
    }

    private async Task RetryAsync(string op, TimeSpan perAttemptTimeout, Func<Task> action, Func<Exception, bool> isTransient)
        => await RetryAsync<object?>(op, perAttemptTimeout, async () => { await action(); return null; }, isTransient);

    private static TimeSpan JitterDelay(int attempt)
    {
        var max = (int)(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        var min = max / 2;
        var ms = Random.Shared.Next(min, Math.Max(min + 1, max));
        return TimeSpan.FromMilliseconds(ms);
    }

    private static bool IsTransientBlob(Exception ex)
        => ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429);

    private static bool IsTransientOcr(Exception ex) => IsTransientApi(ex);

    private static bool IsTransientApi(Exception ex)
        => (ex is RequestFailedException rfe && (rfe.Status >= 500 || rfe.Status == 408 || rfe.Status == 429))
           || ex is HttpRequestException;

    #endregion
}
