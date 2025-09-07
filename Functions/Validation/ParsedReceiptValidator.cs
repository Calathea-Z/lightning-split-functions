using Functions.Contracts.Parsing;

namespace Functions.Validation;

public static class ParsedReceiptValidator
{
    public static bool TryValidate(ParsedReceiptV1 r, out string error, out decimal itemsSum)
    {
        error = "";
        itemsSum = 0m;

        if (r.Version != "parsed-receipt-v1") { error = "bad_version"; return false; }
        if (string.IsNullOrWhiteSpace(r.Currency)) { error = "missing_currency"; return false; }
        if (r.Items is null || r.Items.Count == 0) { error = "no_items"; return false; }

        foreach (var it in r.Items)
        {
            if (string.IsNullOrWhiteSpace(it.Description)) { error = "item_missing_description"; return false; }
            if (it.Quantity < 0) { error = "qty_negative"; return false; }
            if (it.UnitPrice < 0 || it.LineTotal < 0) { error = "price_negative"; return false; }
            itemsSum += it.LineTotal;
        }

        // If subtotal present, check math within 2 cents.
        if (r.SubTotal is decimal s && Math.Abs(s - itemsSum) > 0.02m)
        {
            error = $"items_do_not_sum_to_subtotal (items={itemsSum}, subtotal={s})";
            return false;
        }

        return true;
    }
}