using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Functions.Services.Abstractions;

namespace Functions.Services;

public sealed class AzureVisionOcr : IReceiptOcr
{
    private readonly DocumentAnalysisClient _client;

    public AzureVisionOcr(string endpoint, string key)
    {
        _client = new DocumentAnalysisClient(new Uri(endpoint), new AzureKeyCredential(key));
    }

    public async Task<string> ReadAsync(Stream image, CancellationToken ct = default)
    {
        var op = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            "prebuilt-read",
            image,
            options: null,
            cancellationToken: ct);

        var doc = op.Value;
        if (doc is null || doc.Pages.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var page in doc.Pages)
        {
            foreach (var line in page.Lines)
                sb.AppendLine(line.Content);

            sb.AppendLine(); // page break
        }

        return sb.ToString();
    }
}
