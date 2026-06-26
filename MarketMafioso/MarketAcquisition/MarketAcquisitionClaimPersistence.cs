namespace MarketMafioso.MarketAcquisition;

internal static class MarketAcquisitionClaimPersistence
{
    public static (MarketAcquisitionClaimView Claim, string? AcceptIdempotencyKey, string? RejectIdempotencyKey)? Restore(
        Configuration config)
    {
        var stored = config.ActiveMarketAcquisitionClaim;
        if (stored == null ||
            string.IsNullOrWhiteSpace(stored.Id) ||
            string.IsNullOrWhiteSpace(stored.ClaimToken))
        {
            return null;
        }

        return (
            new MarketAcquisitionClaimView
            {
                Id = stored.Id,
                Status = stored.Status,
                TargetCharacterName = stored.TargetCharacterName,
                TargetWorld = stored.TargetWorld,
                Region = stored.Region,
                ItemId = stored.ItemId,
                ItemName = stored.ItemName,
                QuantityMode = stored.QuantityMode,
                Quantity = stored.Quantity,
                HqPolicy = stored.HqPolicy,
                MaxUnitPrice = stored.MaxUnitPrice,
                MaxTotalGil = stored.MaxTotalGil,
                WorldMode = stored.WorldMode,
                ClaimToken = stored.ClaimToken,
            },
            stored.AcceptIdempotencyKey,
            stored.RejectIdempotencyKey);
    }

    public static void Save(
        Configuration config,
        MarketAcquisitionClaimView claim,
        string? acceptIdempotencyKey,
        string? rejectIdempotencyKey)
    {
        config.ActiveMarketAcquisitionClaim = new PersistedMarketAcquisitionClaim
        {
            Id = claim.Id,
            Status = claim.Status,
            TargetCharacterName = claim.TargetCharacterName,
            TargetWorld = claim.TargetWorld,
            Region = claim.Region,
            ItemId = claim.ItemId,
            ItemName = claim.ItemName,
            QuantityMode = claim.QuantityMode,
            Quantity = claim.Quantity,
            HqPolicy = claim.HqPolicy,
            MaxUnitPrice = claim.MaxUnitPrice,
            MaxTotalGil = claim.MaxTotalGil,
            WorldMode = claim.WorldMode,
            ClaimToken = claim.ClaimToken,
            AcceptIdempotencyKey = acceptIdempotencyKey,
            RejectIdempotencyKey = rejectIdempotencyKey,
        };
    }

    public static void Clear(Configuration config)
    {
        config.ActiveMarketAcquisitionClaim = null;
    }
}
