using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.Tests.MarketDiagnostics;

public sealed class RetainerHistoryParserTests
{
    [Fact]
    public void Parse_FindsSaleRowsInsideSharedAtkArrays()
    {
        var soldAt = DateTimeOffset.Parse("2026-07-24T12:05:00Z");
        int[] numbers =
        [
            999,
            12,
            300,
            0,
            0,
            0,
            (int)soldAt.ToUnixTimeSeconds(),
            4745,
            1234,
            500,
            0,
            0,
            1,
            (int)soldAt.AddMinutes(-2).ToUnixTimeSeconds(),
            7777,
            5678,
            999,
        ];
        string[] strings =
        [
            "unrelated",
            "300 gil",
            "3",
            "Buyer One",
            "7/24/2026",
            "Orange Juice",
            "500 gil",
            "2",
            "Buyer Two",
            "7/24/2026",
            "Rare Widget",
        ];

        var sales = RetainerHistoryParser.Parse(
            numbers,
            strings,
            itemId => itemId switch
            {
                4745 => "Orange Juice",
                7777 => "Rare Widget",
                _ => null,
            },
            soldAt.AddMinutes(1));

        Assert.Collection(
            sales,
            sale =>
            {
                Assert.Equal((uint)4745, sale.ItemId);
                Assert.Equal((uint)3, sale.Quantity);
                Assert.Equal((uint)100, sale.UnitPrice);
                Assert.Equal((ulong)300, sale.TotalGil);
                Assert.False(sale.IsHq);
                Assert.Equal("Buyer One", sale.BuyerName);
            },
            sale =>
            {
                Assert.Equal((uint)7777, sale.ItemId);
                Assert.Equal((uint)2, sale.Quantity);
                Assert.Equal((uint)250, sale.UnitPrice);
                Assert.True(sale.IsHq);
            });
    }

    [Fact]
    public void Parse_RejectsCoincidentalIntegersWithoutMatchingItemStrings()
    {
        var now = DateTimeOffset.Parse("2026-07-24T12:05:00Z");
        var numbers = new[]
        {
            100,
            0,
            0,
            0,
            (int)now.ToUnixTimeSeconds(),
            4745,
            1234,
        };

        Assert.Empty(RetainerHistoryParser.Parse(
            numbers,
            ["100 gil", "1", "Buyer", "Today", "Different Item"],
            itemId => itemId == 4745 ? "Orange Juice" : null,
            now));
    }
}
