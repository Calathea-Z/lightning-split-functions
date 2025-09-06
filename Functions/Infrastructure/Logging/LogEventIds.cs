using Microsoft.Extensions.Logging;

namespace Functions.Infrastructure.Logging
{
    public static class LogEventIds
    {
        public static readonly EventId ReceiptUploadStarted = new(1000, nameof(ReceiptUploadStarted));
        public static readonly EventId ReceiptBlobUploaded = new(1001, nameof(ReceiptBlobUploaded));
        public static readonly EventId ReceiptOcrRequested = new(1002, nameof(ReceiptOcrRequested));
        public static readonly EventId ReceiptParsed = new(1003, nameof(ReceiptParsed));
        public static readonly EventId ReceiptNeedsReview = new(1004, nameof(ReceiptNeedsReview));
        public static readonly EventId ReceiptPersisted = new(1005, nameof(ReceiptPersisted));

        public static readonly EventId BlobUploadFailed = new(2000, nameof(BlobUploadFailed));
        public static readonly EventId OcrFailed = new(2001, nameof(OcrFailed));
        public static readonly EventId ParseFailed = new(2002, nameof(ParseFailed));

        public static readonly EventId ApiCallStarted = new(3000, nameof(ApiCallStarted));
        public static readonly EventId ApiCallSucceeded = new(3001, nameof(ApiCallSucceeded));
        public static readonly EventId ApiCallFailed = new(3002, nameof(ApiCallFailed));
    }
}
