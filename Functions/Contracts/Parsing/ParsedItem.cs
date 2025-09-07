namespace Functions.Contracts.Parsing
{
    public sealed record ParsedItem(
        string Description,
        decimal Quantity,
        decimal UnitPrice,
        decimal LineTotal,
        string? Notes
    );
}
