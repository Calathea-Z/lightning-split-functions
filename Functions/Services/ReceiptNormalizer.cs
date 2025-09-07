using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Functions.Services.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Functions.Services;

public sealed class AoaiReceiptNormalizerHttp : IReceiptNormalizer
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _endpoint;
    private readonly string _deployment;
    private readonly string _apiKey;
    private readonly string _apiVersion;
    private readonly string _schemaJson;
    private readonly string _systemPrompt;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AoaiReceiptNormalizerHttp(IHttpClientFactory httpFactory, IConfiguration cfg)
    {
        _httpFactory = httpFactory;
        _endpoint = (cfg["AOAI_ENDPOINT"] ?? throw new InvalidOperationException("AOAI_ENDPOINT missing")).TrimEnd('/');
        _deployment = cfg["AOAI_DEPLOYMENT"] ?? throw new InvalidOperationException("AOAI_DEPLOYMENT missing");
        _apiKey = cfg["AOAI_KEY"] ?? throw new InvalidOperationException("AOAI_KEY missing");
        _apiVersion = cfg["AOAI_API_VERSION"] ?? "2025-03-01-preview"; // supports JSON mode

        _schemaJson = """
        {
          "version": "parsed-receipt-v1",
          "merchant": { "name": null, "address": null, "phone": null },
          "datetime": null,
          "currency": "USD",
          "items": [ { "description": "string", "quantity": 1, "unitPrice": 0.00, "lineTotal": 0.00, "notes": null } ],
          "subTotal": null,
          "tax": null,
          "tip": null,
          "total": null,
          "confidence": 0.0,
          "issues": []
        }
        """;

        // IMPORTANT: JSON mode requires the word "json" in messages. (See docs)
        _systemPrompt = """
        You are a precise receipt normalizer. Output valid JSON only.
        Rules:
        - Use decimals for money.
        - If a value is unknown, use null.
        - Parse patterns like "2x Bagel" => quantity: 2, description: "Bagel".
        - Fix obvious OCR artifacts without hallucinating.
        - Currency must be a 3-letter code (default USD if unspecified).
        """;
    }

    public async Task<string> NormalizeAsync(string rawText, IDictionary<string, object?>? hints = null, CancellationToken ct = default)
    {
        var hintsJson = hints is null ? "null" : JsonSerializer.Serialize(hints, JsonOpts);

        var userPrompt = $$"""
        RAW RECEIPT TEXT:
        ---
        {{rawText}}
        ---

        HINTS (may be null):
        {{hintsJson}}

        REQUIRED JSON SCHEMA:
        {{_schemaJson}}

        Return ONLY one JSON object matching the schema.
        """;

        var body = new
        {
            messages = new object[]
            {
                new { role = "system", content = _systemPrompt },
                new { role = "user",   content = userPrompt }
            },
            temperature = 0.1,
            // JSON mode: model returns a valid JSON object in message.content
            response_format = new { type = "json_object" }
        };

        var url = $"{_endpoint}/openai/deployments/{_deployment}/chat/completions?api-version={_apiVersion}";
        var client = _httpFactory.CreateClient("aoai");

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("api-key", _apiKey);

        using var resp = await client.SendAsync(req, ct);
        var respText = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AOAI chat failed {(int)resp.StatusCode}: {respText}");

        using var doc = JsonDocument.Parse(respText);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return (content ?? "{}").Trim();
    }
}
