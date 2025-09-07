namespace Functions.Contracts.Ocr
{
    public sealed class ParsedResult
    {
        public int FileParseExitCode { get; set; } // 1 = success
        public string? ParsedText { get; set; }
        public string? ErrorMessage { get; set; }
        public string? ErrorDetails { get; set; }
        public string? Message { get; set; }
    }
}
