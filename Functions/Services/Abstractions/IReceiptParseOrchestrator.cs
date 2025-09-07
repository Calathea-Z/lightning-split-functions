using Functions.Contracts.Messages;

namespace Functions.Services.Abstractions
{

    public interface IReceiptParseOrchestrator
    {
        /// <summary>
        /// Executes the full receipt parsing workflow:
        ///  - Acquire idempotency lock
        ///  - Fetch blob + guard size
        ///  - Preprocess + OCR
        ///  - Persist raw text
        ///  - Heuristic extraction + item creation
        ///  - Auto-reconciliation + totals
        ///  - Review flags + final status
        /// </summary>
        /// <param name="req">The message payload for the parse request.</param>
        /// <param name="ct">Cancellation token provided by the host.</param>
        Task RunAsync(ReceiptParseMessage req, CancellationToken ct);
    }
}
