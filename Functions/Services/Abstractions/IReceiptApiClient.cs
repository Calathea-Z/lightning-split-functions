using Api.Abstractions.Transport;

namespace Functions.Services.Abstractions
{
    public interface IReceiptApiClient
    {
        Task PostItemAsync(Guid receiptId, CreateReceiptItemRequest request, CancellationToken ct = default);
        Task PatchTotalsAsync(Guid receiptId, UpdateTotalsRequest request, CancellationToken ct = default);
        Task PatchStatusAsync(Guid receiptId, UpdateStatusRequest request, CancellationToken ct = default);
        Task PatchRawTextAsync(Guid receiptId, UpdateRawTextRequest request, CancellationToken ct = default);
        Task PatchParseMetaAsync(Guid receiptId, UpdateParseMetaRequest request, CancellationToken ct = default);
        Task PostParseErrorAsync(Guid receiptId, string note, CancellationToken ct = default);
    }
}
