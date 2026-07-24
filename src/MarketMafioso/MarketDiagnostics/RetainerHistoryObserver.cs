using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MarketMafioso.Contracts;

namespace MarketMafioso.MarketDiagnostics;

internal sealed class RetainerHistoryObserver : IDisposable
{
    private const string AddonName = "RetainerHistory";
    private readonly Configuration configuration;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Action<RetainerSaleEvidenceCreateRequest> enqueue;
    private DateTimeOffset captureUntilUtc = DateTimeOffset.MinValue;
    private DateTimeOffset nextCaptureAtUtc = DateTimeOffset.MinValue;
    private bool disposed;

    public RetainerHistoryObserver(
        Configuration configuration,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPlayerState playerState,
        IDataManager dataManager,
        IPluginLog log,
        Action<RetainerSaleEvidenceCreateRequest> enqueue)
    {
        this.configuration = configuration;
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.log = log;
        this.enqueue = enqueue;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonChanged);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddonChanged);
        addonLifecycle.RegisterListener(AddonEvent.PostShow, AddonName, OnAddonChanged);
    }

    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (disposed ||
            !configuration.EnableMarketDiagnostics ||
            now < nextCaptureAtUtc ||
            now > captureUntilUtc)
        {
            return;
        }

        nextCaptureAtUtc = now.AddMilliseconds(250);
        Capture(now);
    }

    private void OnAddonChanged(AddonEvent type, AddonArgs args)
    {
        if (disposed || !configuration.EnableMarketDiagnostics)
            return;

        var now = DateTimeOffset.UtcNow;
        nextCaptureAtUtc = now;
        captureUntilUtc = now.AddSeconds(2);
    }

    private unsafe void Capture(DateTimeOffset observedAtUtc)
    {
        try
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(AddonName, 1);
            if (addon == null || !addon->IsReady || !addon->IsVisible)
                return;

            var stage = AtkStage.Instance();
            if (stage == null)
                return;
            var numberArray = stage->GetNumberArrayData(NumberArrayType.ItemDetail);
            var stringArray = stage->GetStringArrayData(StringArrayType.ItemDetail);
            if (numberArray == null || stringArray == null)
                return;

            var numbers = numberArray->Span.ToArray();
            var strings = stringArray->Span
                .ToArray()
                .Select(value => value.ToString())
                .ToArray();
            var items = dataManager.GetExcelSheet<Item>();
            var sales = RetainerHistoryParser.Parse(
                numbers,
                strings,
                itemId => items.GetRowOrDefault(itemId)?.Name.ToString(),
                observedAtUtc);
            if (sales.Count == 0)
                return;

            var retainerManager = RetainerManager.Instance();
            var activeRetainer = retainerManager == null ? null : retainerManager->GetActiveRetainer();
            var retainerId = activeRetainer == null || activeRetainer->RetainerId == 0
                ? (ulong?)null
                : activeRetainer->RetainerId;
            var retainerName = activeRetainer == null
                ? null
                : activeRetainer->NameString;
            foreach (var sale in sales)
                enqueue(CreateEvidence(sale, retainerId, retainerName));
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Failed to import visible Retainer History.");
            captureUntilUtc = DateTimeOffset.MinValue;
        }
    }

    private RetainerSaleEvidenceCreateRequest CreateEvidence(
        ParsedRetainerHistorySale sale,
        ulong? retainerId,
        string? retainerName)
    {
        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{retainerId}|{sale.SoldAtUtc:O}|{sale.ItemId}|{sale.IsHq}|{sale.Quantity}|" +
            $"{sale.TotalGil}|{sale.BuyerName}");
        return new RetainerSaleEvidenceCreateRequest
        {
            Source = "RetainerHistory",
            EvidenceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
            RetainerId = retainerId,
            RetainerName = retainerName,
            ItemId = sale.ItemId,
            ItemName = sale.ItemName,
            IsHq = sale.IsHq,
            Quantity = sale.Quantity,
            UnitPrice = sale.UnitPrice,
            TotalGil = sale.TotalGil,
            EventAtUtc = sale.SoldAtUtc,
            CharacterName = playerState.CharacterName,
            HomeWorld = playerState.HomeWorld.IsValid
                ? playerState.HomeWorld.Value.Name.ToString()
                : null,
            RawMessage = string.Create(
                CultureInfo.InvariantCulture,
                $"Retainer History: {sale.Quantity} x {sale.ItemName} sold for " +
                $"{sale.TotalGil:N0} gil to {sale.BuyerName ?? "unknown buyer"}."),
        };
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        addonLifecycle.UnregisterListener(AddonEvent.PostShow, AddonName, OnAddonChanged);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, AddonName, OnAddonChanged);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonName, OnAddonChanged);
    }
}
