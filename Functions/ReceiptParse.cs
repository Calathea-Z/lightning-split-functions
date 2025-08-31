using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Functions
{
    public class ReceiptParse
    {
        private readonly ILogger<ReceiptParse> _logger;

        public ReceiptParse(ILogger<ReceiptParse> logger)
        {
            _logger = logger;
        }

        public record ReceiptParseMessage(string Container, string Blob, string? ReceiptId);

        [Function("ReceiptParse")]
        public async Task Run(
            [QueueTrigger("receipt-parse")] string message)
        {
            // Base64 will already be decoded by the binding (see host.json below)
            var req = JsonSerializer.Deserialize<ReceiptParseMessage>(message)
                      ?? throw new InvalidOperationException("Invalid payload");

            var conn = Environment.GetEnvironmentVariable("AzureWebJobsStorage")
                      ?? throw new InvalidOperationException("Missing AzureWebJobsStorage");

            // Download the uploaded blob (Azurite or Azure)
            var blob = new BlobClient(conn, req.Container, req.Blob);
            using var ms = new MemoryStream();
            await blob.DownloadToAsync(ms);
            var bytes = ms.ToArray();

            _logger.LogInformation("Downloaded {len} bytes: {container}/{blob}",
                bytes.Length, req.Container, req.Blob);

            // TODO: OCR/parse; stub a result and emit to receipts-out (optional)
            var outQ = new QueueClient(conn, "receipts-out");
            await outQ.CreateIfNotExistsAsync();
            await outQ.SendMessageAsync(JsonSerializer.Serialize(new
            {
                req.ReceiptId,
                ok = true,
                total = 19.86m,
                at = DateTime.UtcNow
            }));
        }
    }
}