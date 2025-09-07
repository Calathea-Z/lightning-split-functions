using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Functions.Contracts.Receipts
{
    public sealed record CreateReceiptItemDto(
        string Label,
        decimal Qty,
        decimal UnitPrice,
        string? Unit = null,
        string? Category = null,
        string? Notes = null,
        int Position = 0,
        decimal? Discount = null,
        decimal? Tax = null
    );
}
