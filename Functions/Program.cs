using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Extensions.Http;
using System.Net.Http;

var builder = FunctionsApplication.CreateBuilder(args);

// Wire the Functions worker
builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddHttpClient("api")
    .SetHandlerLifetime(TimeSpan.FromMinutes(5))
    .AddPolicyHandler(GetRetryPolicy());

// Also keep the default HttpClient available if you ever need it
builder.Services.AddHttpClient();

// ---------- Observability ----------
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Build().Run();

// ----- local helpers -----
static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    // Retry on 5xx, 408, and transient network failures: 1s, 2s, 4s, 8s, 16s
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(5, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1)));
}
