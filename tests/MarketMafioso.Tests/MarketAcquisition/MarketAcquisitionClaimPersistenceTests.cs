namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionClaimPersistenceTests
{
    [Fact]
    public void SaveAndRestoreClaimPreservesRequestAndIdempotencyKeys()
    {
        var config = new MarketMafioso.Configuration();
        var claim = new MarketMafioso.MarketAcquisition.MarketAcquisitionClaimView
        {
            Id = "request-1",
            Status = "Claimed",
            Origin = MarketMafioso.MarketAcquisition.MarketAcquisitionOrigins.ClientQuickShop,
            CreatedByPluginInstanceId = "plugin-instance",
            TargetCharacterName = "Wei Ning",
            TargetWorld = "Gilgamesh",
            Region = "North America",
            ItemId = 5057,
            ItemName = "Darksteel Nugget",
            QuantityMode = "AllBelowThreshold",
            Quantity = 999,
            HqPolicy = "Either",
            MaxUnitPrice = 70,
            MaxTotalGil = 0,
            WorldMode = "Selected",
            SelectedWorlds = ["Gilgamesh", "Siren"],
            ClaimToken = "claim-token",
        };

        MarketMafioso.MarketAcquisition.MarketAcquisitionClaimPersistence.Save(
            config,
            claim,
            "accept-key",
            "reject-key");

        var restored = MarketMafioso.MarketAcquisition.MarketAcquisitionClaimPersistence.Restore(config);

        Assert.NotNull(restored);
        Assert.Equal("request-1", restored.Value.Claim.Id);
        Assert.Equal("claim-token", restored.Value.Claim.ClaimToken);
        Assert.Equal("Darksteel Nugget", restored.Value.Claim.ItemName);
        Assert.Equal(MarketMafioso.MarketAcquisition.MarketAcquisitionOrigins.ClientQuickShop, restored.Value.Claim.Origin);
        Assert.Equal("plugin-instance", restored.Value.Claim.CreatedByPluginInstanceId);
        Assert.Equal(["Gilgamesh", "Siren"], restored.Value.Claim.SelectedWorlds);
        Assert.Equal("accept-key", restored.Value.AcceptIdempotencyKey);
        Assert.Equal("reject-key", restored.Value.RejectIdempotencyKey);
    }

    [Fact]
    public void ClearRemovesPersistedClaim()
    {
        var config = new MarketMafioso.Configuration
        {
            ActiveMarketAcquisitionClaim = new MarketMafioso.PersistedMarketAcquisitionClaim
            {
                Id = "request-1",
                ClaimToken = "claim-token",
            },
        };

        MarketMafioso.MarketAcquisition.MarketAcquisitionClaimPersistence.Clear(config);

        Assert.Null(config.ActiveMarketAcquisitionClaim);
    }
}
