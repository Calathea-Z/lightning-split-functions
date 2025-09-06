using Polly;
using Polly.Extensions.Http;

namespace Functions.Helpers
{
    public static class RetryPolicies
    {
        public static IAsyncPolicy<HttpResponseMessage> GetHttpRetryPolicy() =>
            HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(5, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
    }
}
