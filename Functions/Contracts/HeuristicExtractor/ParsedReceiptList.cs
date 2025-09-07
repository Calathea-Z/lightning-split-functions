namespace Functions.Contracts.HeuristicExtractor
{
    public sealed record ParsedReceiptList(List<ItemHint> Items, decimal? Subtotal, decimal? Tax, decimal? Tip, decimal? Total, bool IsSane);
}
