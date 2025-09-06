namespace Functions.Dtos.Ocr
{
    public sealed class OcrSpaceDto
    {
        public bool IsErroredOnProcessing { get; set; }
        public object? ErrorMessage { get; set; } // array or string
        public string? ErrorDetails { get; set; }
        public ParsedResult[]? ParsedResults { get; set; }
    }
}
