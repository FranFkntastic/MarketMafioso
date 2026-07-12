using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using ECommons.ExcelServices.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.Squire.Observation;

internal sealed class DalamudExpertDeliveryPreparation
{
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InteractionTimeout = TimeSpan.FromSeconds(30);

    private readonly ICommandManager commandManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly IDataManager dataManager;

    public DalamudExpertDeliveryPreparation(
        ICommandManager commandManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IGameGui gameGui,
        IFramework framework,
        IDataManager dataManager)
    {
        this.commandManager = commandManager;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.gameGui = gameGui;
        this.framework = framework;
        this.dataManager = dataManager;
    }

    public async Task<SquireActionResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (await framework.RunOnTick(IsExpertDeliveryListReady).ConfigureAwait(false))
            return SquireActionResult.Completed();

        if (await framework.RunOnTick(IsSupplyListReady).ConfigureAwait(false))
            return await SelectExpertDeliveryTabAsync(cancellationToken).ConfigureAwait(false);

        var company = await framework.RunOnTick(GetGrandCompany).ConfigureAwait(false);
        if (!TryGetOfficerDataId(company, out var officerDataId))
            return SquireActionResult.Fail("GrandCompanyUnavailable", "The active character is not employed by a Grand Company.");

        if (!await framework.RunOnTick(() => IsOfficerInRange(officerDataId)).ConfigureAwait(false))
        {
            if (!commandManager.ProcessCommand("/li gc"))
                return SquireActionResult.Fail("LifestreamUnavailable", "Lifestream did not accept /li gc, so Squire could not travel to the Grand Company.");

            var arrived = await WaitUntilAsync(
                () => IsOfficerInRange(officerDataId),
                TravelTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!arrived)
                return SquireActionResult.Fail("GrandCompanyTravelTimeout", "Lifestream did not bring the character within interaction range of the Grand Company delivery officer.");
        }

        var interactionStarted = await framework.RunOnTick(() => TryInteract(officerDataId)).ConfigureAwait(false);
        if (!interactionStarted)
            return SquireActionResult.Fail("GrandCompanyOfficerUnavailable", "The Grand Company delivery officer was present but could not be targeted and interacted with.");

        var supplyEntry = dataManager
            .GetExcelSheet<QuestDialogueText>(name: "custom/000/ComDefGrandCompanyOfficer_00073")
            .GetRow(69)
            .Value
            .ExtractText()
            .Trim();
        if (string.IsNullOrWhiteSpace(supplyEntry))
            return SquireActionResult.Fail("GrandCompanyMenuTextUnavailable", "The localized supply and provisioning menu text could not be resolved.");

        var menuSelected = await WaitUntilAsync(
            () => TrySelectSupplyEntry(supplyEntry) || IsSupplyListReady(),
            InteractionTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!menuSelected)
            return SquireActionResult.Fail("GrandCompanyMenuTimeout", "The Grand Company supply and provisioning menu did not become ready.");

        return await SelectExpertDeliveryTabAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SquireActionResult> SelectExpertDeliveryTabAsync(CancellationToken cancellationToken)
    {
        var tabSelected = await WaitUntilAsync(TrySelectExpertDeliveryTab, InteractionTimeout, cancellationToken).ConfigureAwait(false);
        if (!tabSelected)
            return SquireActionResult.Fail("ExpertDeliveryTabTimeout", "The Expert Delivery tab did not become ready.");

        var ready = await WaitUntilAsync(IsExpertDeliveryListReady, InteractionTimeout, cancellationToken).ConfigureAwait(false);
        return ready
            ? SquireActionResult.Completed()
            : SquireActionResult.Fail("ExpertDeliveryListTimeout", "The Expert Delivery item list did not become ready after selecting its tab.");
    }

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await framework.RunOnTick(predicate).ConfigureAwait(false))
                return true;
            await framework.DelayTicks(6).ConfigureAwait(false);
        }
        return false;
    }

    private static unsafe byte GetGrandCompany() =>
        FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance()->GrandCompany;

    private static bool TryGetOfficerDataId(byte company, out uint dataId)
    {
        dataId = company switch
        {
            1 => 1002388,
            2 => 1002394,
            3 => 1002391,
            _ => 0,
        };
        return dataId != 0;
    }

    private IGameObject? FindOfficer(uint dataId) => objectTable
        .Where(gameObject => gameObject.BaseId == dataId && gameObject.IsTargetable)
        .OrderBy(gameObject => gameObject.YalmDistanceX + gameObject.YalmDistanceZ)
        .FirstOrDefault();

    private bool IsOfficerInRange(uint dataId)
    {
        var officer = FindOfficer(dataId);
        return officer is not null && officer.YalmDistanceX <= 7 && officer.YalmDistanceZ <= 7;
    }

    private unsafe bool TryInteract(uint dataId)
    {
        var officer = FindOfficer(dataId);
        var targetSystem = TargetSystem.Instance();
        if (officer is null || targetSystem is null || officer.YalmDistanceX > 7 || officer.YalmDistanceZ > 7)
            return false;

        targetManager.Target = officer;
        targetSystem->InteractWithObject((ClientGameObject*)officer.Address, false);
        return true;
    }

    private unsafe bool TrySelectSupplyEntry(string targetText)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>("SelectString", 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;

        var popup = addon->PopupMenu.PopupMenu;
        for (var index = 0; index < popup.EntryCount; index++)
        {
            var text = popup.EntryNames[index].ToString();
            if (!Automation.Retainers.RetainerUiAutomationText.IsSelectStringEntryMatch(text, targetText))
                continue;
            addon->AtkUnitBase.FireCallbackInt(index);
            return true;
        }
        return false;
    }

    private unsafe bool IsSupplyListReady()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private unsafe bool TrySelectExpertDeliveryTab()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return false;
        if (IsExpertDeliveryListReady())
            return true;

        var node = addon->GetNodeById(13);
        if (node == null)
            return false;
        var button = node->GetAsAtkComponentRadioButton();
        if (button == null)
            return false;
        button->ClickRadioButton(addon);
        return true;
    }

    private unsafe bool IsExpertDeliveryListReady()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GrandCompanySupplyList", 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return false;
        if (addon->UldManager.NodeListCount <= 24)
            return false;
        var listNode = addon->UldManager.SearchNodeById(24);
        return listNode != null && listNode->IsVisible();
    }

}
