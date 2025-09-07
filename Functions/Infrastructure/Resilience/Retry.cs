using Microsoft.Extensions.Logging;

namespace Functions.Infrastructure.Resilience;

public static class Retry
{
    public static async Task<T> RetryAsync<T>(
        string op,
        TimeSpan perAttemptTimeout,
        Func<CancellationToken, Task<T>> action,
        Func<Exception, bool> isTransient,
        int maxAttempts = 3,
        ILogger? log = null,
        CancellationToken outer = default)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            attemptCts.CancelAfter(perAttemptTimeout);

            try
            {
                log?.LogInformation("{Op}: attempt {Attempt}/{Max} (timeout {Timeout}ms)", op, attempt, maxAttempts, perAttemptTimeout.TotalMilliseconds);
                return await action(attemptCts.Token);
            }
            catch (Exception ex) when (isTransient(ex))
            {
                if (attempt < maxAttempts)
                {
                    log?.LogWarning(ex, "{Op}: transient failure on attempt {Attempt}; retrying…", op, attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), outer);
                    continue;
                }
                throw new TimeoutException($"Operation '{op}' exhausted retries after {maxAttempts} attempts.", ex);
            }
            catch (OperationCanceledException oce) when (attemptCts.IsCancellationRequested && !outer.IsCancellationRequested)
            {
                if (attempt < maxAttempts)
                {
                    log?.LogWarning("{Op}: attempt {Attempt} timed out after {Timeout}ms; retrying…", op, attempt, perAttemptTimeout.TotalMilliseconds);
                    continue;
                }
                throw new TimeoutException($"Operation '{op}' timed out after {perAttemptTimeout} (attempt {attempt}).", oce);
            }
        }
        throw new TimeoutException($"Operation '{op}' exhausted retries.");
    }

    public static Task RetryAsync(
        string op,
        TimeSpan perAttemptTimeout,
        Func<CancellationToken, Task> action,
        Func<Exception, bool> isTransient,
        int maxAttempts = 3,
        ILogger? log = null,
        CancellationToken outer = default)
        => RetryAsync<object?>(
            op, perAttemptTimeout,
            async ct => { await action(ct); return null; },
            isTransient, maxAttempts, log, outer);
}
