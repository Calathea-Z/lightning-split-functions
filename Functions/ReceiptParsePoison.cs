using Functions.Contracts.Messages;
using Functions.Services.Abstractions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Functions;

public sealed class ReceiptParsePoisonFunction(ILogger<ReceiptParsePoisonFunction> _log, IPoisonNotifier _notifier)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Function("ReceiptParsePoison")]
    public async Task Run([QueueTrigger("receipt-parse-poison")] string poisonMessage, CancellationToken ct)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<ReceiptParseMessage>(poisonMessage, JsonOpts);
            if (msg is null || !Guid.TryParse(msg.ReceiptId, out var rid))
            {
                _log.LogWarning("Poison message invalid. Raw(first256)={Raw}",
                    poisonMessage is { Length: > 256 } ? poisonMessage[..256] : poisonMessage);
                return;
            }

            await _notifier.NotifyAsync(rid, "Reached poison queue after retries.", ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Poison handler threw (swallowed to avoid re-dead-lettering).");
        }
    }
}
