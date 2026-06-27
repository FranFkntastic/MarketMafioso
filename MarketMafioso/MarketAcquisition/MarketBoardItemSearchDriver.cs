using System;
using System.Collections.Generic;
using System.Globalization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketBoardItemSearchDriver
{
    private const string ItemSearchAddon = "ItemSearch";
    private const string ItemSearchResultAddon = "ItemSearchResult";

    private readonly IGameGui gameGui;
    private uint submittedSearchItemId;
    private string? submittedSearchText;

    public MarketBoardItemSearchDriver(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe MarketBoardItemSearchResult Search(uint itemId, string? itemName)
    {
        if (itemId == 0)
            throw new InvalidOperationException("Item id is required before searching the market board.");

        if (string.IsNullOrWhiteSpace(itemName))
            throw new InvalidOperationException($"Item name is required before searching the market board for item {itemId}.");

        var itemSearchResult = gameGui.GetAddonByName<AtkUnitBase>(ItemSearchResultAddon, 1);
        if (IsAddonReady(itemSearchResult))
        {
            ClearSubmittedSearch();
            return new MarketBoardItemSearchResult
            {
                Status = "ListingsReady",
                Message = $"Market board listings are open for {itemName.Trim()} ({itemId}).",
                Details = new Dictionary<string, string?>
                {
                    ["itemSearchResultVisible"] = true.ToString(),
                },
            };
        }

        var addon = gameGui.GetAddonByName<AddonItemSearch>(ItemSearchAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardItemSearchResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Waiting for the market board item search window to open.",
            };
        }

        var searchText = itemName.Trim();
        var mode = (uint)addon->Mode;
        var partialSearchWasEnabled = addon->PartialMatch;
        var agent = AgentItemSearch.Instance();
        var details = new Dictionary<string, string?>
        {
            ["mode"] = FormatMode(mode),
            ["modeRaw"] = mode.ToString(),
            ["partialSearchBefore"] = partialSearchWasEnabled.ToString(),
            ["itemSearchResultVisible"] = false.ToString(),
        };

        if (ChooseAction(mode) == MarketBoardItemSearchAction.ResetMode)
        {
            ClearSubmittedSearch();
            addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, 0);
            return new MarketBoardItemSearchResult
            {
                Status = "ModeReset",
                Message = $"Resetting market board item search mode from {FormatMode(mode)} before searching for {searchText} ({itemId}).",
                Details = details,
            };
        }

        if (ShouldDisablePartialSearch(partialSearchWasEnabled))
        {
            addon->PartialMatch = false;
            if (addon->PartialSearchCheckBox != null)
                addon->PartialSearchCheckBox->AtkComponentButton.SetChecked(false);
        }

        AddAgentDetails(details, agent);
        if (TryOpenExactItemResult(addon, agent, itemId, details))
        {
            ClearSubmittedSearch();
            return new MarketBoardItemSearchResult
            {
                Status = "ItemOpenSent",
                Message = $"Opening exact market board item result for {searchText} ({itemId}); waiting for market listings.",
                Details = details,
            };
        }

        var searchMatchesSubmittedState = IsSubmittedSearchCurrent(submittedSearchItemId, submittedSearchText, itemId, searchText);
        if (searchMatchesSubmittedState)
        {
            var exactItemVisible = AgentContainsItem(agent, itemId);
            var agentIsPartialSearching = agent != null && agent->IsPartialSearching;
            var agentIsItemPushPending = agent != null && agent->IsItemPushPending;
            var shouldWait = ShouldWaitForSubmittedSearch(
                searchMatchesSubmittedState,
                exactItemVisible,
                agentIsPartialSearching,
                agentIsItemPushPending);

            details["searchAlreadySubmitted"] = true.ToString();
            details["submittedSearchExactItemVisible"] = exactItemVisible.ToString();
            details["submittedSearchStillInFlight"] = shouldWait.ToString();
            if (shouldWait)
            {
                details["searchSource"] = "TextInputEnterCallback";
                details["partialSearchAfter"] = addon->PartialMatch.ToString();

                return new MarketBoardItemSearchResult
                {
                    Status = "SearchSent",
                    Message = $"Waiting for market board item search results for {searchText} ({itemId}).",
                    Details = details,
                };
            }

            details["staleSubmittedSearchCleared"] = true.ToString();
            ClearSubmittedSearch();
        }

        if (!TrySubmitSearchWithTextInputEnter(addon, searchText, details))
        {
            return new MarketBoardItemSearchResult
            {
                Status = "SearchSubmitFailed",
                Message = $"Could not submit market board item search for {searchText} ({itemId}); see diagnostics.",
                Details = details,
            };
        }

        submittedSearchItemId = itemId;
        submittedSearchText = searchText;
        details["partialSearchAfter"] = addon->PartialMatch.ToString();
        AddAgentDetails(details, agent);
        details["searchAlreadySubmitted"] = false.ToString();

        return new MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = $"Searching market board item list for {searchText} ({itemId}).",
            Details = details,
        };
    }

    public unsafe MarketBoardItemSearchResult Observe(uint itemId, string? itemName)
    {
        if (itemId == 0)
            throw new InvalidOperationException("Item id is required before observing the market board search state.");

        if (string.IsNullOrWhiteSpace(itemName))
            throw new InvalidOperationException($"Item name is required before observing the market board search state for item {itemId}.");

        var searchText = itemName.Trim();
        var itemSearchResult = gameGui.GetAddonByName<AtkUnitBase>(ItemSearchResultAddon, 1);
        if (IsAddonReady(itemSearchResult))
        {
            return new MarketBoardItemSearchResult
            {
                Status = "ListingsReady",
                Message = $"Market board listings are open for {searchText} ({itemId}).",
                Details = new Dictionary<string, string?>
                {
                    ["itemSearchResultVisible"] = true.ToString(),
                },
            };
        }

        var addon = gameGui.GetAddonByName<AddonItemSearch>(ItemSearchAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardItemSearchResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Waiting for the market board item search window to open.",
            };
        }

        var mode = (uint)addon->Mode;
        var agent = AgentItemSearch.Instance();
        var resultListItemCount = addon->ResultsList == null ? 0 : addon->ResultsList->GetItemCount();
        var details = new Dictionary<string, string?>
        {
            ["mode"] = FormatMode(mode),
            ["modeRaw"] = mode.ToString(),
            ["partialSearch"] = addon->PartialMatch.ToString(),
            ["searchButtonAvailable"] = (addon->SearchButton != null).ToString(),
            ["searchTextInputAvailable"] = (addon->SearchTextInput != null).ToString(),
            ["resultsListAvailable"] = (addon->ResultsList != null).ToString(),
            ["resultListItemCount"] = resultListItemCount.ToString(),
            ["itemSearchResultVisible"] = false.ToString(),
        };
        AddAgentDetails(details, agent);

        var exactItemVisible = AgentContainsItem(agent, itemId);
        details["exactItemVisibleInAgent"] = exactItemVisible.ToString();
        if (resultListItemCount > 0 || exactItemVisible)
        {
            return new MarketBoardItemSearchResult
            {
                Status = "ItemResultsReady",
                Message = $"Market board item search results are visible for {searchText} ({itemId}); select the exact item result.",
                Details = details,
            };
        }

        return new MarketBoardItemSearchResult
        {
            Status = "AwaitingManualSearch",
            Message = $"Waiting for manual market board search for {searchText} ({itemId}).",
            Details = details,
        };
    }

    internal static bool ShouldResetToNormalSearch(uint mode)
    {
        return mode != (uint)AddonItemSearch.SearchMode.Normal;
    }

    internal static MarketBoardItemSearchAction ChooseAction(uint mode)
    {
        return ShouldResetToNormalSearch(mode)
            ? MarketBoardItemSearchAction.ResetMode
            : MarketBoardItemSearchAction.SubmitSearch;
    }

    internal static bool ShouldDisablePartialSearch(bool partialSearchEnabled)
    {
        return false;
    }

    internal static bool IsSubmittedSearchCurrent(uint submittedItemId, string? submittedText, uint itemId, string searchText)
    {
        return submittedItemId == itemId
            && string.Equals(submittedText?.Trim(), searchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldWaitForSubmittedSearch(
        bool searchMatches,
        bool exactItemVisible,
        bool agentIsPartialSearching,
        bool agentIsItemPushPending)
    {
        return searchMatches && (exactItemVisible || agentIsPartialSearching || agentIsItemPushPending);
    }

    internal static IReadOnlyList<MarketBoardItemSearchSubmitCallback> GetSearchSubmitCallbackSequence()
    {
        return
        [
            MarketBoardItemSearchSubmitCallback.TextChanged,
            MarketBoardItemSearchSubmitCallback.Enter,
        ];
    }

    internal static IReadOnlyList<MarketBoardItemSearchResultActivationEvent> GetResultActivationEventSequence()
    {
        return
        [
            MarketBoardItemSearchResultActivationEvent.ListItemClick,
            MarketBoardItemSearchResultActivationEvent.ListItemDoubleClick,
        ];
    }

    internal static MarketBoardItemSearchFocusTarget ChooseTextInputFocusTarget(bool hasCollisionNode, bool hasOwnerNode)
    {
        if (hasCollisionNode)
            return MarketBoardItemSearchFocusTarget.CollisionNode;

        return hasOwnerNode
            ? MarketBoardItemSearchFocusTarget.OwnerNode
            : MarketBoardItemSearchFocusTarget.None;
    }

    private static unsafe bool IsAddonReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private unsafe bool TrySubmitSearchWithTextInputEnter(
        AddonItemSearch* addon,
        string searchText,
        IDictionary<string, string?> details)
    {
        addon->SearchText.SetString(searchText);
        addon->SearchText2.SetString(searchText);

        var input = addon->SearchTextInput;
        details["searchSource"] = "TextInputEnterCallback";
        details["searchTextInputAvailable"] = (input != null).ToString();
        if (input == null)
        {
            details["searchSubmitStatus"] = "TextInputUnavailable";
            return false;
        }

        input->SetText(searchText);
        var inputBase = &input->AtkComponentInputBase;
        var ownerNode = inputBase->AtkComponentBase.OwnerNode;
        var collisionNode = inputBase->CollisionNode;
        var focusTargetKind = ChooseTextInputFocusTarget(collisionNode != null, ownerNode != null);
        var focusNode = focusTargetKind switch
        {
            MarketBoardItemSearchFocusTarget.CollisionNode => &collisionNode->AtkResNode,
            MarketBoardItemSearchFocusTarget.OwnerNode => &ownerNode->AtkResNode,
            _ => null,
        };
        details["textInputCallbackAvailable"] = (inputBase->Callback != null).ToString();
        details["textInputCallbackEventKind"] = inputBase->CallbackEventKind.ToString();
        details["textInputWasActive"] = inputBase->IsActive.ToString();
        details["searchButtonWasEnabled"] = (addon->SearchButton != null && addon->SearchButton->IsEnabled).ToString();
        details["textInputFocusTarget"] = focusTargetKind.ToString();
        details["textInputFocusTargetNode"] = FormatNode(focusNode);
        if (inputBase->Callback == null)
        {
            details["searchSubmitStatus"] = "TextInputCallbackUnavailable";
            return false;
        }

        var focusSet = false;
        if (focusNode != null)
        {
            addon->AtkUnitBase.Focus();
            addon->AtkUnitBase.SetFocusNode(focusNode, setCursorFocusNode: true);
            addon->AtkUnitBase.SetComponentFocusNode(&inputBase->AtkComponentBase);

            var stage = AtkStage.Instance();
            if (stage != null && stage->AtkInputManager != null)
                focusSet = stage->AtkInputManager->SetFocus(focusNode, &addon->AtkUnitBase, 0);
        }

        inputBase->IsActive = true;
        inputBase->SelectionStart = searchText.Length;
        inputBase->SelectionEnd = searchText.Length;
        inputBase->CursorPos = searchText.Length;
        details["textInputFocusSet"] = focusSet.ToString();
        details["textInputIsActiveAfterFocus"] = inputBase->IsActive.ToString();

        var callbackResults = new List<string>();
        foreach (var callback in GetSearchSubmitCallbackSequence())
        {
            var callbackType = callback == MarketBoardItemSearchSubmitCallback.TextChanged
                ? InputCallbackType.TextChanged
                : InputCallbackType.Enter;
            var callbackResult = inputBase->Callback(
                &addon->AtkUnitBase,
                callbackType,
                inputBase->RawString.StringPtr,
                inputBase->EvaluatedString.StringPtr,
                inputBase->CallbackEventKind);
            callbackResults.Add($"{callback}:{callbackResult}");
        }

        details["textInputCallbackSequence"] = string.Join(",", GetSearchSubmitCallbackSequence());
        details["textInputCallbackResults"] = string.Join(",", callbackResults);
        details["searchButtonEnabledAfterCallbacks"] = (addon->SearchButton != null && addon->SearchButton->IsEnabled).ToString();
        details["searchSubmitStatus"] = "Submitted";
        return true;
    }

    private void ClearSubmittedSearch()
    {
        submittedSearchItemId = 0;
        submittedSearchText = null;
    }

    private static unsafe bool TryOpenExactItemResult(
        AddonItemSearch* addon,
        AgentItemSearch* agent,
        uint itemId,
        IDictionary<string, string?> details)
    {
        if (agent == null)
        {
            details["itemResultSelectStatus"] = "AgentUnavailable";
            return false;
        }

        var itemCount = Math.Min((int)agent->ItemCount, 100);
        if (itemCount <= 0)
        {
            details["itemResultSelectStatus"] = "NoAgentItems";
            return false;
        }

        for (var index = 0; index < itemCount; index++)
        {
            var candidateItemId = agent->ItemBuffer[index];
            if (candidateItemId != itemId)
                continue;

            details["itemResultSelectStatus"] = "ExactMatch";
            details["itemResultIndex"] = index.ToString();
            details["itemResultId"] = candidateItemId.ToString();
            details["resultListItemCount"] = addon->ResultsList == null
                ? null
                : addon->ResultsList->GetItemCount().ToString();

            if (addon->ResultsList == null)
            {
                details["itemResultSelectStatus"] = "ResultsListUnavailable";
                return false;
            }

            details["listIsItemInteractionEnabled"] = addon->ResultsList->IsItemInteractionEnabled.ToString();
            details["listIsItemClickEnabled"] = addon->ResultsList->IsItemClickEnabled.ToString();
            details["listSelectedItemIndexBefore"] = addon->ResultsList->SelectedItemIndex.ToString(CultureInfo.InvariantCulture);
            details["agentResultSelectedIndexBefore"] = agent->ResultSelectedIndex.ToString(CultureInfo.InvariantCulture);

            addon->ResultsList->SelectItem(index, true);
            foreach (var activationEvent in GetResultActivationEventSequence())
                addon->ResultsList->DispatchItemEvent(index, ToAtkEventType(activationEvent));

            details["itemOpenSource"] = "ResultListActivationSequence";
            details["itemOpenEventSequence"] = string.Join(",", GetResultActivationEventSequence());
            details["listSelectedItemIndexAfter"] = addon->ResultsList->SelectedItemIndex.ToString(CultureInfo.InvariantCulture);
            details["agentResultSelectedIndexAfter"] = agent->ResultSelectedIndex.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        details["itemResultSelectStatus"] = "ExactItemNotFound";
        details["agentItemPreview"] = FormatAgentItemPreview(agent, itemCount);
        return false;
    }

    private static AtkEventType ToAtkEventType(MarketBoardItemSearchResultActivationEvent activationEvent)
    {
        return activationEvent switch
        {
            MarketBoardItemSearchResultActivationEvent.ListItemClick => AtkEventType.ListItemClick,
            MarketBoardItemSearchResultActivationEvent.ListItemDoubleClick => AtkEventType.ListItemDoubleClick,
            _ => throw new ArgumentOutOfRangeException(nameof(activationEvent), activationEvent, null),
        };
    }

    private static unsafe void AddAgentDetails(IDictionary<string, string?> details, AgentItemSearch* agent)
    {
        if (agent == null)
        {
            details["agentAvailable"] = false.ToString();
            return;
        }

        var itemCount = Math.Min((int)agent->ItemCount, 100);
        details["agentAvailable"] = true.ToString();
        details["agentItemCount"] = itemCount.ToString();
        details["agentIsPartialSearching"] = agent->IsPartialSearching.ToString();
        details["agentIsItemPushPending"] = agent->IsItemPushPending.ToString();
        details["agentResultItemId"] = agent->ResultItemId.ToString();
        details["agentResultSelectedIndex"] = agent->ResultSelectedIndex.ToString();
        details["agentItemPreview"] = FormatAgentItemPreview(agent, itemCount);
    }

    private static unsafe bool AgentContainsItem(AgentItemSearch* agent, uint itemId)
    {
        if (agent == null)
            return false;

        var itemCount = Math.Min((int)agent->ItemCount, 100);
        if (itemCount <= 0)
            return false;

        for (var index = 0; index < itemCount; index++)
        {
            if (agent->ItemBuffer[index] == itemId)
                return true;
        }

        return false;
    }

    private static unsafe string FormatAgentItemPreview(AgentItemSearch* agent, int itemCount)
    {
        if (agent == null || itemCount <= 0)
            return string.Empty;

        var previewCount = Math.Min(itemCount, 8);
        var values = new string[previewCount];
        for (var index = 0; index < previewCount; index++)
            values[index] = agent->ItemBuffer[index].ToString();

        return string.Join(",", values);
    }

    private static unsafe string FormatNode(AtkResNode* node)
    {
        return node == null
            ? "null"
            : $"0x{FormatPointerValue(node)}#{node->NodeId.ToString(CultureInfo.InvariantCulture)}";
    }

    private static unsafe string FormatPointer(void* pointer)
    {
        return pointer == null ? "null" : $"0x{FormatPointerValue(pointer)}";
    }

    private static unsafe string FormatPointerValue(void* pointer)
    {
        return ((nuint)pointer).ToString("X", CultureInfo.InvariantCulture);
    }

    private static string FormatMode(uint mode)
    {
        return Enum.IsDefined(typeof(AddonItemSearch.SearchMode), mode)
            ? ((AddonItemSearch.SearchMode)mode).ToString()
            : mode.ToString();
    }
}

public enum MarketBoardItemSearchAction
{
    ResetMode,
    SubmitSearch,
}

public enum MarketBoardItemSearchSubmitCallback
{
    TextChanged,
    Enter,
}

public enum MarketBoardItemSearchResultActivationEvent
{
    ListItemClick,
    ListItemDoubleClick,
}

public enum MarketBoardItemSearchFocusTarget
{
    None,
    OwnerNode,
    CollisionNode,
}

public sealed record MarketBoardItemSearchResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();
    public bool SearchSent => string.Equals(Status, "SearchSent", StringComparison.OrdinalIgnoreCase);
    public bool ReadyForListings => string.Equals(Status, "ListingsReady", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress =>
        Status is "MarketBoardNotOpen" or "ModeReset" or "SearchSent" or "ItemSelectionSent" or "ItemOpenSent";
}
