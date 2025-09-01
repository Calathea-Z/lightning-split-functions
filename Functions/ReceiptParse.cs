using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

public sealed record ReceiptParseMessage(string Container, string Blob, string? ReceiptId);
public sealed record UpdateTotalsDto(decimal? SubTotal, decimal? Tax, decimal? Tip, decimal? Total);

public class ReceiptParse(ILogger<ReceiptParse> _log, IHttpClientFactory http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Function("ReceiptParse")]
    public async Task Run([QueueTrigger("receipt-parse")] string message)
    {
        ReceiptParseMessage req;
        try
        {
            req = JsonSerializer.Deserialize<ReceiptParseMessage>(message, JsonOpts)
                  ?? throw new InvalidOperationException("Invalid payload");
            if (!Guid.TryParse(req.ReceiptId, out _))
                throw new InvalidOperationException($"Bad ReceiptId: {req.ReceiptId}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Deserialization failed. Raw message: {raw}", message);
            throw; // bad message -> let it poison
        }

        var rid = Guid.Parse(req.ReceiptId!);
        var conn = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                  ?? throw new InvalidOperationException("Missing AzureWebJobsStorage");

        try
        {
            // 1) Download blob (prove access)
            var service = new BlobServiceClient(conn);
            var blob = service.GetBlobContainerClient(req.Container).GetBlobClient(req.Blob);
            using var ms = new MemoryStream();
            await blob.DownloadToAsync(ms);
            _log.LogInformation("Downloaded {len} bytes for receipt {id}", ms.Length, rid);

            // Optional: force a failure for testing
            if (Environment.GetEnvironmentVariable("FAIL_PARSE") == "1")
                throw new InvalidOperationException("Forced failure for testing");

            // 2) OCR (stubbed)
            var parsed = new UpdateTotalsDto(18.23m, 1.63m, 0m, 19.86m);

            // 3) PATCH API
            var apiBase = Environment.GetEnvironmentVariable("ApiBaseUrl") ?? "http://localhost:5104";
            var client = http.CreateClient("api");
            client.BaseAddress = new Uri(apiBase);

            var resp = await client.PatchAsJsonAsync($"/api/receipts/{rid}/totals", parsed);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"PATCH totals failed {resp.StatusCode}: {body}");
            }

            _log.LogInformation("Updated totals for {id} to {total}", rid, parsed.Total);
        }
        catch (Exception ex)
        {
            // IMPORTANT: do not mark FailedParse here; let retries/poison handle it
            _log.LogError(ex, "Parse failed for receipt {id}", rid);
            throw; // keep retrying; poison if persistent
        }
    }
}
