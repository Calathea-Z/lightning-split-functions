using Microsoft.Extensions.Logging;

namespace Functions.Infrastructure.Logging
{
    public static partial class ReceiptLogs
    {
        static readonly Action<ILogger, Guid, string?, Exception?> _uploadStarted =
            LoggerMessage.Define<Guid, string?>(
                LogLevel.Information, LogEventIds.ReceiptUploadStarted,
                "Uploading receipt {ReceiptId} (ContentType: {ContentType})");

        static readonly Action<ILogger, Guid, string, long, Exception?> _blobUploaded =
            LoggerMessage.Define<Guid, string, long>(
                LogLevel.Information, LogEventIds.ReceiptBlobUploaded,
                "Uploaded blob for {ReceiptId} (BlobName: {BlobName}, Bytes: {SizeBytes})");

        static readonly Action<ILogger, Guid, string, Exception?> _ocrRequested =
            LoggerMessage.Define<Guid, string>(
                LogLevel.Information, LogEventIds.ReceiptOcrRequested,
                "Requested OCR for {ReceiptId} (Engine: {Engine})");

        static readonly Action<ILogger, Guid, int, decimal, decimal, decimal, Exception?> _parsed =
            LoggerMessage.Define<Guid, int, decimal, decimal, decimal>(
                LogLevel.Information, LogEventIds.ReceiptParsed,
                "Parsed {ReceiptId} (Items: {ItemCount}, Subtotal: {SubTotal}, Tax: {Tax}, Total: {Total})");

        static readonly Action<ILogger, Guid, string?, Exception?> _needsReview =
            LoggerMessage.Define<Guid, string?>(
                LogLevel.Warning, LogEventIds.ReceiptNeedsReview,
                "{ReceiptId} needs review (Reason: {Reason})");

        static readonly Action<ILogger, Guid, Exception?> _persisted =
            LoggerMessage.Define<Guid>(
                LogLevel.Information, LogEventIds.ReceiptPersisted,
                "Persisted {ReceiptId}");

        static readonly Action<ILogger, Guid, string, Exception?> _blobFailed =
            LoggerMessage.Define<Guid, string>(
                LogLevel.Error, LogEventIds.BlobUploadFailed,
                "Blob upload failed for {ReceiptId} (Message: {Error})");

        static readonly Action<ILogger, Guid, string, Exception?> _ocrFailed =
            LoggerMessage.Define<Guid, string>(
                LogLevel.Error, LogEventIds.OcrFailed,
                "OCR failed for {ReceiptId} (Message: {Error})");

        static readonly Action<ILogger, Guid, string, Exception?> _parseFailed =
            LoggerMessage.Define<Guid, string>(
                LogLevel.Error, LogEventIds.ParseFailed,
                "Parse failed for {ReceiptId} (Message: {Error})");

        // Extension helpers (read nicely at call sites)
        public static IDisposable WithReceiptScope(this ILogger logger, Guid receiptId) =>
            logger.BeginScope(new KeyValuePair<string, object>("ReceiptId", receiptId));

        public static void UploadStarted(this ILogger l, Guid id, string? contentType) =>
            _uploadStarted(l, id, contentType, null);

        public static void BlobUploaded(this ILogger l, Guid id, string blobName, long size) =>
            _blobUploaded(l, id, blobName, size, null);

        public static void OcrRequested(this ILogger l, Guid id, string engine) =>
            _ocrRequested(l, id, engine, null);

        public static void Parsed(this ILogger l, Guid id, int items, decimal sub, decimal tax, decimal total) =>
            _parsed(l, id, items, sub, tax, total, null);

        public static void NeedsReview(this ILogger l, Guid id, string? reason) =>
            _needsReview(l, id, reason, null);

        public static void Persisted(this ILogger l, Guid id) =>
            _persisted(l, id, null);

        public static void BlobFailed(this ILogger l, Guid id, string msg, Exception ex) =>
            _blobFailed(l, id, msg, ex);

        public static void OcrFailed(this ILogger l, Guid id, string msg, Exception ex) =>
            _ocrFailed(l, id, msg, ex);

        public static void ParseFailed(this ILogger l, Guid id, string msg, Exception ex) =>
            _parseFailed(l, id, msg, ex);
    }
}
