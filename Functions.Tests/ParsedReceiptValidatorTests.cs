using System;
using Xunit;
using Functions.Contracts.Parsing;
using Functions.Validation;

namespace Functions.Tests
{
    public class ParsedReceiptValidatorTests
    {
        [Fact]
        public void DiscountedSubtotal_IsAccepted_AndMathBalances()
        {
            var parsed = new ParsedReceiptV1(
                Version: "parsed-receipt-v1",
                Merchant: new Merchant(Name: null, Address: null, Phone: null),
                Datetime: null,
                Currency: "USD",
                Items: new List<ParsedItem>
                {
                    new ParsedItem("Butter Chicken (Makhani)", 1m, 22.79m, 22.79m, Notes: null),
                    new ParsedItem("Papadum (2pc)",           1m,  2.99m,  2.99m, Notes: null),
                },
                SubTotal: 20.62m,   // post-discount subtotal
                Tax: 1.65m,
                Tip: 2.00m,
                Total: 24.27m,
                Confidence: 1.0,
                Issues: new List<string>()
            );

            var ok = ParsedReceiptValidator.TryValidate(parsed, out var err, out var itemsSum);

            Assert.True(ok, $"Validator failed: {err}");
            Assert.Equal(25.78m, Math.Round(itemsSum, 2, MidpointRounding.AwayFromZero));
        }
    }
}
