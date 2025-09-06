using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Functions.Services.Abstractions;
using Microsoft.Extensions.Logging;

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

    public Task PatchRawTextAsync(Guid id, string text, CancellationToken ct) =>
        PatchOrThrow($"/api/receipts/{id}/rawtext", new { rawText = text }, ct);

    public Task PatchTotalsAsync(Guid id, decimal? sub, decimal? tax, decimal? tip, decimal total, CancellationToken ct) =>
        PatchOrThrow($"/api/receipts/{id}/totals", new { subTotal = sub, tax, tip, total }, ct);

    public Task PatchReviewAsync(Guid id, decimal? reconcileDelta, bool needsReview, CancellationToken ct) =>
        PatchOrThrow($"/api/receipts/{id}/review", new { ReconcileDelta = reconcileDelta, NeedsReview = needsReview }, ct);

    public Task PatchStatusAsync(Guid id, string status, CancellationToken ct) =>
        PatchOrThrow($"/api/receipts/{id}/status", new { status }, ct);

    public Task PostItemAsync(Guid id, object dto, CancellationToken ct) =>
        Send(c => c.PostAsJsonAsync($"/api/receipts/{id}/items", dto, JsonOpts, ct),
             "POST", $"/api/receipts/{id}/items", id, ct, throwOnError: false, allowRetry: false);

    public async Task PostParseErrorAsync(Guid receiptId, string note, CancellationToken ct)
    {
        var url = $"/api/receipts/{receiptId}/parse-error";
        using var resp = await New().PostAsJsonAsync(url, note, JsonOpts, ct);

        if (resp.IsSuccessStatusCode) return;

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            log.LogInformation("Receipt {ReceiptId} not found when marking FailedParse.", receiptId);
            return;
        }

        var body = await SafeReadBodyAsync(resp, ct);
        log.LogError("Failed to mark receipt {ReceiptId} as FailedParse. Status={Status} Body(first512)={Body}",
            receiptId, (int)resp.StatusCode, Truncate(body, 512));
    }

    // ---------- Private ----------
    private async Task PatchOrThrow(string path, object dto, CancellationToken ct)
    {
        using var resp = await Send(c => c.PatchAsJsonAsync(path, dto, JsonOpts, ct),
                                    "PATCH", path, Guid.Empty, ct, throwOnError: true, allowRetry: true);
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
            var body = await last.Content.ReadAsStringAsync(ct);
            if (throwOnError)
                throw new InvalidOperationException($"{op} {path} failed {(int)last.StatusCode}: {body}");

            log.LogWarning("{Op} {Path} failed {Status}: {Body}", op, path, (int)last.StatusCode, body);
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
}
