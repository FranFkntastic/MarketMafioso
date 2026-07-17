using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterWorkspaceViewPresenterTests
{
    [Theory]
    [InlineData(false, OutfitterWorkspaceView.Planner)]
    [InlineData(true, OutfitterWorkspaceView.Advisor)]
    public void Build_AlwaysExposesBothViews(bool advisorSelected, OutfitterWorkspaceView expectedSelection)
    {
        var options = OutfitterWorkspaceViewPresenter.Build(advisorSelected);

        Assert.Collection(
            options,
            planner => Assert.Equal(OutfitterWorkspaceView.Planner, planner.View),
            advisor => Assert.Equal(OutfitterWorkspaceView.Advisor, advisor.View));
        Assert.Equal(expectedSelection, Assert.Single(options, option => option.Selected).View);
    }
}
