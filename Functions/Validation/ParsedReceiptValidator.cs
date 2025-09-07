using Functions.Contracts.Parsing;

namespace Functions.Validation;

public static class ParsedReceiptValidator
{
    /// <summary>
    /// Validates a parsed receipt for basic structural integrity and math.
    /// Loosening: accepts discount scenarios where SubTotal &lt; sum(items) IF the total math is consistent:
    ///     Round2(SubTotal) + Round2(Tax) + Round2(Tip) == Round2(Total)  (± $0.02),
    /// and the implied discount isn't absurd (&lt;= 60% of items sum).
    /// </summary>
    public static bool TryValidate(ParsedReceiptV1 r, out string error, out decimal itemsSum)
    {
        error = "";
        itemsSum = 0m;

        // ---------- Structural checks ----------
        if (r.Version != "parsed-receipt-v1") { error = "bad_version"; return false; }
        if (string.IsNullOrWhiteSpace(r.Currency)) { error = "missing_currency"; return false; }
        if (r.Items is null || r.Items.Count == 0) { error = "no_items"; return false; }

        foreach (var it in r.Items)
        {
            if (string.IsNullOrWhiteSpace(it.Description)) { error = "item_missing_description"; return false; }
            if (it.Quantity < 0) { error = "qty_negative"; return false; }
            if (it.UnitPrice < 0 || it.LineTotal < 0) { error = "price_negative"; return false; }

            // If both Quantity and UnitPrice are meaningful, ensure LineTotal ~= Qty * UnitPrice (±$0.02)
            var expected = Round2(it.Quantity * it.UnitPrice);
            if (Math.Abs(expected - it.LineTotal) > 0.02m)
            {
                error = $"line_total_mismatch (qty*price={expected}, line={it.LineTotal})";
                return false;
            }

            itemsSum += it.LineTotal;
        }
        itemsSum = Round2(itemsSum);

        // ---------- Math checks ----------
        var sub = r.SubTotal;                 // post-discount subtotal (if present)
        var tax = Round2(r.Tax ?? 0m);
        var tip = Round2(r.Tip ?? 0m);
        var total = r.Total;

        // If total present, ensure total ~= subtotalOrItems + tax + tip (±$0.02)
        if (total is decimal T)
        {
            var subtotalBasis = Round2(sub ?? itemsSum);
            if (Math.Abs(Round2(subtotalBasis + tax + tip) - T) > 0.02m)
            {
                error = $"total_mismatch (basis={subtotalBasis}, tax={tax}, tip={tip}, total={T})";
                return false;
            }
        }

        // If SubTotal present, check against items sum with discount tolerance.
        if (sub is decimal s)
        {
            var delta = Round2(itemsSum - s);

            // Exact/near match: ok
            if (Math.Abs(delta) <= 0.02m) return true;

            // Discount-tolerant acceptance:
            // Accept when SubTotal < itemsSum, implied discount reasonable, and overall total math (if provided) is consistent.
            if (s < itemsSum && delta > 0m)
            {
                var discountPctCap = Round2(itemsSum * 0.60m); // don't accept bizarre 80–90% "discounts"
                var discountReasonable = delta <= discountPctCap;

                var totalMathOk = true; // default true if Total is missing
                if (total is decimal t)
                {
                    totalMathOk = Math.Abs(Round2(s + tax + tip) - t) <= 0.02m;
                }

                if (discountReasonable && totalMathOk)
                {
                    // Treat as valid discounted receipt
                    return true;
                }
            }

            // Otherwise fail with explicit message
            error = $"items_do_not_sum_to_subtotal (items={itemsSum}, subtotal={s})";
            return false;
        }

        // If we got here, subtotal is absent; structure and (optional) total math already verified.
        return true;
    }

    private static decimal Round2(decimal d) =>
        Math.Round(d, 2, MidpointRounding.AwayFromZero);
}
