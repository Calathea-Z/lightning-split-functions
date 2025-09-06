using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Functions.Helpers
{
    public static class Money
    {
        private static readonly Regex MoneyRegex = new(
            @"\$?\s*([0-9]{1,3}(?:,[0-9]{3})*|[0-9]+)(?:\.([0-9]{1,2}))?",
            RegexOptions.Compiled);

        public static bool TryParse(string s, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var m = MoneyRegex.Match(s);
            if (!m.Success) return false;

            var intPart = m.Groups[1].Value.Replace(",", "");
            var fracPart = m.Groups[2].Success ? m.Groups[2].Value : null;

            if (!long.TryParse(intPart, out var whole)) return false;

            if (string.IsNullOrEmpty(fracPart))
            {
                value = whole;
                return true;
            }

            if (!int.TryParse(fracPart, out var frac)) return false;
            if (fracPart.Length == 1) frac *= 10; // normalize ".5" → ".50"

            value = whole + (frac / 100m);
            return true;
        }
    }
}
