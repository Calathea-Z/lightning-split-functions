using Azure.Storage.Blobs;
using Functions.Helpers;
using Functions.Services;
using Functions.Services.Abstractions;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

/* ---------- Logging: activity + scopes ---------- */
builder.Logging.Configure(o =>
{
    o.ActivityTrackingOptions =
        ActivityTrackingOptions.TraceId |
        ActivityTrackingOptions.SpanId |
        ActivityTrackingOptions.ParentId;
});

builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

/* ---------- Config helper (supports root and "Values:" keys) ---------- */
string? GetCfg(string key) =>
    builder.Configuration[key]
    ?? builder.Configuration[$"Values:{key}"]
    ?? Environment.GetEnvironmentVariable(key)
    ?? Environment.GetEnvironmentVariable($"Values:{key}");

/* ---------- Functions worker ---------- */
builder.ConfigureFunctionsWebApplication();

/* ---------- HTTP clients ---------- */
builder.Services
    .AddHttpClient("api")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddPolicyHandler(RetryPolicies.GetHttpRetryPolicy());

builder.Services.AddHttpClient(); // default

builder.Services
    .AddHttpClient("ocrspace", c => c.Timeout = TimeSpan.FromSeconds(60))
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));
// .AddPolicyHandler(RetryPolicies.GetHttpRetryPolicy()); // optional

/* ---------- Application Insights (Functions isolated) ---------- */
var aiConn = GetCfg("APPLICATIONINSIGHTS_CONNECTION_STRING")
          ?? GetCfg("ApplicationInsights:ConnectionString");

builder.Services.AddApplicationInsightsTelemetryWorkerService(o =>
{
    if (!string.IsNullOrWhiteSpace(aiConn))
        o.ConnectionString = aiConn;
});

builder.Services.Configure<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerOptions>(o =>
{
    o.IncludeScopes = true; // BeginScope(state) -> customDimensions
    o.TrackExceptionsAsExceptionTelemetry = true;
});

builder.Services.AddSingleton<ITelemetryInitializer>(
    new CloudRoleNameInitializer("Lightning.Functions"));

builder.Services.ConfigureFunctionsApplicationInsights();

/* ---------- Azure Storage Blobs DI ---------- */
var storageConn = GetCfg("AzureWebJobsStorage")
    ?? throw new InvalidOperationException("AzureWebJobsStorage is not configured. Set it in local.settings.json or environment.");

builder.Services.AddAzureClients(az => az.AddBlobServiceClient(storageConn));

builder.Services.AddSingleton(sp =>
{
    var svc = sp.GetRequiredService<BlobServiceClient>();
    var container = svc.GetBlobContainerClient("receipts");
#if DEBUG
    container.CreateIfNotExists(); // dev convenience only
#endif
    return container;
});

/* ---------- Base API URL (validated) ---------- */
var apiBaseRaw = GetCfg("ApiBaseUrl") ?? "http://localhost:5104";
if (!Uri.TryCreate(apiBaseRaw, UriKind.Absolute, out var apiBase) ||
    (apiBase.Scheme != Uri.UriSchemeHttp && apiBase.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException($"Invalid ApiBaseUrl: '{apiBaseRaw}'. Use absolute http/https URL.");
}

/* ---------- Orchestrator ---------- */
builder.Services.AddScoped<IReceiptParseOrchestrator, ReceiptParseOrchestrator>();

/* ---------- Our app services ---------- */
builder.Services.AddSingleton<IImagePreprocessor, ImagePreprocessor>();

builder.Services.AddSingleton<IReceiptApiClient>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>();
    var log = sp.GetRequiredService<ILogger<ReceiptApiClient>>();
    return new ReceiptApiClient(http, log, apiBase);
});

// If you implemented a poison notifier service, register it:
// builder.Services.AddScoped<IPoisonNotifier, PoisonNotifier>();

/* ---------- OCR service selection ---------- */
var endpoint = GetCfg("COMPUTERVISION__ENDPOINT");
var keyAzure = GetCfg("COMPUTERVISION__KEY");
var keyOcrSpace = GetCfg("OCRSPACE__APIKEY");
var useOcr = (GetCfg("USE_OCR") ?? "1") == "1";

if (useOcr && !string.IsNullOrWhiteSpace(keyOcrSpace))
{
    builder.Services.AddSingleton<IReceiptOcr>(sp =>
    {
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
        var logger = sp.GetRequiredService<ILogger<OcrSpaceOcr>>();
        return new OcrSpaceOcr(httpFactory, logger, keyOcrSpace!);
    });
    Console.WriteLine("OCR: Using OCR.Space");
}
else if (useOcr && !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(keyAzure))
{
    builder.Services.AddSingleton<IReceiptOcr>(_ => new AzureVisionOcr(endpoint!, keyAzure!));
    Console.WriteLine("OCR: Using Azure Document Intelligence");
}
else
{
    builder.Services.AddSingleton<IReceiptOcr, NoopOcr>();
    Console.WriteLine("OCR: Using Noop (returns empty text)");
}

/* ---------- diagnostics ---------- */
Console.WriteLine($"OCR: useOcr={useOcr}, hasOcrSpaceKey={!string.IsNullOrWhiteSpace(keyOcrSpace)}, hasAzureKey={!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(keyAzure)}");
Console.WriteLine($"ApiBaseUrl={apiBase}");
Console.WriteLine($"CWD={Environment.CurrentDirectory}");
Console.WriteLine($"Has local.settings.json? {File.Exists(Path.Combine(Environment.CurrentDirectory, "local.settings.json"))}");

/* ---------- run ---------- */
builder.Build().Run();
