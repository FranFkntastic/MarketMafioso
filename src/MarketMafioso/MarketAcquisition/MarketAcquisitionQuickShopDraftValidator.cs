using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionQuickShopDraftValidator
{
    public static MarketAcquisitionQuickShopValidationResult Validate(
        MarketAcquisitionQuickShopDraft draft,
        string? clientApiKey,
        string? characterName,
        string? world)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(clientApiKey))
            errors.Add("Client API key is required.");
        if (string.IsNullOrWhiteSpace(characterName))
            errors.Add("Current character name is required.");
        if (string.IsNullOrWhiteSpace(world))
            errors.Add("Current world is required.");

        ValidateRoute(draft, errors);

        if (draft.Lines.Count == 0)
        {
            errors.Add("At least one quick-shop line is required.");
        }
        else
        {
            for (var index = 0; index < draft.Lines.Count; index++)
                ValidateLine(draft.Lines[index], index + 1, errors);
        }

        return new MarketAcquisitionQuickShopValidationResult(errors);
    }

    private static void ValidateRoute(MarketAcquisitionQuickShopDraft draft, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(draft.Region))
        {
            errors.Add("Region is required.");
        }
        else
        {
            try
            {
                _ = MarketAcquisitionWorldCatalog.NormalizeRegion(draft.Region);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (draft.WorldMode is not ("Recommended" or "AllWorldSweep"))
            errors.Add("World mode must be Recommended or AllWorldSweep.");

        if (draft.WorldMode == "AllWorldSweep")
        {
            var sweepScope = string.IsNullOrWhiteSpace(draft.SweepScope)
                ? "Region"
                : draft.SweepScope.Trim();
            if (sweepScope is not ("Region" or "CurrentDataCenter" or "DataCenters"))
            {
                errors.Add("Sweep scope must be Region, CurrentDataCenter, or DataCenters.");
            }
            else if (sweepScope == "DataCenters")
            {
                if (draft.SweepDataCenters.Count == 0 ||
                    draft.SweepDataCenters.All(string.IsNullOrWhiteSpace))
                {
                    errors.Add("At least one data center is required for a data-center sweep.");
                }
                else if (!string.IsNullOrWhiteSpace(draft.Region))
                {
                    foreach (var dataCenter in draft.SweepDataCenters.Where(dc => !string.IsNullOrWhiteSpace(dc)))
                    {
                        try
                        {
                            _ = MarketAcquisitionWorldCatalog.NormalizeDataCenterName(draft.Region, dataCenter);
                        }
                        catch (InvalidOperationException ex)
                        {
                            errors.Add(ex.Message);
                        }
                    }
                }
            }
        }
    }

    private static void ValidateLine(
        MarketAcquisitionQuickShopLineDraft line,
        int lineNumber,
        List<string> errors)
    {
        if (line.ItemId == 0)
            errors.Add($"Line {lineNumber}: item id is required.");
        if (line.MaxUnitPrice == 0)
            errors.Add($"Line {lineNumber}: max unit price is required before route sync.");

        if (line.QuantityMode is not ("TargetQuantity" or "AllBelowThreshold"))
        {
            errors.Add($"Line {lineNumber}: quantity mode must be TargetQuantity or AllBelowThreshold.");
        }
        else if (line.QuantityMode == "TargetQuantity" && line.TargetQuantity == 0)
        {
            errors.Add($"Line {lineNumber}: target quantity is required.");
        }

        if (!IsSupportedHqPolicy(line.HqPolicy))
            errors.Add($"Line {lineNumber}: HQ policy must be Either, HQOnly, or NQOnly.");
    }

    private static bool IsSupportedHqPolicy(string hqPolicy)
    {
        try
        {
            _ = MarketAcquisitionPolicy.NormalizeHqPolicy(hqPolicy);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

public sealed record MarketAcquisitionQuickShopValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
