using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Functions.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Functions.Tests;

public class RetryPolicyTests
{
    private readonly Mock<ILogger> _mockLogger;

    public RetryPolicyTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    [Fact]
    public async Task RetryAsync_TransientException_RetriesUpToMaxAttempts()
    {
        // Arrange
        var attemptCount = 0;
        var maxAttempts = 3;
        var perAttemptTimeout = TimeSpan.FromSeconds(1);

        Func<CancellationToken, Task<string>> action = async (ct) =>
        {
            attemptCount++;
            if (attemptCount < maxAttempts)
                throw new HttpRequestException("Transient error");
            return "success";
        };

        Func<Exception, bool> isTransient = ex => ex is HttpRequestException;

        // Act
        var result = await ReceiptParseOrchestrator.RetryAsync(
            "test.operation",
            perAttemptTimeout,
            action,
            isTransient,
            maxAttempts,
            _mockLogger.Object,
            CancellationToken.None);

        // Assert
        Assert.Equal("success", result);
        Assert.Equal(maxAttempts, attemptCount);
    }

    [Fact]
    public async Task RetryAsync_NonTransientException_DoesNotRetry()
    {
        // Arrange
        var attemptCount = 0;
        var maxAttempts = 3;
        var perAttemptTimeout = TimeSpan.FromSeconds(1);

        Func<CancellationToken, Task<string>> action = async (ct) =>
        {
            attemptCount++;
            throw new InvalidOperationException("Non-transient error");
        };

        Func<Exception, bool> isTransient = ex => ex is HttpRequestException;

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ReceiptParseOrchestrator.RetryAsync(
                "test.operation",
                perAttemptTimeout,
                action,
                isTransient,
                maxAttempts,
                _mockLogger.Object,
                CancellationToken.None);
        });

        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task RetryAsync_TimeoutException_RetriesAndThrowsAfterMaxAttempts()
    {
        // Arrange
        var attemptCount = 0;
        var maxAttempts = 2;
        var perAttemptTimeout = TimeSpan.FromMilliseconds(100);

        Func<CancellationToken, Task<string>> action = async (ct) =>
        {
            attemptCount++;
            await Task.Delay(200, ct); // Longer than timeout
            return "success";
        };

        Func<Exception, bool> isTransient = ex => false;

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await ReceiptParseOrchestrator.RetryAsync(
                "test.operation",
                perAttemptTimeout,
                action,
                isTransient,
                maxAttempts,
                _mockLogger.Object,
                CancellationToken.None);
        });

        Assert.Equal(maxAttempts, attemptCount);
    }

    [Fact]
    public async Task RetryAsync_OuterCancellation_RespectsCancellation()
    {
        // Arrange
        var attemptCount = 0;
        var maxAttempts = 3;
        var perAttemptTimeout = TimeSpan.FromSeconds(1);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        Func<CancellationToken, Task<string>> action = async (ct) =>
        {
            attemptCount++;
            await Task.Delay(100, ct); // Longer than cancellation timeout
            return "success";
        };

        Func<Exception, bool> isTransient = ex => false;

        // Act & Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ReceiptParseOrchestrator.RetryAsync(
                "test.operation",
                perAttemptTimeout,
                action,
                isTransient,
                maxAttempts,
                _mockLogger.Object,
                cts.Token);
        });
        
        // Verify it's a cancellation exception (TaskCanceledException inherits from OperationCanceledException)
        Assert.True(exception is OperationCanceledException);

        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task RetryAsync_SuccessOnFirstAttempt_NoRetries()
    {
        // Arrange
        var attemptCount = 0;
        var maxAttempts = 3;
        var perAttemptTimeout = TimeSpan.FromSeconds(1);

        Func<CancellationToken, Task<string>> action = async (ct) =>
        {
            attemptCount++;
            return "success";
        };

        Func<Exception, bool> isTransient = ex => false;

        // Act
        var result = await ReceiptParseOrchestrator.RetryAsync(
            "test.operation",
            perAttemptTimeout,
            action,
            isTransient,
            maxAttempts,
            _mockLogger.Object,
            CancellationToken.None);

        // Assert
        Assert.Equal("success", result);
        Assert.Equal(1, attemptCount);
    }

    [Fact]
    public async Task RetryAsync_ExhaustsRetries_ThrowsTimeoutException()
    {
        // Arrange
        var attemptCount = 0;
        var maxAttempts = 2;
        var perAttemptTimeout = TimeSpan.FromMilliseconds(100);

        Func<CancellationToken, Task<string>> action = async (ct) =>
        {
            attemptCount++;
            throw new HttpRequestException("Transient error");
        };

        Func<Exception, bool> isTransient = ex => ex is HttpRequestException;

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await ReceiptParseOrchestrator.RetryAsync(
                "test.operation",
                perAttemptTimeout,
                action,
                isTransient,
                maxAttempts,
                _mockLogger.Object,
                CancellationToken.None);
        });

        Assert.Equal(maxAttempts, attemptCount);
    }
}
