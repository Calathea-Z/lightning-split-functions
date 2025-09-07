using Api.Abstractions.Receipts;
using Api.Abstractions.Transport;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Functions.Services;
using Functions.Services.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Functions.Tests;

public class ReceiptParseOrchestratorTests
{
    private readonly Mock<ILogger<IReceiptParseOrchestrator>> _mockLogger;
    private readonly Mock<BlobServiceClient> _mockBlobServiceClient;
    private readonly Mock<IReceiptOcr> _mockOcr;
    private readonly Mock<IImagePreprocessor> _mockImagePreprocessor;
    private readonly Mock<IReceiptApiClient> _mockApiClient;
    private readonly Mock<IReceiptNormalizer> _mockNormalizer;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<BlobContainerClient> _mockLockContainer;
    private readonly Mock<BlobClient> _mockLockBlob;
    private readonly Mock<BlobContainerClient> _mockReceiptContainer;
    private readonly Mock<BlobClient> _mockReceiptBlob;

    public ReceiptParseOrchestratorTests()
    {
        _mockLogger = new Mock<ILogger<IReceiptParseOrchestrator>>();
        _mockBlobServiceClient = new Mock<BlobServiceClient>();
        _mockOcr = new Mock<IReceiptOcr>();
        _mockImagePreprocessor = new Mock<IImagePreprocessor>();
        _mockApiClient = new Mock<IReceiptApiClient>();
        _mockNormalizer = new Mock<IReceiptNormalizer>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLockContainer = new Mock<BlobContainerClient>();
        _mockLockBlob = new Mock<BlobClient>();
        _mockReceiptContainer = new Mock<BlobContainerClient>();
        _mockReceiptBlob = new Mock<BlobClient>();

        // Setup configuration mock
        _mockConfiguration.Setup(x => x["AOAI_DEPLOYMENT"]).Returns("test-model");

        // Container wiring
        _mockBlobServiceClient.Setup(x => x.GetBlobContainerClient("receipt-parse-locks"))
            .Returns(_mockLockContainer.Object);
        _mockBlobServiceClient.Setup(x => x.GetBlobContainerClient("receipts"))
            .Returns(_mockReceiptContainer.Object);

        // Lock + receipt blob clients
        _mockLockContainer.Setup(x => x.GetBlobClient(It.IsAny<string>()))
            .Returns(_mockLockBlob.Object);
        _mockReceiptContainer.Setup(x => x.GetBlobClient("test-receipt.jpg"))
            .Returns(_mockReceiptBlob.Object);

        // CreateIfNotExistsAsync � match the 4-arg overload your code binds to (with defaults + ct)
        _mockLockContainer
            .Setup(x => x.CreateIfNotExistsAsync(
                It.IsAny<PublicAccessType>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                BlobsModelFactory.BlobContainerInfo(default, DateTimeOffset.UtcNow),
                Mock.Of<Response>()));
    }

    private static Response<BlobProperties> MakeBlobProps(long length, string contentType = "image/jpeg")
    {
        var props = BlobsModelFactory.BlobProperties(
            lastModified: DateTimeOffset.UtcNow,
            contentLength: length,
            contentType: contentType
        );
        return Response.FromValue(props, Mock.Of<Response>());
    }

    private ReceiptParseOrchestrator CreateOrchestrator() =>
        new ReceiptParseOrchestrator(
            _mockLogger.Object,
            _mockBlobServiceClient.Object,
            _mockOcr.Object,
            _mockImagePreprocessor.Object,
            _mockApiClient.Object,
            _mockNormalizer.Object,
            _mockConfiguration.Object);

    private static ReceiptParseMessage CreateTestMessage() =>
        new("receipts", "test-receipt.jpg", Guid.NewGuid().ToString());

    [Fact]
    public async Task Given_ValidReceipt_When_Processing_Then_ExecutesCorrectCallOrder()
    {
        // Arrange
        var message = CreateTestMessage();
        var receiptId = Guid.Parse(message.ReceiptId!);
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024 * 1024, "image/jpeg"));

        // Match OpenReadAsync(long, CancellationToken) � your orchestrator calls with only ct
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        // Lock creation uses BinaryData overload
        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        // Preprocess + OCR
        _mockImagePreprocessor.Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        var sunnyMartText =
            "Coffee $3.50\nSandwich $8.75\nCookie 2x $2.00\nSoda $2.50\nChips $1.50\nSubtotal: $20.25\nTax: $1.54\nTip: $2.00\nTotal: $22.79";
        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sunnyMartText);

        // API calls
        var postedItems = new List<CreateReceiptItemRequest>();
        _mockApiClient.Setup(x => x.PatchRawTextAsync(receiptId, It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateReceiptItemRequest, CancellationToken>((_, item, _) => postedItems.Add(item))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchStatusAsync(receiptId, It.IsAny<UpdateStatusRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchParseMetaAsync(receiptId, It.IsAny<UpdateParseMetaRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await orchestrator.RunAsync(message, CancellationToken.None);

        // Assert
        _mockApiClient.Verify(x => x.PatchRawTextAsync(receiptId, It.Is<UpdateRawTextRequest>(r => r.RawText == sunnyMartText), It.IsAny<CancellationToken>()), Times.Once);
        _mockApiClient.Verify(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(5));
        _mockApiClient.Verify(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        _mockApiClient.Verify(x => x.PatchStatusAsync(receiptId, It.Is<UpdateStatusRequest>(r => r.Status == ReceiptStatus.Parsed), It.IsAny<CancellationToken>()), Times.Once);
        _mockApiClient.Verify(x => x.PatchParseMetaAsync(receiptId, It.IsAny<UpdateParseMetaRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(5, postedItems.Count);

        _mockLockBlob.Verify(x => x.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(),
            It.IsAny<BlobRequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_OcrTransientException_When_Processing_Then_RetriesWithFreshStream()
    {
        // Arrange
        var message = CreateTestMessage();
        var orchestrator = CreateOrchestrator();
        var attemptCount = 0;

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));

        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        _mockImagePreprocessor
            .Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback(() => attemptCount++)
            .Returns<Stream, CancellationToken>((_, __) =>
            {
                if (attemptCount == 1) throw new HttpRequestException("Transient network error");
                return Task.FromResult("Coffee $3.50\nTotal: $3.50");
            });

        _mockApiClient.Setup(x => x.PatchRawTextAsync(It.IsAny<Guid>(), It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PostItemAsync(It.IsAny<Guid>(), It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchTotalsAsync(It.IsAny<Guid>(), It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchStatusAsync(It.IsAny<Guid>(), It.IsAny<UpdateStatusRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchParseMetaAsync(It.IsAny<Guid>(), It.IsAny<UpdateParseMetaRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await orchestrator.RunAsync(message, CancellationToken.None);

        // Assert
        Assert.Equal(2, attemptCount);
        _mockOcr.Verify(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        // With your orchestrator, the preprocessed bytes are buffered once and reused on retry
        _mockReceiptBlob.Verify(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockImagePreprocessor.Verify(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_OcrNonTransientException_When_Processing_Then_BubblesErrorAndCleansUpLock()
    {
        // Arrange
        var message = CreateTestMessage();
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        _mockImagePreprocessor.Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("OCR service unavailable"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync(message, CancellationToken.None));

        _mockApiClient.Verify(x => x.PatchRawTextAsync(It.IsAny<Guid>(), It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApiClient.Verify(x => x.PostItemAsync(It.IsAny<Guid>(), It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        _mockLockBlob.Verify(x => x.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(),
            It.IsAny<BlobRequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ItemsWithAdjustmentLabels_When_Processing_Then_AdjustmentsAreSkipped()
    {
        // Arrange
        var message = CreateTestMessage();
        var receiptId = Guid.Parse(message.ReceiptId!);
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        _mockImagePreprocessor.Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        var textWithAdjustments = "Coffee $3.50\nAdjustment -$1.00\nDiscount/Adjustment -$0.50\nSandwich $8.75";
        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(textWithAdjustments);

        var postedItems = new List<CreateReceiptItemRequest>();
        _mockApiClient.Setup(x => x.PatchRawTextAsync(receiptId, It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, CreateReceiptItemRequest, CancellationToken>((_, item, __) => postedItems.Add(item))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchTotalsAsync(It.IsAny<Guid>(), It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchStatusAsync(It.IsAny<Guid>(), It.IsAny<UpdateStatusRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchParseMetaAsync(It.IsAny<Guid>(), It.IsAny<UpdateParseMetaRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await orchestrator.RunAsync(message, CancellationToken.None);

        // Assert
        _mockApiClient.Verify(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(2, postedItems.Count);
    }

    [Fact]
    public async Task Given_ItemPostTransientError_When_Processing_Then_DoesNotRetryAndCleansUpLock()
    {
        // Arrange
        var message = CreateTestMessage();
        var receiptId = Guid.Parse(message.ReceiptId!);
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        _mockImagePreprocessor.Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Coffee $3.50\nSandwich $8.75");

        _mockApiClient.Setup(x => x.PatchRawTextAsync(receiptId, It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Transient error"));

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => orchestrator.RunAsync(message, CancellationToken.None));
        _mockApiClient.Verify(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        _mockLockBlob.Verify(x => x.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(),
            It.IsAny<BlobRequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_MissingSubtotal_When_Processing_Then_ComputesFromItems()
    {
        // Arrange
        var message = CreateTestMessage();
        var receiptId = Guid.Parse(message.ReceiptId!);
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        _mockImagePreprocessor.Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        var textWithoutSubtotal = "Coffee $3.50\nSandwich $8.75\nTax: $1.54\nTotal: $13.79";
        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(textWithoutSubtotal);

        var finalTotals = new List<UpdateTotalsRequest>();

        _mockApiClient.Setup(x => x.PatchRawTextAsync(receiptId, It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, UpdateTotalsRequest, CancellationToken>((_, totals, __) => finalTotals.Add(totals))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchStatusAsync(receiptId, It.IsAny<UpdateStatusRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchParseMetaAsync(receiptId, It.IsAny<UpdateParseMetaRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await orchestrator.RunAsync(message, CancellationToken.None);

        // Assert
        _mockApiClient.Verify(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        Assert.True(finalTotals.Count >= 1);
        var last = finalTotals.Last();
        Assert.Equal(12.25m, last.SubTotal);
        Assert.Equal(1.54m, last.Tax);
        Assert.Equal(13.79m, last.Total);
    }

    [Fact]
    public async Task Given_MissingTotal_When_Processing_Then_ComputesFromSubtotalTaxTip()
    {
        // Arrange
        var message = CreateTestMessage();
        var receiptId = Guid.Parse(message.ReceiptId!);
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        _mockImagePreprocessor.Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        var textWithoutTotal = "Coffee $3.50\nSandwich $8.75\nSubtotal: $12.25\nTax: $1.54\nTip: $2.00";
        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(textWithoutTotal);

        var finalTotals = new List<UpdateTotalsRequest>();

        _mockApiClient.Setup(x => x.PatchRawTextAsync(receiptId, It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, UpdateTotalsRequest, CancellationToken>((_, totals, __) => finalTotals.Add(totals))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchStatusAsync(receiptId, It.IsAny<UpdateStatusRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchParseMetaAsync(receiptId, It.IsAny<UpdateParseMetaRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await orchestrator.RunAsync(message, CancellationToken.None);

        // Assert
        _mockApiClient.Verify(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        Assert.True(finalTotals.Count >= 1);
        var last = finalTotals.Last();
        Assert.Equal(12.25m, last.SubTotal);
        Assert.Equal(1.54m, last.Tax);
        Assert.Equal(2.00m, last.Tip);
        Assert.Equal(15.79m, last.Total);
    }

    [Fact]
    public async Task Given_DecimalRounding_When_Processing_Then_EnsuresTwoDecimalPlaces()
    {
        // Arrange
        var message = CreateTestMessage();
        var receiptId = Guid.Parse(message.ReceiptId!);
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        _mockImagePreprocessor.Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        var textWithRounding = "Coffee $3.333\nSandwich $8.666\nSubtotal: $12.00\nTax: $1.234\nTip: $2.567";
        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(textWithRounding);

        var finalTotals = new List<UpdateTotalsRequest>();

        _mockApiClient.Setup(x => x.PatchRawTextAsync(receiptId, It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PostItemAsync(receiptId, It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, UpdateTotalsRequest, CancellationToken>((_, totals, __) => finalTotals.Add(totals))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchStatusAsync(receiptId, It.IsAny<UpdateStatusRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchParseMetaAsync(receiptId, It.IsAny<UpdateParseMetaRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await orchestrator.RunAsync(message, CancellationToken.None);

        // Assert
        _mockApiClient.Verify(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
        Assert.True(finalTotals.Count >= 1);
        var last = finalTotals.Last();
        Assert.Equal(12.00m, last.SubTotal);
        Assert.Equal(1.23m, last.Tax);
        Assert.Equal(2.57m, last.Tip);
        Assert.Equal(15.80m, last.Total);
    }

    [Fact]
    public async Task Given_LockAlreadyExists_When_Processing_Then_ExitsEarly()
    {
        // Arrange
        var message = CreateTestMessage();
        var orchestrator = CreateOrchestrator();

        var requestFailedException = new RequestFailedException(412, "ConditionNotMet", "BlobAlreadyExists", null);
        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(requestFailedException);

        // Act
        await orchestrator.RunAsync(message, CancellationToken.None);

        // Assert
        _mockReceiptBlob.Verify(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockOcr.Verify(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApiClient.Verify(x => x.PatchRawTextAsync(It.IsAny<Guid>(), It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_HugeBlob_When_Processing_Then_ThrowsAndDeletesLock()
    {
        // Arrange
        var message = CreateTestMessage();
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(60L * 1024 * 1024)); // 60MB

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync(message, CancellationToken.None));

        _mockLockBlob.Verify(x => x.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(),
            It.IsAny<BlobRequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ZeroItemsOrBadParse_When_Processing_Then_StillSetsStatusToParsed()
    {
        // Arrange
        var message = CreateTestMessage();
        var receiptId = Guid.Parse(message.ReceiptId!);
        var orchestrator = CreateOrchestrator();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        _mockImagePreprocessor.Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        var emptyText = "Total: $0.00";
        _mockOcr.Setup(x => x.ReadAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyText);

        _mockApiClient.Setup(x => x.PatchRawTextAsync(receiptId, It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchTotalsAsync(receiptId, It.IsAny<UpdateTotalsRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchStatusAsync(receiptId, It.IsAny<UpdateStatusRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockApiClient.Setup(x => x.PatchParseMetaAsync(receiptId, It.IsAny<UpdateParseMetaRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await orchestrator.RunAsync(message, CancellationToken.None);

        // Assert
        _mockApiClient.Verify(x => x.PostItemAsync(It.IsAny<Guid>(), It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApiClient.Verify(x => x.PatchStatusAsync(receiptId, It.Is<UpdateStatusRequest>(r => r.Status == ReceiptStatus.ParsedNeedsReview), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_CancellationRequested_When_Processing_Then_ExitsGracefullyAndCleansUpLock()
    {
        // Arrange
        var message = CreateTestMessage();
        var orchestrator = CreateOrchestrator();
        using var cts = new CancellationTokenSource();

        _mockReceiptBlob
            .Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeBlobProps(1024));
        _mockReceiptBlob
            .Setup(x => x.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[1024]));

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        // Simulate cooperative cancellation bubbling from a dependency
        _mockImagePreprocessor
            .Setup(x => x.PrepareAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => orchestrator.RunAsync(message, cts.Token));

        _mockLockBlob.Verify(x => x.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(),
            It.IsAny<BlobRequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_BlobNotFound_When_Processing_Then_LogsAndBailsWithoutApiCalls()
    {
        // Arrange
        var message = CreateTestMessage();
        var orchestrator = CreateOrchestrator();

        _mockLockBlob.Setup(x => x.UploadAsync(
                It.IsAny<BinaryData>(),
                It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        var notFound = new RequestFailedException(404, "BlobNotFound", "The specified blob does not exist.", null);
        _mockReceiptBlob.Setup(x => x.GetPropertiesAsync(It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(notFound);

        // Act & Assert
        await Assert.ThrowsAsync<RequestFailedException>(() => orchestrator.RunAsync(message, CancellationToken.None));

        _mockApiClient.Verify(x => x.PatchRawTextAsync(It.IsAny<Guid>(), It.IsAny<UpdateRawTextRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockApiClient.Verify(x => x.PostItemAsync(It.IsAny<Guid>(), It.IsAny<CreateReceiptItemRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        _mockLockBlob.Verify(x => x.DeleteIfExistsAsync(
            It.IsAny<DeleteSnapshotsOption>(),
            It.IsAny<BlobRequestConditions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

}
