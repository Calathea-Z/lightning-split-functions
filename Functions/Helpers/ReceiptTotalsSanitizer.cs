using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Functions.Helpers
{
    public static class ReceiptTotalsSanitizer
    {
        /// <summary>
        /// If numbers look like cents-scaled dollars (e.g., 11700 instead of 117),
        /// convert by dividing by 100. Uses sanity rules and optional reference sum.
        /// </summary>
        public static (decimal sub, decimal? tax, decimal? tip, decimal total) Normalize(
            decimal sub, decimal? tax, decimal? tip, decimal total,
            decimal? sumOfLineItems = null)
        {
            bool looksScaled =
                (total >= 1000m && sumOfLineItems.HasValue && sumOfLineItems.Value < 1000m)
                || (total >= 1000m && sub < total && sub < 1000m)
                || (sub >= 1000m && (tax ?? 0) < 100m && (tip ?? 0) < 100m);

            if (looksScaled)
            {
                sub = decimal.Round(sub / 100m, 2);
                tax = tax.HasValue ? decimal.Round(tax.Value / 100m, 2) : null;
                tip = tip.HasValue ? decimal.Round(tip.Value / 100m, 2) : null;
                total = decimal.Round(total / 100m, 2);
            }

            if (total == 0m)
                total = decimal.Round(sub + (tax ?? 0m) + (tip ?? 0m), 2);

            return (sub, tax, tip, total);
        }
    }
}
