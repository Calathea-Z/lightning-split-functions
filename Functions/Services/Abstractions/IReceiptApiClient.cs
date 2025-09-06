using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Functions.Services.Abstractions
{
    public interface IReceiptApiClient
    {
        Task PatchRawTextAsync(Guid id, string text, CancellationToken ct);
        Task PatchTotalsAsync(Guid id, decimal? sub, decimal? tax, decimal? tip, decimal total, CancellationToken ct);
        Task PatchReviewAsync(Guid id, decimal? reconcileDelta, bool needsReview, CancellationToken ct);
        Task PatchStatusAsync(Guid id, string status, CancellationToken ct);
        Task PostItemAsync(Guid id, object dto, CancellationToken ct);
        Task PostParseErrorAsync(Guid receiptId, string note, CancellationToken ct);
    }
}
