namespace Functions.Services.Abstractions;
        
public interface IReceiptOcr
{
    Task<string> ReadAsync(Stream image, CancellationToken ct);
}