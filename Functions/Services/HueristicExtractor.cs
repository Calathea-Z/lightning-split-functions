// Functions/Services/HeuristicExtractor.cs
using Functions.Helpers;
using System.Text.RegularExpressions;
using System.Linq;
using Functions.Contracts.HueristicExtractor;

namespace Functions.Services
{

    public static class HeuristicExtractor
    {
        // -------- Patterns --------
        private static readonly Regex MoneyOnly = new(
            @"^\s*\$?\s*-?\d{1,6}(?:[.,]\d{2})\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex InlineMoney = new(
            @"\$?\s*-?\d{1,6}(?:[.,]\d{2})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PriceAtEnd = new(
            @"^(?<desc>.+?)\s+(?<price>\$?\s*\d{1,6}(?:[.,]\d{2})?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UnitThenTotal = new(
            @"^(?<desc>.+?)\s+(?<unit>\$?\s*\d{1,6}(?:[.,]\d{2})?)\s+(?<total>\$?\s*\d{1,6}(?:[.,]\d{2})?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UnitThenNoTotal = new(
            @"^(?<desc>.+?)\s+(?<unit>\$?\s*\d{1,6}(?:[.,]\d{2})?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex QtyTimesUnit = new(
            @"^(?<desc>.+?)\s+(?<qty>\d{1,3})\s*[x×]\s*(?<unit>\$?\s*\d{1,6}(?:[.,]\d{2})?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LeadingQtyThenUnit = new(
            @"^(?<qty>\d{1,3})\s*[x×]\s*(?<desc>.+?)\s+\$?\s*(?<unit>\d{1,6}(?:[.,]\d{2})?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // qty anywhere in the description (prefix OR suffix)
        private static readonly Regex QtyAnywhere = new(
            @"(?:(?<q1>\d{1,3})\s*[x×]\b)|(?:\b[x×]\s*(?<q2>\d{1,3}))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TotalsLabel = new(
            @"^(?<lab>subtotal|sub\s*total|total\s*amount|total|amount\s*due|sales\s*tax|tax|tip|gratuity|service|cash|change)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // -------- Entry point --------
        public static ParsedReceipt Extract(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new ParsedReceipt(new List<ParsedItem>(), null, null, null, null, false);

            // 0) Normalize & split
            var rawLines = raw.Split('\n')
                .Select(l => l.Replace("\r", "").Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            // 0.5) Fix interleaved triples like: [desc-only][desc+unit][money-only]
            // Example: Water / Muffin x2 $1.25 / $2.00  →  Water $1.25 / Muffin x2 / $2.00
            FixInterleavedTriples(rawLines);

            // 1) Pre-merge: attach money-only lines to the previous line (totals-aware + qty-guard)
            var lines = MergeMoneyOnlyLines(rawLines);

            var items = new List<ParsedItem>();
            decimal? subtotal = null, tax = null, tip = null, total = null;

            // 1.5) Preview printed SUBTOTAL if present (for tie-breakers near totals)
            var previewSubtotal = PreviewSubtotal(lines);

            // 2) Parse with lookahead for totals & two-line UnitThenTotal
            bool inTotals = false;
            decimal runningItemsSum = 0m;

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                // Flip into totals mode as soon as any totals/settlement label appears
                if (TotalsLabel.IsMatch(line)) inTotals = true;

                // Try totals on this or next line; consume the next line if used
                if (TryTotalsOnThisOrNextLine(lines, i, out var setWhich, out var val, out var consumedNext))
                {
                    switch (setWhich)
                    {
                        case "subtotal": if (subtotal is null) subtotal = val; break;
                        case "tax": if (tax is null) tax = val; break;
                        case "tip": if (tip is null) tip = val; break;
                        case "total": if (total is null) total = val; break;
                            // cash/change intentionally ignored for computation
                    }

                    if (consumedNext) i++; // consume the money-only line we peeked
                    continue;              // never treat totals lines as items
                }

                // Once we are in totals mode, stop parsing items
                if (inTotals) continue;

                // --- Item parsing (order matters) ---
                if (TryMatch(LeadingQtyThenUnit, line, out var it0))
                {
                    items.Add(it0!);
                    runningItemsSum = Round2(runningItemsSum + (it0!.TotalPrice ?? (it0.UnitPrice * it0.Qty)));
                    continue;
                }
                if (TryMatch(UnitThenTotal, line, out var it1))
                {
                    items.Add(it1!);
                    runningItemsSum = Round2(runningItemsSum + (it1!.TotalPrice ?? (it1.UnitPrice * it1.Qty)));
                    continue;
                }
                if (TryTwoLineUnitTotal(lines, i, out var it1b))
                {
                    items.Add(it1b!);
                    runningItemsSum = Round2(runningItemsSum + (it1b!.TotalPrice ?? (it1b.UnitPrice * it1b.Qty)));
                    i++; continue;
                }
                if (TryMatch(QtyTimesUnit, line, out var it2))
                {
                    items.Add(it2!);
                    runningItemsSum = Round2(runningItemsSum + (it2!.TotalPrice ?? (it2.UnitPrice * it2.Qty)));
                    continue;
                }
                if (TryMatch(PriceAtEnd, line, out var it3))
                {
                    items.Add(it3!);
                    runningItemsSum = Round2(runningItemsSum + (it3!.TotalPrice ?? (it3.UnitPrice * it3.Qty)));
                    continue;
                }

                // Bare description followed by money-only next line (ambiguous: could be UNIT or TOTAL)
                if (i + 1 < lines.Count && !TotalsLabel.IsMatch(line) && IsMoneyOnly(lines[i + 1]))
                {
                    if (TryMoney(lines[i + 1], out var money))
                    {
                        var rawDesc = Clean(StripQtyPrefix(line));
                        if (!string.IsNullOrWhiteSpace(rawDesc))
                        {
                            var qty = ExtractQtyFromDesc(line) ?? 1;

                            if (qty > 1)
                            {
                                // Two interpretations:
                                // (A) money is UNIT → line total = unit * qty
                                // (B) money is TOTAL → unit = total / qty
                                decimal unitA = money;
                                decimal totalA = Round2(unitA * qty);

                                decimal totalB = money;
                                decimal unitB = qty > 0 ? Round2(totalB / qty) : totalB;

                                // Choose the interpretation that keeps us closer to printed subtotal (when available)
                                decimal sumIfA = Round2(runningItemsSum + totalA);
                                decimal sumIfB = Round2(runningItemsSum + totalB);

                                bool chooseB = false;
                                if (previewSubtotal is decimal sub2)
                                    chooseB = Math.Abs(sumIfB - sub2) + 0.0001m < Math.Abs(sumIfA - sub2);

                                if (chooseB)
                                {
                                    items.Add(new ParsedItem(rawDesc, qty, unitB, totalB));
                                    runningItemsSum = sumIfB;
                                }
                                else
                                {
                                    items.Add(new ParsedItem(rawDesc, qty, unitA, totalA));
                                    runningItemsSum = sumIfA;
                                }

                                i++; // consume money line
                                continue;
                            }

                            // qty == 1: keep smart tie-breaker near totals using printed Subtotal
                            if (NextNextIsTotals(lines, i) && previewSubtotal is decimal sub)
                            {
                                decimal asTotal = Round2(runningItemsSum + money);
                                decimal asUnit = Round2(runningItemsSum + money); // qty=1 → same

                                var pickTotal = Math.Abs(asTotal - sub) + 0.0001m < Math.Abs(asUnit - sub);

                                var unit = money;
                                var totalLine = unit; // qty=1
                                if (pickTotal)
                                {
                                    items.Add(new ParsedItem(rawDesc, 1, unit, totalLine));
                                    runningItemsSum = asTotal;
                                }
                                else
                                {
                                    items.Add(new ParsedItem(rawDesc, 1, unit, totalLine));
                                    runningItemsSum = asUnit;
                                }
                            }
                            else
                            {
                                // default for qty=1 away from totals
                                var unit = money;
                                items.Add(new ParsedItem(rawDesc, 1, unit, unit));
                                runningItemsSum = Round2(runningItemsSum + unit);
                            }

                            i++; // consume money line
                            continue;
                        }
                    }
                }

                // If we reach here, the line didn't match any known item shape; skip.
            }

            // 3) Fallback: if items still empty, re-scan single-line price-at-end after merges
            if (items.Count == 0)
            {
                foreach (var line in lines)
                {
                    if (TotalsLabel.IsMatch(line)) continue;
                    var m = PriceAtEnd.Match(line);
                    if (!m.Success) continue;
                    var desc = Clean(StripQtyPrefix(m.Groups["desc"].Value));
                    if (desc.Length < 2) continue;
                    if (!TryMoney(m.Groups["price"].Value, out var p)) continue;
                    items.Add(new ParsedItem(desc, 1, p, p));
                }
                items = items
                    .GroupBy(i => (i.Description.ToLowerInvariant(), i.TotalPrice ?? i.UnitPrice))
                    .Select(g => g.First()).Take(40).ToList();
            }

            // 4) Compute totals if missing
            var sumItems = items.Sum(i => i.TotalPrice ?? (i.UnitPrice * i.Qty));
            if (subtotal is null && items.Count > 0) subtotal = Round2(sumItems);
            if (total is null && items.Count > 0) total = Round2(sumItems + (tax ?? 0m) + (tip ?? 0m));

            var isSane = items.Count > 0 || (subtotal ?? 0m) > 0m || (total ?? 0m) > 0m;
            return new ParsedReceipt(items, subtotal, tax, tip, total, isSane);
        }

        // -------- Helpers --------

        // Interleaved triple fixer:
        // If we see [i]=desc-without-price, [i+1]=desc+unit (UnitThenNoTotal), [i+2]=money-only,
        // move the unit from [i+1] to [i] and strip it from [i+1].
        private static void FixInterleavedTriples(List<string> lines)
        {
            for (int i = 0; i + 2 < lines.Count; i++)
            {
                var a = lines[i].Trim();
                var b = lines[i + 1].Trim();
                var c = lines[i + 2].Trim();

                if (TotalsLabel.IsMatch(a) || TotalsLabel.IsMatch(b)) continue;

                bool aHasPrice = InlineMoney.IsMatch(a);
                var m = UnitThenNoTotal.Match(b);

                if (!aHasPrice && m.Success && IsMoneyOnly(c))
                {
                    // Heuristic safety: 'a' must look like an item name
                    if (LooksLikeItemName(a))
                    {
                        var unitText = m.Groups["unit"].Value.Trim();
                        var descOnlyB = m.Groups["desc"].Value.Trim();

                        // Move the unit to line A; strip from line B
                        lines[i] = $"{a} {unitText}";
                        lines[i + 1] = descOnlyB;

                        // Advance i one step so we don't reprocess the just-modified B as a fresh triple
                        i++;
                    }
                }
            }
        }

        // Totals-aware merge: only attach money-only to plausible item names and only before totals
        // NEVER attach to a previous line that contains a qty token (e.g., "2x", "x2", "×2")
        private static List<string> MergeMoneyOnlyLines(List<string> src)
        {
            var merged = new List<string>(src.Count);
            bool inTotals = false;

            foreach (var raw in src)
            {
                var line = raw.Trim();

                if (TotalsLabel.IsMatch(line))
                    inTotals = true;

                if (!inTotals && merged.Count > 0 && IsMoneyOnly(line))
                {
                    var prev = merged[^1].Trim();

                    // Do not attach if previous contains a qty token; let item parser handle it
                    bool prevHasQty = QtyAnywhere.IsMatch(prev);

                    // Guard: don't attach to totals-like or price-complete lines; only to itemish text
                    if (!prevHasQty && !TotalsLabel.IsMatch(prev) && !PriceAtEnd.IsMatch(prev) && LooksLikeItemName(prev))
                    {
                        merged[^1] = $"{prev} {line}";
                        continue;
                    }
                }

                merged.Add(line);
            }

            return merged;
        }

        private static bool TryTotalsOnThisOrNextLine(
            IReadOnlyList<string> lines, int i, out string? which, out decimal val, out bool consumedNext)
        {
            which = null; val = 0m; consumedNext = false;
            var line = lines[i];

            if (TotalsLabel.IsMatch(line))
            {
                // Try in-line money
                var m = Regex.Match(line, @"(\$?\s*-?\d{1,6}(?:[.,]\d{2}))\s*$", RegexOptions.IgnoreCase);
                if (m.Success && TryMoney(m.Groups[1].Value, out val))
                {
                    which = NormalizeTotalLabel(line);
                    return which is not null;
                }

                // Else look at next line for money-only
                if (i + 1 < lines.Count && IsMoneyOnly(lines[i + 1]) && TryMoney(lines[i + 1], out val))
                {
                    which = NormalizeTotalLabel(line);
                    consumedNext = which is not null;
                    return consumedNext;
                }
            }
            return false;
        }

        private static string? NormalizeTotalLabel(string s)
        {
            s = s.ToLowerInvariant();
            if (s.Contains("subtotal") || s.Contains("sub total")) return "subtotal";
            if (Regex.IsMatch(s, @"\bsales\s*tax\b|\btax\b")) return "tax";
            if (s.Contains("tip") || s.Contains("gratuity") || s.Contains("service")) return "tip";
            if (s.Contains("total amount") || s.StartsWith("total") || s.Contains("amount due")) return "total";
            if (s.StartsWith("cash")) return "cash";
            if (s.StartsWith("change")) return "change";
            return null;
        }

        private static bool TryTwoLineUnitTotal(IReadOnlyList<string> lines, int i, out ParsedItem? item)
        {
            item = null;
            var line = lines[i];
            var m = UnitThenNoTotal.Match(line);
            if (!m.Success) return false;

            if (i + 1 < lines.Count && IsMoneyOnly(lines[i + 1]) &&
                TryMoney(m.Groups["unit"].Value, out var unit) &&
                TryMoney(lines[i + 1], out var tot))
            {
                var desc = Clean(m.Groups["desc"].Value);
                var qty = GuessQty(unit, tot);
                item = new ParsedItem(desc, qty, unit, tot);
                return true;
            }
            return false;
        }

        private static bool TryMatch(Regex rx, string line, out ParsedItem? item)
        {
            item = null;
            var m = rx.Match(line);
            if (!m.Success) return false;

            if (rx == PriceAtEnd)
            {
                var desc = Clean(StripQtyPrefix(m.Groups["desc"].Value));
                if (TryMoney(m.Groups["price"].Value, out var price))
                    item = new ParsedItem(desc, 1, price, price);
            }
            else if (rx == UnitThenTotal)
            {
                var desc = Clean(m.Groups["desc"].Value);
                if (TryMoney(m.Groups["unit"].Value, out var unit) &&
                    TryMoney(m.Groups["total"].Value, out var tot))
                {
                    var qty = GuessQty(unit, tot);
                    item = new ParsedItem(desc, qty, unit, tot);
                }
            }
            else if (rx == QtyTimesUnit)
            {
                var desc = Clean(m.Groups["desc"].Value);
                if (int.TryParse(m.Groups["qty"].Value, out var qty) &&
                    TryMoney(m.Groups["unit"].Value, out var unit))
                {
                    var tot = Round2(qty * unit);
                    item = new ParsedItem(desc, qty, unit, tot);
                }
            }
            else // LeadingQtyThenUnit
            {
                var desc = Clean(m.Groups["desc"].Value);
                if (int.TryParse(m.Groups["qty"].Value, out var qty) &&
                    TryMoney(m.Groups["unit"].Value, out var unit))
                {
                    var tot = Round2(qty * unit);
                    item = new ParsedItem(desc, qty, unit, tot);
                }
            }
            return item is not null;
        }

        private static void TryPullTotal(string line, string keyPattern, ref decimal? target)
        {
            if (target is not null) return;
            var rx = new Regex($@"^(?:{keyPattern})\b.*?(\$?\s*-?\d{{1,6}}(?:[.,]\d{{2}})?)$",
                               RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var m = rx.Match(line);
            if (m.Success && TryMoney(m.Groups[1].Value, out var val))
                target = val;
        }

        private static bool TryMoney(string s, out decimal val) => Money.TryParse(s, out val);

        private static int GuessQty(decimal unit, decimal total)
        {
            if (unit <= 0) return 1;
            var q = (int)Math.Round(total / unit, MidpointRounding.AwayFromZero);
            return Math.Max(q, 1);
        }

        private static int? ExtractQtyFromDesc(string s)
        {
            var m = QtyAnywhere.Match(s ?? "");
            if (!m.Success) return null;

            if (int.TryParse(m.Groups["q1"].Value, out var q1) && q1 > 0) return q1;
            if (int.TryParse(m.Groups["q2"].Value, out var q2) && q2 > 0) return q2;
            return null;
        }

        private static bool NextNextIsTotals(IReadOnlyList<string> lines, int i)
        {
            // true if there is a line i+2 and it's a totals label
            return (i + 2 < lines.Count) && TotalsLabel.IsMatch(lines[i + 2].Trim());
        }

        private static decimal? PreviewSubtotal(IReadOnlyList<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (!TotalsLabel.IsMatch(line)) continue;

                if (NormalizeTotalLabel(line) == "subtotal")
                {
                    // same-line number?
                    var m = Regex.Match(line, @"(\$?\s*-?\d{1,6}(?:[.,]\d{2}))\s*$", RegexOptions.IgnoreCase);
                    if (m.Success && Money.TryParse(m.Groups[1].Value, out var v)) return v;

                    // next-line money-only?
                    if (i + 1 < lines.Count && IsMoneyOnly(lines[i + 1]) && Money.TryParse(lines[i + 1], out var v2)) return v2;
                }
            }
            return null;
        }

        private static string StripQtyPrefix(string s) => QtyPrefixStrip.Replace(s, "");
        private static readonly Regex QtyPrefixStrip = new(
            @"^\s*\d{1,3}\s*[x×]\s*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string Clean(string s) => Regex.Replace(s, @"\s{2,}", " ").Trim();
        private static decimal Round2(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

        // --- Tiny helpers ---

        private static bool LooksLikeItemName(string s)
        {
            var t = s.Trim();
            if (TotalsLabel.IsMatch(t)) return false;
            if (t.Length == 0) return false;

            bool hasLetters = t.Any(char.IsLetter);
            if (!hasLetters) return false;

            var letters = t.Where(char.IsLetter).ToArray();
            bool mostlyUpper = letters.Length > 0 && letters.All(char.IsUpper) && t.Length <= 24;
            return !mostlyUpper;
        }

        private static bool IsMoneyOnly(string s) => MoneyOnly.IsMatch(s ?? string.Empty);
    }
}
