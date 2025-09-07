using Functions.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace Functions.Services;

public sealed class PoisonNotifier(IReceiptApiClient _api, ILogger<PoisonNotifier> _log) : IPoisonNotifier
{
    public async Task NotifyAsync(Guid receiptId, string note, CancellationToken ct)
    {
        try
        {
            await _api.PostParseErrorAsync(receiptId, note, ct); 
            _log.LogWarning("Marked receipt {ReceiptId} as FailedParse from poison queue.", receiptId);
        }
        catch (HttpRequestException ex)
        {
            _log.LogError(ex, "Failed to notify parse error for {ReceiptId}", receiptId);
        }
    }
}