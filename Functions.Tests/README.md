# Functions Unit Tests

This test suite provides comprehensive coverage for the Functions app, specifically testing the receipt parsing workflow and all its components.

## Test Coverage

### 1. ReceiptParseOrchestratorTests.cs

Tests the main orchestrator that coordinates the entire receipt parsing workflow:

- **Happy Path (Sunny Mart)**: Tests the complete workflow with 5 items, proper totals, and correct API call order
- **OCR Retry Logic**: Verifies transient exception handling with fresh streams
- **Empty OCR Text**: Ensures empty OCR results still patch raw text and proceed
- **Item Filtering**: Tests that "Adjustment" and "Discount/Adjustment" items are skipped
- **Item Posting**: Verifies no retries on item POST failures
- **Totals Handling**: Tests missing subtotal/total computation and rounding
- **Finalization**: Verifies PatchStatus then second PatchTotals call order
- **Locking**: Tests idempotency with 412 responses and lock cleanup
- **Error Handling**: Ensures proper cleanup even when exceptions occur
- **Edge Cases**: Tests huge blobs, IsSane=false, and zero items scenarios

### 2. RetryPolicyTests.cs

Tests the retry mechanism used throughout the application:

- **Transient Exception Retries**: Verifies retries up to max attempts
- **Non-Transient Exception Handling**: Ensures no retries for permanent failures
- **Timeout Handling**: Tests per-attempt timeouts and final timeout exceptions
- **Cancellation Support**: Verifies outer cancellation token respect
- **Success on First Attempt**: Ensures no unnecessary retries
- **Retry Exhaustion**: Tests proper exception when all retries are exhausted

### 3. OcrSpaceOcrTests.cs

Tests the OCR service integration:

- **Valid Image Processing**: Tests successful OCR text extraction
- **Transient Error Retries**: Verifies retry logic with backoff
- **Non-Transient Error Handling**: Tests proper exception propagation
- **Engine Fallback**: Tests fallback from engine 2 to engine 1 on page errors
- **Empty Response Handling**: Tests empty OCR result processing
- **Network Exception Retries**: Verifies network error retry logic

### 4. ReceiptApiClientTests.cs

Tests the API client for backend communication:

- **PATCH Operations**: Tests PatchRawText, PatchTotals, PatchStatus
- **POST Operations**: Tests PostItem and PostParseError
- **Retry Policies**: Verifies different retry behaviors for different operations
- **Error Handling**: Tests various HTTP status code scenarios
- **Payload Validation**: Ensures correct request payloads are sent

### 5. HeuristicExtractorTests.cs

Tests the receipt text parsing logic:

- **Sunny Mart Scenario**: Tests complete receipt parsing with all fields
- **Adjustment Items**: Verifies adjustment items are included in extraction
- **Missing Fields**: Tests handling of missing subtotal, tax, tip, total
- **Empty/Gibberish Text**: Tests edge cases with invalid input
- **Quantity Patterns**: Tests various quantity format recognition
- **Decimal Formats**: Tests different decimal notation handling
- **Total Labels**: Tests recognition of various total field labels
- **Sanity Checking**: Tests IsSane flag logic

### 6. ReceiptParseIntegrationTests.cs

Integration tests covering complete workflows:

- **Complex Receipt Processing**: Tests full workflow with complex receipt data
- **Error Recovery**: Tests lock cleanup even when API calls fail
- **Lock Deletion Failure**: Tests graceful handling of lock cleanup failures
- **Partial Data Handling**: Tests processing with missing receipt fields

## Test Scenarios Covered

### Happy Path (Sunny Mart)

- Posts raw text once
- Posts exactly 5 items (no adjustments)
- Calls PatchTotals with (19.25, 1.54, 2.00, 22.79)
- Calls PatchStatus(Parsed)
- Calls PatchTotals again (final reconcile trigger)
- Call order: PatchRaw → PostItem×N → PatchTotals → PatchStatus → PatchTotals

### OCR & Stream Handling

- OCR transient exception retry with fresh stream
- OCR non-transient exception bubbling
- Empty OCR text handling (still patches raw text)

### Item Posting Rules

- Adjustment/Discount items are skipped
- Item POSTs are not retried (single attempt only)
- Exact API call verification

### Totals Handling

- OCR-provided totals sent unchanged
- Missing subtotal computed from items
- Missing total computed from subtotal + tax + tip
- Proper rounding to 2 decimal places

### Finalization & Status

- PatchStatus(Parsed) after first PatchTotals
- Second PatchTotals after status flip
- Zero items still results in Parsed status

### Locking & Idempotency

- 412 response causes early exit
- Lock creation and deletion in finally block
- Lock cleanup even on exceptions

### Retry Policy

- OCR transient errors retry with fresh streams
- API calls retry per configured policy
- Item posts do not retry
- Timeout handling and final TimeoutException

### Error Handling & Cleanup

- API failures after item posting surface error
- Lock deletion in finally block
- Proper exception propagation

### Edge Cases

- Huge blob (>50MB) throws and deletes lock
- IsSane=false still processes and sets status
- Zero items still sets Parsed status

## Running the Tests

```bash
cd Functions/Functions.Tests
dotnet test
```

## Test Dependencies

- **xUnit**: Testing framework
- **Moq**: Mocking framework
- **Azure.Storage.Blobs**: Azure blob storage mocking
- **Microsoft.Extensions.Logging.Abstractions**: Logging abstractions

## Key Testing Patterns

1. **Mock Setup**: Comprehensive mocking of all dependencies
2. **Call Verification**: Exact verification of API call counts and parameters
3. **Exception Testing**: Both success and failure scenarios
4. **Integration Testing**: End-to-end workflow validation
5. **Edge Case Coverage**: Boundary conditions and error states
6. **Async Testing**: Proper async/await pattern testing
7. **Cancellation Testing**: CancellationToken handling verification
