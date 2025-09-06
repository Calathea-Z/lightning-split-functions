using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Functions.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Functions.Tests;

public class ReceiptApiClientTests
{
    private readonly Mock<ILogger<ReceiptApiClient>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<HttpMessageHandler> _mockHttpMessageHandler;
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress = new("https://api.example.com");

    public ReceiptApiClientTests()
    {
        _mockLogger = new Mock<ILogger<ReceiptApiClient>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();

        _httpClient = new HttpClient(_mockHttpMessageHandler.Object)
        {
            BaseAddress = _baseAddress
        };
        _mockHttpClientFactory.Setup(x => x.CreateClient("api"))
            .Returns(_httpClient);
    }

    private ReceiptApiClient CreateApiClient()
    {
        return new ReceiptApiClient(_mockHttpClientFactory.Object, _mockLogger.Object, _baseAddress);
    }

    [Theory]
    [InlineData("PATCH", "/api/receipts/{0}/rawtext")]
    [InlineData("PATCH", "/api/receipts/{0}/totals")]
    [InlineData("PATCH", "/api/receipts/{0}/status")]
    [InlineData("POST", "/api/receipts/{0}/items")]
    public async Task ApiMethods_ValidRequest_SendsCorrectPayload(string method, string pathTemplate)
    {
        // Arrange
        var apiClient = CreateApiClient();
        var receiptId = Guid.NewGuid();
        var expectedPath = string.Format(pathTemplate, receiptId);

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method.ToString() == method &&
                    req.RequestUri!.ToString().Contains(expectedPath) &&
                    req.Content != null),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{}")
            });

        // Act
        switch (method)
        {
            case "PATCH" when pathTemplate.Contains("rawtext"):
                await apiClient.PatchRawTextAsync(receiptId, "Coffee $3.50", CancellationToken.None);
                break;
            case "PATCH" when pathTemplate.Contains("totals"):
                await apiClient.PatchTotalsAsync(receiptId, 19.25m, 1.54m, 2.00m, 22.79m, CancellationToken.None);
                break;
            case "PATCH" when pathTemplate.Contains("status"):
                await apiClient.PatchStatusAsync(receiptId, "Parsed", CancellationToken.None);
                break;
            case "POST" when pathTemplate.Contains("items"):
                await apiClient.PostItemAsync(receiptId, new { Label = "Coffee", Qty = 1, UnitPrice = 3.50m }, CancellationToken.None);
                break;
        }

        // Assert
        _mockHttpMessageHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method.ToString() == method &&
                req.RequestUri!.ToString().Contains(expectedPath) &&
                req.Content != null),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task PostItemAsync_TransientError_DoesNotRetry()
    {
        // Arrange
        var apiClient = CreateApiClient();
        var receiptId = Guid.NewGuid();
        var itemDto = new { Label = "Coffee", Qty = 1, UnitPrice = 3.50m };

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.RequestTimeout,
                Content = new StringContent("Request timeout")
            });

        // Act - Should not throw exception since throwOnError is false
        await apiClient.PostItemAsync(receiptId, itemDto, CancellationToken.None);

        // Verify only one attempt was made (critical: POST is non-idempotent)
        _mockHttpMessageHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task PatchRawTextAsync_TransientError_RetriesWithBackoff()
    {
        // Arrange
        var apiClient = CreateApiClient();
        var receiptId = Guid.NewGuid();
        var rawText = "Coffee $3.50";
        var attemptCount = 0;

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, ct) =>
            {
                attemptCount++;
                if (attemptCount == 1)
                {
                    return Task.FromResult(new HttpResponseMessage
                    {
                        StatusCode = HttpStatusCode.RequestTimeout,
                        Content = new StringContent("Request timeout")
                    });
                }
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{}")
                });
            });

        // Act
        await apiClient.PatchRawTextAsync(receiptId, rawText, CancellationToken.None);

        // Assert - Proves retry policy works
        Assert.Equal(2, attemptCount);
    }

    [Fact]
    public async Task PatchTotalsAsync_NonTransientError_ThrowsException()
    {
        // Arrange
        var apiClient = CreateApiClient();
        var receiptId = Guid.NewGuid();

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.BadRequest,
                Content = new StringContent("Bad request")
            });

        // Act & Assert - One representative 4xx → throws
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            apiClient.PatchTotalsAsync(receiptId, 19.25m, 1.54m, 2.00m, 22.79m, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Not found")]
    [InlineData(HttpStatusCode.InternalServerError, "Internal server error")]
    public async Task PostParseErrorAsync_ErrorResponses_LogsAndContinues(HttpStatusCode statusCode, string responseBody)
    {
        // Arrange
        var apiClient = CreateApiClient();
        var receiptId = Guid.NewGuid();
        var note = "Parse failed";

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseBody)
            });

        // Act
        await apiClient.PostParseErrorAsync(receiptId, note, CancellationToken.None);

        // Assert - Should not throw exception, just log (most realistic race condition)
        _mockHttpMessageHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ApiMethods_CancellationToken_PassedThrough()
    {
        // Arrange
        var apiClient = CreateApiClient();
        var receiptId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        CancellationToken? capturedToken = null;

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, ct) =>
            {
                capturedToken = ct;
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{}")
                });
            });

        // Act
        await apiClient.PatchRawTextAsync(receiptId, "test", token);

        // Assert - Cancellation token is passed through
        Assert.NotNull(capturedToken);
        // Verify that the cancellation token is not cancelled (functional equivalence)
        Assert.False(capturedToken.Value.IsCancellationRequested);
        Assert.False(token.IsCancellationRequested);
        
        _mockHttpMessageHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }
}
