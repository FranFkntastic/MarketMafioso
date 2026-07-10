using System;

namespace MarketMafioso.WorkshopPrep;

internal static class WorkshopAssemblyPromptPolicy
{
    public static bool IsContributeItemsPrompt(string text)
    {
        return text.StartsWith("Contribute ", StringComparison.Ordinal) &&
               text.Contains(" to the company project?", StringComparison.Ordinal);
    }

    public static bool IsHighQualityHandoffPrompt(string text)
    {
        return text == "You are about to hand over an HQ item. Proceed?" ||
               text == "Do you really want to trade a high-quality item?";
    }

    public static bool IsRetrieveFinishedProjectPrompt(string text)
    {
        return text == "Retrieve from the company workshop?" ||
               (text.StartsWith("Retrieve ", StringComparison.Ordinal) &&
                text.EndsWith(" from the company workshop?", StringComparison.Ordinal));
    }

    public static bool IsPromptAllowedForPendingConfirmation(
        WorkshopAssemblyPendingConfirmationKind kind,
        string text)
    {
        return kind switch
        {
            WorkshopAssemblyPendingConfirmationKind.ProjectStart =>
                text.StartsWith("Craft ", StringComparison.Ordinal),
            WorkshopAssemblyPendingConfirmationKind.MaterialContribution =>
                IsHighQualityHandoffPrompt(text) || IsContributeItemsPrompt(text),
            WorkshopAssemblyPendingConfirmationKind.PhaseAdvance =>
                text.StartsWith("Advance to the next phase", StringComparison.Ordinal),
            WorkshopAssemblyPendingConfirmationKind.FinalConstruction =>
                text.StartsWith("Complete the construction", StringComparison.Ordinal),
            WorkshopAssemblyPendingConfirmationKind.ProductRetrieval =>
                IsRetrieveFinishedProjectPrompt(text),
            _ => false,
        };
    }

    public static bool IsContributeMaterialsEntry(string text)
    {
        return text.StartsWith("Contribute materials.", StringComparison.Ordinal);
    }

    public static bool IsPostContributionMenuEntry(string text)
    {
        return IsContributeMaterialsEntry(text) ||
               IsAdvancePhaseEntry(text) ||
               IsCompleteConstructionEntry(text) ||
               IsCollectFinishedProductEntry(text);
    }

    public static bool IsAdvancePhaseEntry(string text)
    {
        return text.StartsWith("Advance to the next phase of production.", StringComparison.Ordinal);
    }

    public static bool IsCompleteConstructionEntry(string text)
    {
        return text.StartsWith("Complete the construction of", StringComparison.Ordinal);
    }

    public static bool IsCollectFinishedProductEntry(string text)
    {
        return text.StartsWith("Collect finished product.", StringComparison.Ordinal);
    }
}
