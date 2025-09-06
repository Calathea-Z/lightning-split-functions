using System.Net.Http.Headers;
using System.Text.Json;
using Functions.Dtos.Ocr;
using Functions.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace Functions.Services;

public sealed class OcrSpaceOcr : IReceiptOcr
{
    private const string Endpoint = "https://api.ocr.space/parse/image";
    private const int MaxBufferBytes = 25 * 1024 * 1024; // 25 MB
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan[] Backoff = {
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromMilliseconds(1500)
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OcrSpaceOcr> _logger;
    private readonly string _apiKey;

    // Register named client in Program.cs: builder.Services.AddHttpClient("ocrspace", c => c.Timeout = TimeSpan.FromSeconds(30));
    public OcrSpaceOcr(IHttpClientFactory httpFactory, ILogger<OcrSpaceOcr> logger, string apiKey)
    {
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentNullException(nameof(apiKey));
        _apiKey = apiKey;
    }

    public async Task<string> ReadAsync(Stream image, CancellationToken ct = default)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));

        await using var ms = await BufferStreamAsync(image, MaxBufferBytes, ct);
        _logger.LogInformation("OCR upload buffered: {Bytes} bytes", ms.Length);

        try
        {
            return await PostOnceWithRetryAsync(ms, engine: 2, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("page error", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "OCREngine=2 page error. Falling back to OCREngine=1.");
            ms.Position = 0;
            return await PostOnceWithRetryAsync(ms, engine: 1, ct);
        }
    }

    // ---------- Retry wrapper ----------
    private async Task<string> PostOnceWithRetryAsync(MemoryStream contentStream, int engine, CancellationToken ct)
    {
        Exception? last = null;

        for (int attempt = 0; attempt <= Backoff.Length; attempt++)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(PerAttemptTimeout);

            try
            {
                var result = await PostOnceAsync(contentStream, engine, linkedCts.Token);
                if (attempt > 0) _logger.LogInformation("OCR succeeded on attempt {Attempt}", attempt + 1);
                return result;
            }
            catch (OperationCanceledException oce) when (!ct.IsCancellationRequested && attempt < Backoff.Length)
            {
                last = oce;
                var delay = Jitter(Backoff[attempt]);
                _logger.LogWarning(oce, "OCR attempt {Attempt} timed out. Retrying in {Delay}ms", attempt + 1, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException hre) when (attempt < Backoff.Length)
            {
                last = hre;
                var delay = Jitter(Backoff[attempt]);
                _logger.LogWarning(hre, "OCR attempt {Attempt} network error. Retrying in {Delay}ms", attempt + 1, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            catch (OcrTransientException tex) when (attempt < Backoff.Length)
            {
                last = tex;
                var delay = Jitter(Backoff[attempt]);
                _logger.LogWarning(tex, "OCR attempt {Attempt} transient {Status}. Retrying in {Delay}ms", attempt + 1, tex.Status, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }

        throw last ?? new InvalidOperationException("OCR failed after retries.");
    }

    // ---------- Single HTTP attempt ----------
    private async Task<string> PostOnceAsync(Stream contentStream, int engine, CancellationToken ct)
    {
        if (contentStream.CanSeek) contentStream.Position = 0;

        using var form = new MultipartFormDataContent
        {
            { new StringContent(_apiKey), "apikey" },
            { new StringContent("eng"), "language" },
            { new StringContent(engine.ToString()), "OCREngine" },
            { new StringContent("true"), "scale" },
            { new StringContent("true"), "isTable" }
        };

        var mediaType = DetectMediaType(contentStream) ?? "image/png";
        if (contentStream.CanSeek) contentStream.Position = 0;

        var fileContent = new StreamContent(contentStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(fileContent, "file", mediaType switch
        {
            "image/jpeg" => "receipt.jpg",
            "image/png" => "receipt.png",
            "image/webp" => "receipt.webp",
            "application/pdf" => "receipt.pdf",
            _ => "receipt.bin"
        });

        var client = _httpFactory.CreateClient("ocrspace");
        using var resp = await client.PostAsync(Endpoint, form, ct);

        var raw = await SafeReadBodyAsync(resp, ct);
        _logger.LogInformation("OCR.Space response {Status}: {Body}",
            (int)resp.StatusCode, raw.Length > 1000 ? raw[..1000] + "..." : raw);

        if (!resp.IsSuccessStatusCode)
        {
            if (IsTransient((int)resp.StatusCode))
                throw new OcrTransientException((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {Truncate(raw, 256)}");

            throw new InvalidOperationException($"OCR HTTP {(int)resp.StatusCode}: {Truncate(raw, 256)}");
        }

        var dto = JsonSerializer.Deserialize<OcrSpaceDto>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                  ?? throw new InvalidOperationException("OCR.Space returned unreadable JSON.");

        if (dto.IsErroredOnProcessing)
            throw new InvalidOperationException(BuildErrorMessage(dto));

        var first = dto.ParsedResults?.FirstOrDefault()
                    ?? throw new InvalidOperationException("OCR.Space returned no ParsedResults.");

        if (first.FileParseExitCode != 1)
            throw new InvalidOperationException(BuildPageErrorMessage(first));

        return first.ParsedText?.Trim() ?? string.Empty;
    }

    // ---------- Local helpers ----------
    private static bool IsTransient(int s) => s == 408 || s == 429 || (s >= 500 && s != 501 && s != 505);

    private static TimeSpan Jitter(TimeSpan baseDelay)
    {
        var max = (int)(baseDelay.TotalMilliseconds * 1.4);
        var min = (int)(baseDelay.TotalMilliseconds * 0.6);
        return TimeSpan.FromMilliseconds(Random.Shared.Next(min, Math.Max(min + 1, max)));
    }

    private static async Task<MemoryStream> BufferStreamAsync(Stream src, int maxBytes, CancellationToken ct)
    {
        if (src.CanSeek) src.Position = 0;
        var ms = new MemoryStream(capacity: Math.Min(maxBytes, 1_048_576));
        var buffer = new byte[64 * 1024];
        int total = 0, read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new InvalidOperationException($"OCR upload exceeds max buffer of {maxBytes} bytes.");
            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        ms.Position = 0;
        return ms;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);

    private static string? DetectMediaType(Stream s)
    {
        if (!s.CanSeek) return null;

        var pos = s.Position;
        try
        {
            Span<byte> buf = stackalloc byte[12];
            var read = s.Read(buf);
            if (read >= 3 && buf[0] == 0xFF && buf[1] == 0xD8 && buf[2] == 0xFF) return "image/jpeg";
            if (read >= 4 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E && buf[3] == 0x47) return "image/png";
            if (read >= 4 && buf[0] == 0x25 && buf[1] == 0x50 && buf[2] == 0x44 && buf[3] == 0x46) return "application/pdf";
            if (read >= 12 && buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46 &&
                             buf[8] == 0x57 && buf[9] == 0x45 && buf[10] == 0x42 && buf[11] == 0x50) return "image/webp";
            return null;
        }
        finally { s.Position = pos; }
    }

    private static string BuildErrorMessage(OcrSpaceDto dto)
    {
        var errs = new List<string>();

        if (dto.ErrorMessage is not null)
        {
            if (dto.ErrorMessage is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) errs.Add(s!);
                    }
                }
                else
                {
                    var s = el.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) errs.Add(s);
                }
            }
            else
            {
                var s = dto.ErrorMessage.ToString();
                if (!string.IsNullOrWhiteSpace(s)) errs.Add(s);
            }
        }

        if (!string.IsNullOrWhiteSpace(dto.ErrorDetails))
            errs.Add(dto.ErrorDetails);

        var composed = string.Join(" | ", errs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        return string.IsNullOrWhiteSpace(composed)
            ? "OCR.Space reported IsErroredOnProcessing=true but no message. (See logs for raw body.)"
            : $"OCR.Space error: {composed}";
    }

    private static string BuildPageErrorMessage(ParsedResult pr)
    {
        var parts = new List<string> { $"OCR.Space page error (code {pr.FileParseExitCode})" };
        if (!string.IsNullOrWhiteSpace(pr.ErrorMessage)) parts.Add(pr.ErrorMessage);
        if (!string.IsNullOrWhiteSpace(pr.ErrorDetails)) parts.Add(pr.ErrorDetails);
        if (!string.IsNullOrWhiteSpace(pr.Message)) parts.Add(pr.Message);

        var msg = string.Join(" | ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(msg) ? "OCR.Space page error with no details. (See logs.)" : msg;
    }

    private sealed class OcrTransientException : Exception
    {
        public int Status { get; }
        public OcrTransientException(int status, string message) : base(message) => Status = status;
    }
}
