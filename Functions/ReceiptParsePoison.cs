using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

public class ReceiptParsePoison(ILogger<ReceiptParsePoison> log, IHttpClientFactory http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Function("ReceiptParsePoison")]
    public async Task Run([QueueTrigger("receipt-parse-poison")] string poisonMessage)
    {
        ReceiptParseMessage? msg = null;
        try
        {
            msg = JsonSerializer.Deserialize<ReceiptParseMessage>(poisonMessage, JsonOpts);
            if (msg is null || string.IsNullOrWhiteSpace(msg.ReceiptId) || !Guid.TryParse(msg.ReceiptId, out var rid))
            {
                log.LogWarning("Poison message missing/invalid receipt id. Raw: {raw}", poisonMessage);
                return; // swallow: can't do anything
            }

            var baseUrl = Environment.GetEnvironmentVariable("ApiBaseUrl") ?? "http://localhost:5104";
            var client = http.CreateClient("api");
            client.BaseAddress = new Uri(baseUrl);

            var content = "Reached poison queue after retries.";
            using var resp = await client.PostAsJsonAsync($"/api/receipts/{rid}/parse-error", content);

            if (resp.IsSuccessStatusCode)
            {
                log.LogWarning("Marked receipt {rid} as FailedParse from poison queue.", rid);
                return;
            }

            // Not found: probably deleted receipt. Treat as benign and log.
            if ((int)resp.StatusCode == 404)
            {
                log.LogInformation("Receipt {rid} not found when marking FailedParse (likely deleted). Skipping.", rid);
                return;
            }

            // Other status: log details for investigation
            var body = await resp.Content.ReadAsStringAsync();
            log.LogError("Failed to mark receipt {rid} as FailedParse. Status={status} Body={body}", rid, resp.StatusCode, body);
            // swallow to avoid re-looping poison; alerts/AI should catch this
        }
        catch (Exception ex)
        {
            // Swallow to avoid poison-looping the poison handler itself
            log.LogError(ex, "Poison handler threw. Message={msg}", msg is null ? poisonMessage : JsonSerializer.Serialize(msg, JsonOpts));
        }
    }
}
