namespace Functions.Dtos.Receipts;

public sealed record UpdateTotalsDto(decimal? SubTotal, decimal? Tax, decimal? Tip, decimal? Total);