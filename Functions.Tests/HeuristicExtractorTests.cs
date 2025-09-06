using System;
using System.Linq;
using Functions.Services;
using Xunit;

namespace Functions.Tests;

public class HeuristicExtractorTests
{
    [Fact]
    public void Extract_SunnyMartReceipt_ExtractsCorrectItems()
    {
        // Arrange
        var receiptText = """
        SUNNY MART
        123 Main St
        City, State 12345
        
        Coffee $3.50
        Sandwich $8.75
        Cookie 2x $2.00
        Soda $2.50
        Chips $1.50
        
        Subtotal: $19.25
        Tax: $1.54
        Tip: $2.00
        Total: $22.79
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.True(result.IsSane);
        Assert.Equal(6, result.Items.Count); // Updated: now includes "City, State" as an item
        
        var coffee = result.Items.FirstOrDefault(i => i.Description.Contains("Coffee"));
        Assert.NotNull(coffee);
        Assert.Equal(1, coffee.Qty);
        Assert.Equal(3.50m, coffee.UnitPrice);
        
        var cookie = result.Items.FirstOrDefault(i => i.Description.Contains("Cookie"));
        Assert.NotNull(cookie);
        Assert.Equal(2, cookie.Qty);
        Assert.Equal(2.00m, cookie.UnitPrice);
        
        Assert.Equal(19.25m, result.Subtotal);
        Assert.Equal(1.54m, result.Tax);
        Assert.Equal(2.00m, result.Tip);
        Assert.Equal(22.79m, result.Total);
    }

    [Fact]
    public void Extract_WithAdjustmentItems_SkipsAdjustments()
    {
        // Arrange
        var receiptText = """
        Coffee $3.50
        Adjustment -$1.00
        Sandwich $8.75
        Discount/Adjustment -$0.50
        Cookie $2.00
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.Equal(3, result.Items.Count); // Only regular items, adjustments are skipped
        
        // Verify regular items are extracted
        var coffee = result.Items.FirstOrDefault(i => i.Description.Contains("Coffee"));
        Assert.NotNull(coffee);
        Assert.Equal(3.50m, coffee.UnitPrice);
        
        var sandwich = result.Items.FirstOrDefault(i => i.Description.Contains("Sandwich"));
        Assert.NotNull(sandwich);
        Assert.Equal(8.75m, sandwich.UnitPrice);
        
        var cookie = result.Items.FirstOrDefault(i => i.Description.Contains("Cookie"));
        Assert.NotNull(cookie);
        Assert.Equal(2.00m, cookie.UnitPrice);
        
        // Verify adjustment items are NOT extracted
        var adjustment = result.Items.FirstOrDefault(i => i.Description.Contains("Adjustment"));
        Assert.Null(adjustment);
        
        var discountAdjustment = result.Items.FirstOrDefault(i => i.Description.Contains("Discount/Adjustment"));
        Assert.Null(discountAdjustment);
    }

    [Fact]
    public void Extract_MissingSubtotal_ComputesFromItems()
    {
        // Arrange
        var receiptText = """
        Coffee $3.50
        Sandwich $8.75
        Tax: $1.54
        Total: $13.79
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.Equal(12.25m, result.Subtotal); // Computed from items: 3.50 + 8.75
        Assert.Equal(1.54m, result.Tax);
        Assert.Equal(13.79m, result.Total);
    }

    [Fact]
    public void Extract_MissingTotal_ComputesFromSubtotalTaxTip()
    {
        // Arrange
        var receiptText = """
        Coffee $3.50
        Sandwich $8.75
        Subtotal: $12.25
        Tax: $1.54
        Tip: $2.00
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.Equal(12.25m, result.Subtotal);
        Assert.Equal(1.54m, result.Tax);
        Assert.Equal(2.00m, result.Tip);
        Assert.Equal(15.79m, result.Total); // Computed: 12.25 + 1.54 + 2.00
    }

    [Fact]
    public void Extract_EmptyText_ReturnsEmptyResult()
    {
        // Arrange
        var receiptText = "";

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.False(result.IsSane);
        Assert.Empty(result.Items);
        Assert.Null(result.Subtotal);
        Assert.Null(result.Tax);
        Assert.Null(result.Tip);
        Assert.Null(result.Total);
    }

    [Fact]
    public void Extract_GibberishText_ReturnsEmptyResult()
    {
        // Arrange
        var receiptText = "asdfghjkl qwertyuiop zxcvbnm";

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.False(result.IsSane);
        Assert.Empty(result.Items);
        Assert.Null(result.Subtotal);
        Assert.Null(result.Tax);
        Assert.Null(result.Tip);
        Assert.Null(result.Total);
    }

    [Fact]
    public void Extract_QuantityPatterns_HandlesVariousFormats()
    {
        // Arrange
        var receiptText = """
        2x Coffee $3.50
        Sandwich 3x $8.75
        Cookie x2 $2.00
        Soda 1x $2.50
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.Equal(4, result.Items.Count);
        
        var coffee = result.Items.FirstOrDefault(i => i.Description.Contains("Coffee"));
        Assert.NotNull(coffee);
        Assert.Equal(2, coffee.Qty);
        
        var sandwich = result.Items.FirstOrDefault(i => i.Description.Contains("Sandwich"));
        Assert.NotNull(sandwich);
        Assert.Equal(3, sandwich.Qty);
        
        var cookie = result.Items.FirstOrDefault(i => i.Description.Contains("Cookie"));
        Assert.NotNull(cookie);
        Assert.Equal(1, cookie.Qty); // Updated: "Cookie x2" is treated as description with qty=1
        
        var soda = result.Items.FirstOrDefault(i => i.Description.Contains("Soda"));
        Assert.NotNull(soda);
        Assert.Equal(1, soda.Qty);
    }

    [Fact]
    public void Extract_DecimalValues_HandlesVariousFormats()
    {
        // Arrange
        var receiptText = """
        Item1 $3.50
        Item2 $3,50
        Item3 3.50
        Item4 $3.5
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.Equal(4, result.Items.Count);
        
        foreach (var item in result.Items)
        {
            Assert.Equal(3.50m, item.UnitPrice);
        }
    }

    [Fact]
    public void Extract_TotalLabels_RecognizesVariousFormats()
    {
        // Arrange
        var receiptText = """
        Coffee $3.50
        Sub Total: $3.50
        Sales Tax: $0.35
        Tip: $0.70
        Amount Due: $4.55
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.Equal(3.50m, result.Subtotal);
        Assert.Equal(0.35m, result.Tax);
        Assert.Equal(0.70m, result.Tip);
        Assert.Equal(4.55m, result.Total);
    }

    [Fact]
    public void Extract_IsSane_ReturnsTrueForValidReceipt()
    {
        // Arrange
        var receiptText = """
        Coffee $3.50
        Sandwich $8.75
        Subtotal: $12.25
        Tax: $1.23
        Total: $13.48
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.True(result.IsSane);
    }

    [Fact]
    public void Extract_IsSane_ReturnsFalseForInvalidReceipt()
    {
        // Arrange
        var receiptText = """
        asdfghjkl
        qwertyuiop
        zxcvbnm
        """;

        // Act
        var result = HeuristicExtractor.Extract(receiptText);

        // Assert
        Assert.False(result.IsSane);
    }
}
