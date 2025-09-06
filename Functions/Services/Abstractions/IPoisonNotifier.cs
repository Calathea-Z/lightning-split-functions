namespace Functions.Services.Abstractions;

public interface IPoisonNotifier
{
    Task NotifyAsync(Guid receiptId, string note, CancellationToken ct);
}