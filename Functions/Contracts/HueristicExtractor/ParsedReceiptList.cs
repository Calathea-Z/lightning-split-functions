namespace Functions.Contracts.HueristicExtractor
{
    public sealed record ParsedReceipt(List<ParsedItem> Items, decimal? Subtotal, decimal? Tax, decimal? Tip, decimal? Total, bool IsSane);
}
