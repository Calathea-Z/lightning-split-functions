using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Functions.Contracts.HueristicExtractor
{
    public sealed record ParsedItem(string Description, int Qty, decimal UnitPrice, decimal? TotalPrice);
}
