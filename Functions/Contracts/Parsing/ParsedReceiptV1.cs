namespace Functions.Contracts.Parsing
{
    public sealed record ParsedReceiptV1(
        string Version,
        Merchant Merchant,
        string? Datetime,
        string Currency,
        List<ParsedItem> Items,
        decimal? SubTotal,
        decimal? Tax,
        decimal? Tip,
        decimal? Total,
        double Confidence,
        List<string> Issues
    );
}
