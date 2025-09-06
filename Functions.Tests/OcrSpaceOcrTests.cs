using System;
using System.IO;
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

public class OcrSpaceOcrTests
{
    private readonly Mock<ILogger<OcrSpaceOcr>> _mockLogger;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<HttpMessageHandler> _mockHttpMessageHandler;
    private readonly HttpClient _httpClient;

    public OcrSpaceOcrTests()
    {
        _mockLogger = new Mock<ILogger<OcrSpaceOcr>>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();

        _httpClient = new HttpClient(_mockHttpMessageHandler.Object);
        _mockHttpClientFactory.Setup(x => x.CreateClient("ocrspace"))
            .Returns(_httpClient);
    }

    private OcrSpaceOcr CreateOcrService()
    {
        return new OcrSpaceOcr(_mockHttpClientFactory.Object, _mockLogger.Object, "test-api-key");
    }

    [Fact]
    public async Task ReadAsync_ValidImage_ReturnsOcrText()
    {
        // Arrange
        var ocrService = CreateOcrService();
        using var imageStream = new MemoryStream(new byte[1024]);
        var expectedResponse = """
        {
            "ParsedResults": [
                {
                    "ParsedText": "Coffee $3.50\nSandwich $8.75\nTotal: $12.25",
                    "FileParseExitCode": 1
                }
            ]
        }
        """;

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(expectedResponse)
            });

        // Act
        var result = await ocrService.ReadAsync(imageStream, CancellationToken.None);

        // Assert
        Assert.Equal("Coffee $3.50\nSandwich $8.75\nTotal: $12.25", result);
    }

    [Fact]
    public async Task ReadAsync_TransientError_RetriesWithBackoff()
    {
        // Arrange
        var ocrService = CreateOcrService();
        using var imageStream = new MemoryStream(new byte[1024]);
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
                    Content = new StringContent("""
                    {
                        "ParsedResults": [
                            {
                                "ParsedText": "Success on retry",
                                "FileParseExitCode": 1
                            }
                        ]
                    }
                    """)
                });
            });

        // Act
        var result = await ocrService.ReadAsync(imageStream, CancellationToken.None);

        // Assert
        Assert.Equal("Success on retry", result);
        Assert.Equal(2, attemptCount);
    }

    [Fact]
    public async Task ReadAsync_NonTransientError_ThrowsException()
    {
        // Arrange
        var ocrService = CreateOcrService();
        using var imageStream = new MemoryStream(new byte[1024]);

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

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ocrService.ReadAsync(imageStream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_Engine2PageError_FallsBackToEngine1()
    {
        // Arrange
        var ocrService = CreateOcrService();
        using var imageStream = new MemoryStream(new byte[1024]);

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("""
                {
                    "ParsedResults": [
                        {
                            "ParsedText": "Success with engine 2",
                            "FileParseExitCode": 1
                        }
                    ]
                }
                """)
            });

        // Act
        var result = await ocrService.ReadAsync(imageStream, CancellationToken.None);

        // Assert
        Assert.Equal("Success with engine 2", result);
    }

    [Fact]
    public async Task ReadAsync_EmptyResponse_ReturnsEmptyString()
    {
        // Arrange
        var ocrService = CreateOcrService();
        using var imageStream = new MemoryStream(new byte[1024]);

        _mockHttpMessageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("""
                {
                    "ParsedResults": [
                        {
                            "ParsedText": "",
                            "FileParseExitCode": 1
                        }
                    ]
                }
                """)
            });

        // Act
        var result = await ocrService.ReadAsync(imageStream, CancellationToken.None);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public async Task ReadAsync_NetworkException_Retries()
    {
        // Arrange
        var ocrService = CreateOcrService();
        using var imageStream = new MemoryStream(new byte[1024]);
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
                    throw new HttpRequestException("Network error");
                }
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("""
                    {
                        "ParsedResults": [
                            {
                                "ParsedText": "Success after network retry",
                                "FileParseExitCode": 1
                            }
                        ]
                    }
                    """)
                });
            });

        // Act
        var result = await ocrService.ReadAsync(imageStream, CancellationToken.None);

        // Assert
        Assert.Equal("Success after network retry", result);
        Assert.Equal(2, attemptCount);
    }
}
