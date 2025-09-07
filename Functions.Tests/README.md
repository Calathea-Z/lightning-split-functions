# Functions Unit Tests

This test suite provides comprehensive coverage for the Functions app, specifically testing the receipt parsing workflow and all its components. The test suite includes **46 tests** across **6 test classes** covering all aspects of the receipt parsing pipeline.

## Test Coverage

### 1. ReceiptParseOrchestratorTests.cs

Tests the main orchestrator that coordinates the entire receipt parsing workflow:

- **Happy Path (Sunny Mart)**: Tests the complete workflow with 5 items, proper totals, and correct API call order using new request objects
- **OCR Retry Logic**: Verifies transient exception handling with fresh streams
- **Empty OCR Text**: Ensures empty OCR results still patch raw text and proceed
- **Item Filtering**: Tests that "Adjustment" and "Discount/Adjustment" items are skipped
- **Item Posting**: Verifies no retries on item POST failures
- **Totals Handling**: Tests missing subtotal/total computation and rounding
- **Parse Metadata**: Tests PatchParseMeta calls with parsing engine information
- **Finalization**: Verifies PatchParseMeta → PatchStatus → second PatchTotals call order
- **Locking**: Tests idempotency with 412 responses and lock cleanup
- **Error Handling**: Ensures proper cleanup even when exceptions occur
- **Edge Cases**: Tests huge blobs, IsSane=false, and zero items scenarios
- **Configuration**: Tests IConfiguration dependency for LLM model configuration

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

- **PATCH Operations**: Tests PatchRawText, PatchTotals, PatchStatus, PatchParseMeta
- **POST Operations**: Tests PostItem and PostParseError
- **Request Objects**: Tests new API structure using request objects (UpdateRawTextRequest, UpdateTotalsRequest, etc.)
- **Retry Policies**: Verifies different retry behaviors for different operations
- **Error Handling**: Tests various HTTP status code scenarios including resilient PostParseErrorAsync
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

### 6. ParsedReceiptValidatorTests.cs

Tests the receipt validation logic:

- **Discounted Subtotal Validation**: Tests validation of receipts with post-discount subtotals
- **Math Balance Verification**: Ensures item totals match expected calculations
- **Validation Logic**: Tests the TryValidate method with various receipt scenarios

## Test Scenarios Covered

### Happy Path (Sunny Mart)

- Posts raw text once using UpdateRawTextRequest
- Posts exactly 5 items (no adjustments) using CreateReceiptItemRequest
- Calls PatchTotals with UpdateTotalsRequest(19.25, 1.54, 2.00, 22.79)
- Calls PatchParseMeta with UpdateParseMetaRequest
- Calls PatchStatus with UpdateStatusRequest(Parsed)
- Calls PatchTotals again (final reconcile trigger)
- Call order: PatchRaw → PostItem×N → PatchTotals → PatchParseMeta → PatchStatus → PatchTotals

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

- PatchParseMeta with parsing metadata after first PatchTotals
- PatchStatus(Parsed) after parse metadata update
- Second PatchTotals after status flip
- Zero items still results in ParsedNeedsReview status

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
- **Microsoft.Extensions.Configuration**: Configuration mocking
- **Api.Abstractions.Transport**: Request/response objects for API communication

## API Structure

The tests now use the updated API structure with request objects:

- **UpdateRawTextRequest**: For patching raw OCR text
- **UpdateTotalsRequest**: For updating receipt totals (subtotal, tax, tip, total)
- **UpdateStatusRequest**: For updating receipt status
- **UpdateParseMetaRequest**: For updating parsing metadata (engine, model, version, etc.)
- **CreateReceiptItemRequest**: For creating receipt items

This structure provides better type safety and clearer API contracts compared to the previous individual parameter approach.

## Key Testing Patterns

1. **Mock Setup**: Comprehensive mocking of all dependencies including IConfiguration
2. **Request Object Testing**: Verification of request object properties and structure
3. **Call Verification**: Exact verification of API call counts and request objects
4. **Exception Testing**: Both success and failure scenarios with proper error handling
5. **Integration Testing**: End-to-end workflow validation with new API structure
6. **Edge Case Coverage**: Boundary conditions and error states
7. **Async Testing**: Proper async/await pattern testing
8. **Cancellation Testing**: CancellationToken handling verification
9. **Resilient Error Handling**: Tests for graceful degradation (e.g., PostParseErrorAsync)
