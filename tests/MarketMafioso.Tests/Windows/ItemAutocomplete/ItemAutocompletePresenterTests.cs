using MarketMafioso.Windows.ItemAutocomplete;

namespace MarketMafioso.Tests.Windows.ItemAutocomplete;

public sealed class ItemAutocompletePresenterTests
{
    [Fact]
    public void ResolveSelectedItem_WhenInputExactlyMatchesOneItem_ReturnsThatItem()
    {
        var resolved = ItemAutocompletePresenter.ResolveSelectedItem(
            Options(),
            "fire shard",
            selectedItem: null);

        Assert.NotNull(resolved);
        Assert.Equal(2u, resolved.ItemId);
        Assert.Equal("Fire Shard", resolved.Name);
    }

    [Fact]
    public void ResolveSelectedItem_WhenInputAmbiguouslyMatchesMultipleItems_ReturnsNull()
    {
        var options = new[]
        {
            new AcquisitionItemOption(1, "Copper Ore"),
            new AcquisitionItemOption(2, "Copper Ore"),
        };

        var resolved = ItemAutocompletePresenter.ResolveSelectedItem(
            options,
            "Copper Ore",
            selectedItem: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveSelectedItem_WhenSelectedItemStillMatchesName_KeepsSelectedItem()
    {
        var selected = new AcquisitionItemOption(2, "Copper Ore");
        var options = new[]
        {
            new AcquisitionItemOption(1, "Copper Ore"),
            selected,
        };

        var resolved = ItemAutocompletePresenter.ResolveSelectedItem(
            options,
            "copper ore",
            selected);

        Assert.Same(selected, resolved);
    }

    [Fact]
    public void GetSearchResults_OrdersPrefixMatchesBeforeContainsMatches()
    {
        var results = ItemAutocompletePresenter.GetSearchResults(
            Options(),
            "shard");

        Assert.Equal(
            ["Shard Glue", "Fire Shard", "Lightning Shard"],
            results.Select(item => item.Name).ToArray());
    }

    [Fact]
    public void GetSearchResults_WhenSearchIsTooShort_ReturnsEmpty()
    {
        var results = ItemAutocompletePresenter.GetSearchResults(
            Options(),
            "s");

        Assert.Empty(results);
    }

    [Fact]
    public void FormatDisplayName_WhenNameIsUnique_ReturnsItemNameOnly()
    {
        var option = new AcquisitionItemOption(2, "Fire Shard");

        var label = ItemAutocompletePresenter.FormatDisplayName(Options(), option);

        Assert.Equal("Fire Shard", label);
    }

    [Fact]
    public void FormatDisplayName_WhenNameIsDuplicated_AddsStableDuplicateOrdinal()
    {
        var options = new[]
        {
            new AcquisitionItemOption(10, "Copper Ore"),
            new AcquisitionItemOption(8, "Copper Ore"),
            new AcquisitionItemOption(12, "Silver Ore"),
        };

        var firstLabel = ItemAutocompletePresenter.FormatDisplayName(options, options[1]);
        var secondLabel = ItemAutocompletePresenter.FormatDisplayName(options, options[0]);

        Assert.Equal("Copper Ore - duplicate 1", firstLabel);
        Assert.Equal("Copper Ore - duplicate 2", secondLabel);
    }

    private static IReadOnlyList<AcquisitionItemOption> Options() =>
    [
        new(4, "Lightning Shard"),
        new(2, "Fire Shard"),
        new(8, "Shard Glue"),
    ];
}
