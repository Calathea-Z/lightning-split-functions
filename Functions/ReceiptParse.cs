using Functions.Contracts.Messages;
using Functions.Infrastructure.Logging;
using Functions.Services.Abstractions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Functions;

public class ReceiptParseFunction(
    ILogger<ReceiptParseFunction> _log,
    IReceiptParseOrchestrator _orchestrator
)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Function("ReceiptParse")]
    public async Task Run([QueueTrigger("receipt-parse")] string message, CancellationToken ct)
    {
        var req = Deserialize(message, _log);
        var rid = Guid.Parse(req.ReceiptId!);
        using var _scope = _log.WithReceiptScope(rid);

        try
        {
            await _orchestrator.RunAsync(req, ct);
            _log.Persisted(rid);
        }
        catch (Exception ex)
        {
            _log.ParseFailed(rid, ex.Message, ex);
            throw;
        }
    }

    private static ReceiptParseMessage Deserialize(string raw, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Empty queue message.");
        string s = raw.Trim();

        try
        {
            // If portal wrapped as a JSON string:  "\"{...}\""
            if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
                s = JsonSerializer.Deserialize<string>(s) ?? s;

            // Direct JSON?
            if (s.TrimStart().StartsWith("{"))
                return Validate(JsonSerializer.Deserialize<ReceiptParseMessage>(s, JsonOpts), "direct");

            // Base64 → JSON?
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s));
                if (decoded.TrimStart().StartsWith("{"))
                    return Validate(JsonSerializer.Deserialize<ReceiptParseMessage>(decoded, JsonOpts), "base64");
            }
            catch { /* not base64 */ }

            // URL-decoded JSON?
            var unescaped = Uri.UnescapeDataString(s);
            if (unescaped.TrimStart().StartsWith("{"))
                return Validate(JsonSerializer.Deserialize<ReceiptParseMessage>(unescaped, JsonOpts), "url");

            // Bare GUID? Tell the user what to post.
            if (Guid.TryParse(s, out _))
                throw new InvalidOperationException(
                    "Queue message must be JSON with keys ReceiptId, Container, Blob. " +
                    "Example: {\"ReceiptId\":\"<guid>\",\"Container\":\"receipts\",\"Blob\":\"<guid>/receipt.jpg\"}");

            throw new JsonException("Queue message must be a JSON object.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Deserialization failed. Raw head: {Head}", s[..Math.Min(200, s.Length)]);
            throw;
        }

        ReceiptParseMessage Validate(ReceiptParseMessage? req, string mode)
        {
            if (req is null) throw new InvalidOperationException($"Invalid payload ({mode}).");
            if (string.IsNullOrWhiteSpace(req.ReceiptId) || !Guid.TryParse(req.ReceiptId, out _))
                throw new InvalidOperationException($"Bad ReceiptId: {req.ReceiptId}");
            if (string.IsNullOrWhiteSpace(req.Container))
                throw new InvalidOperationException("Missing 'Container'.");
            if (string.IsNullOrWhiteSpace(req.Blob))
                throw new InvalidOperationException("Missing 'Blob'.");
            return req;
        }
    }
}
