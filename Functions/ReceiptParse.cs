using Functions.Infrastructure.Logging;
using Functions.Models;
using Functions.Services;
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
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

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

    private static ReceiptParseMessage Deserialize(string raw, ILogger _log)
    {
        try
        {
            var req = JsonSerializer.Deserialize<ReceiptParseMessage>(raw, JsonOpts)
                      ?? throw new InvalidOperationException("Invalid payload");
            if (!Guid.TryParse(req.ReceiptId, out _))
                throw new InvalidOperationException($"Bad ReceiptId: {req.ReceiptId}");
            return req;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Deserialization failed. Raw: {Raw}", raw);
            throw;
        }
    }
}
