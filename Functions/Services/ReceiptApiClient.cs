using Api.Abstractions.Receipts;
using Api.Abstractions.Transport;
using Functions.Services.Abstractions;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Functions.Services;

public sealed class ReceiptApiClient(IHttpClientFactory http, ILogger<ReceiptApiClient> log, Uri baseAddress) : IReceiptApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private HttpClient New()
    {
        var c = http.CreateClient("api");
        if (c.BaseAddress is null) c.BaseAddress = baseAddress;
        return c;
    }

    public Task PostItemAsync(Guid id, CreateReceiptItemRequest request, CancellationToken ct = default) =>
        Send(c => c.PostAsJsonAsync($"/api/receipts/{id:D}/items", request, JsonOpts, ct),
             "POST", $"/api/receipts/{id:D}/items", id, ct,
             throwOnError: false,  // do not blow up on a single bad line-item
             allowRetry: false);   // avoid duping items on retry

    public Task PatchTotalsAsync(Guid id, UpdateTotalsRequest request, CancellationToken ct = default) =>
        PatchOrThrow($"/api/receipts/{id:D}/totals", request, ct);

    public Task PatchStatusAsync(Guid id, UpdateStatusRequest request, CancellationToken ct = default) =>
        PatchOrThrow($"/api/receipts/{id:D}/status", request, ct);

    public Task PatchRawTextAsync(Guid id, UpdateRawTextRequest request, CancellationToken ct = default) =>
        PatchOrThrow($"/api/receipts/{id:D}/rawtext", request, ct);

    public Task PatchParseMetaAsync(Guid id, UpdateParseMetaRequest request, CancellationToken ct = default) =>
        PatchOrThrow($"/api/receipts/{id:D}/parse-meta", request, ct);

    public async Task PostParseErrorAsync(Guid receiptId, string note, CancellationToken ct = default)
    {
        // 1) Best-effort: set status to FailedParse
        try
        {
            await PatchStatusAsync(receiptId, new UpdateStatusRequest(ReceiptStatus.FailedParse), ct);
        }
        catch (Exception ex)
        {
            // Don't explode on any error - this is a best-effort operation
            log.LogWarning(ex, "Failed to mark receipt {ReceiptId} as FailedParse", receiptId);
        }

        // 2) Attach reject reason to parse-meta (non-fatal if it fails)
        try
        {
            var meta = new UpdateParseMetaRequest(
                ParsedBy: ParseEngine.Heuristics,   // source unknown; heuristics is a safe default
                LlmAttempted: false,
                LlmAccepted: null,
                LlmModel: null,
                ParserVersion: null,
                RejectReason: note);

            await PatchParseMetaAsync(receiptId, meta, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to patch parse-meta reject reason for {ReceiptId}", receiptId);
        }
    }

    #region Helpers 
    private async Task PatchOrThrow(string path, object dto, CancellationToken ct)
    {
        using var _ = await Send(c => c.PatchAsJsonAsync(path, dto, JsonOpts, ct),
                                 "PATCH", path, Guid.Empty, ct,
                                 throwOnError: true,
                                 allowRetry: true);
    }

    private static readonly TimeSpan[] Backoff = {
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1200)
    };

    private static bool IsTransient(int s) => s == 408 || (s >= 500 && s != 501 && s != 505);

    private async Task<HttpResponseMessage> Send(
        Func<HttpClient, Task<HttpResponseMessage>> call,
        string op,
        string path,
        Guid rid,
        CancellationToken ct,
        bool throwOnError,
        bool allowRetry = true)
    {
        HttpResponseMessage? last = null;

        for (int i = 0; i <= (allowRetry ? Backoff.Length : 0); i++)
        {
            try
            {
                last = await call(New());
                if ((int)last.StatusCode < 400 || !IsTransient((int)last.StatusCode) || !allowRetry) break;

                log.LogWarning("{Op} transient {Status} for {Path} rid={Rid} attempt={Attempt}",
                    op, (int)last.StatusCode, path, rid, i + 1);
            }
            catch (HttpRequestException ex) when (i < Backoff.Length && allowRetry)
            {
                log.LogWarning(ex, "{Op} network error for {Path} rid={Rid} attempt={Attempt}",
                    op, path, rid, i + 1);
            }

            if (i < Backoff.Length && allowRetry) await Task.Delay(Backoff[i], ct);
        }

        if (last is null) throw new InvalidOperationException("No response from HTTP call.");

        if (!last.IsSuccessStatusCode)
        {
            var body = await SafeReadBodyAsync(last, ct);
            if (throwOnError)
                throw new InvalidOperationException($"{op} {path} failed {(int)last.StatusCode}: {body}");

            log.LogWarning("{Op} {Path} failed {Status}: {Body}", op, path, (int)last.StatusCode, Truncate(body, 512));
        }

        return last;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s.Substring(0, max);
    }

    #endregion
}
