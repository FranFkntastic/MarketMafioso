using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.Automation.MarketBoard;

internal sealed unsafe class DalamudMarketBoardBrowseObserver : IMarketBoardBrowseRuntime, IDisposable
{
    internal const string ApprovedGameVersion = "2026.07.16.0001.0000";
    internal const string PatchContractId = "mmf.market-board-browse";

    private const int HeaderLength = 8;
    private const int ListingsPerPage = 10;
    private const int ListingStride = 0x90;
    private const int ListingItemIdOffset = 0x2C;
    private const int PageMetadataOffset = ListingsPerPage * ListingStride;
    private const int HistoryHeaderLength = 4;
    private const int HistoryRecordCount = 20;
    private const int HistoryRecordStride = 0x30;
    private const int HistoryPriceOffset = 0;
    private const int HistoryQuantityOffset = 8;

    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly MarketBoardBrowseOperationGate gate = new();
    private Hook<InfoProxyItemSearch.Delegates.RequestData>? requestDataHook;
    private Hook<PacketDispatcher.Delegates.HandleMarketBoardItemRequestStartPacket>? headerHook;
    private Hook<InfoProxyItemSearch.Delegates.AddPage>? addPageHook;
    private Hook<InfoProxyItemSearch.Delegates.ProcessItemHistory>? historyHook;

    public DalamudMarketBoardBrowseObserver(
        IGameInteropProvider interopProvider,
        IFramework framework,
        IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(interopProvider);
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        framework.Update += OnFrameworkUpdate;

        try
        {
            var compatibility = GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion);
            if (!compatibility.IsApproved)
                throw new InvalidOperationException(compatibility.Message);

            var infoProxyVtable = InfoProxyItemSearch.StaticVirtualTablePointer;
            var requestDataAddress = infoProxyVtable == null ? 0 : (nint)infoProxyVtable->RequestData;
            var addPageAddress = infoProxyVtable == null ? 0 : (nint)infoProxyVtable->AddPage;
            var headerAddress = PacketDispatcher.Addresses.HandleMarketBoardItemRequestStartPacket.Value;
            var historyAddress = InfoProxyItemSearch.Addresses.ProcessItemHistory.Value;
            var dispatchItemEventAddress = AtkComponentList.Addresses.DispatchItemEvent.Value;
            var runSearchAddress = AddonItemSearch.Addresses.RunSearch.Value;
            var setModeFilterAddress = AddonItemSearch.Addresses.SetModeFilter.Value;

            RequireAddress(requestDataAddress, "InfoProxyItemSearch.RequestData");
            RequireAddress(addPageAddress, "InfoProxyItemSearch.AddPage");
            RequireAddress(headerAddress, "HandleMarketBoardItemRequestStartPacket");
            RequireAddress(historyAddress, "InfoProxyItemSearch.ProcessItemHistory");
            RequireAddress(dispatchItemEventAddress, "AtkComponentList.DispatchItemEvent");
            RequireAddress(runSearchAddress, "AddonItemSearch.RunSearch");
            RequireAddress(setModeFilterAddress, "AddonItemSearch.SetModeFilter");

            requestDataHook = interopProvider.HookFromAddress<InfoProxyItemSearch.Delegates.RequestData>(
                requestDataAddress,
                RequestDataDetour);
            headerHook = interopProvider.HookFromAddress<PacketDispatcher.Delegates.HandleMarketBoardItemRequestStartPacket>(
                headerAddress,
                HeaderDetour);
            addPageHook = interopProvider.HookFromAddress<InfoProxyItemSearch.Delegates.AddPage>(
                addPageAddress,
                AddPageDetour);
            historyHook = interopProvider.HookFromAddress<InfoProxyItemSearch.Delegates.ProcessItemHistory>(
                historyAddress,
                HistoryDetour);

            requestDataHook.Enable();
            headerHook.Enable();
            addPageHook.Enable();
            historyHook.Enable();
            IsAvailable = true;
            AvailabilityMessage = compatibility.Message;
        }
        catch (Exception exception)
        {
            DisposeHooks();
            IsAvailable = false;
            AvailabilityMessage =
                $"{GamePatchCompatibility.FailureCode}: {PatchContractId} observer unavailable: {exception.Message}";
            log.Error(
                exception,
                "[MarketMafioso] Market-board browse observer is unavailable; remote browse remains blocked.");
        }
    }

    public bool IsAvailable { get; private set; }
    public string AvailabilityMessage { get; private set; } = "Market-board browse observer has not initialized.";
    public MarketBoardBrowseSnapshot Snapshot => gate.Snapshot;

    public bool TryBegin(
        MarketBoardBrowseOwner owner,
        uint itemId,
        out MarketBoardBrowseSnapshot snapshot)
    {
        if (!IsAvailable)
        {
            snapshot = MarketBoardBrowseSnapshot.Idle with
            {
                Phase = MarketBoardBrowsePhase.Failed,
                FailureCode = GamePatchCompatibility.FailureCode,
                Message = AvailabilityMessage,
            };
            return false;
        }

        return gate.TryBegin(owner, itemId, out snapshot);
    }

    public bool TryClaimActivation(
        MarketBoardBrowseOwner owner,
        uint itemId,
        out MarketBoardBrowseSnapshot snapshot)
    {
        if (!IsAvailable)
        {
            snapshot = MarketBoardBrowseSnapshot.Idle with
            {
                Phase = MarketBoardBrowsePhase.Failed,
                FailureCode = GamePatchCompatibility.FailureCode,
                Message = AvailabilityMessage,
            };
            return false;
        }

        return gate.TryClaimActivation(owner, itemId, out snapshot);
    }

    public bool TryAbandon(
        MarketBoardBrowseOwner owner,
        string operationId,
        string reason,
        out MarketBoardBrowseSnapshot snapshot) =>
        gate.TryAbandon(owner, operationId, reason, out snapshot);

    private void OnFrameworkUpdate(IFramework _) => gate.Advance(DateTimeOffset.UtcNow);

    private bool RequestDataDetour(InfoProxyItemSearch* proxy)
    {
        var itemId = proxy == null ? 0 : proxy->SearchItemId;
        bool accepted;
        try
        {
            accepted = requestDataHook!.Original(proxy);
        }
        catch (Exception exception)
        {
            gate.ObserveRequest(itemId, false);
            log.Error(exception, "[MarketMafioso] RequestData trampoline failed for item {ItemId}.", itemId);
            throw;
        }

        gate.ObserveRequest(itemId, accepted);
        LogTerminalFailure();
        return accepted;
    }

    private void HeaderDetour(uint targetId, nint packet)
    {
        var status = uint.MaxValue;
        var listingCount = uint.MaxValue;
        try
        {
            if (packet != 0)
            {
                var bytes = new ReadOnlySpan<byte>((void*)packet, HeaderLength);
                status = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                listingCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to observe the market-board result header.");
        }

        headerHook!.Original(targetId, packet);
        gate.ObserveHeader(status, listingCount);
        LogTerminalFailure();
    }

    private void AddPageDetour(InfoProxyItemSearch* proxy, nint packet)
    {
        byte continuationToken = 0;
        byte firstMarker = 0xFF;
        byte requestId = 0xFF;
        byte proxyCurrentRequestId = 0;
        IReadOnlyList<uint> itemIds = [];
        try
        {
            if (proxy != null && packet != 0)
            {
                var bytes = new ReadOnlySpan<byte>((void*)packet, PageMetadataOffset + 4);
                continuationToken = bytes[PageMetadataOffset];
                firstMarker = bytes[PageMetadataOffset + 1];
                requestId = bytes[PageMetadataOffset + 2];
                proxyCurrentRequestId = proxy->InfoProxyPageInterface.CurrentRequestId;
                itemIds = DecodePageItemIds(bytes);
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to observe a market-board listings page.");
        }

        addPageHook!.Original(proxy, packet);
        gate.ObservePage(
            continuationToken,
            firstMarker,
            requestId,
            proxyCurrentRequestId,
            itemIds);
        LogTerminalFailure();
    }

    private void HistoryDetour(InfoProxyItemSearch* proxy, nint packet)
    {
        uint itemId = 0;
        var structurallyValid = false;
        var entryCount = 0;
        try
        {
            if (packet != 0)
            {
                var bytes = new ReadOnlySpan<byte>(
                    (void*)packet,
                    HistoryHeaderLength + (HistoryRecordCount * HistoryRecordStride));
                itemId = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                structurallyValid = TryCountStandardHistoryEntries(bytes, out entryCount);
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to observe standard market-board history.");
        }

        historyHook!.Original(proxy, packet);
        gate.ObserveHistory(itemId, structurallyValid, entryCount);
        LogTerminalFailure();
    }

    internal static IReadOnlyList<uint> DecodePageItemIds(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PageMetadataOffset + 4)
            return [];

        var itemIds = new List<uint>(ListingsPerPage);
        for (var index = 0; index < ListingsPerPage; index++)
        {
            var itemIdOffset = (index * ListingStride) + ListingItemIdOffset;
            var itemId = BinaryPrimitives.ReadUInt32LittleEndian(packet[itemIdOffset..]);
            if (itemId != 0)
                itemIds.Add(itemId);
        }

        return itemIds;
    }

    internal static bool TryCountStandardHistoryEntries(
        ReadOnlySpan<byte> packet,
        out int entryCount)
    {
        entryCount = 0;
        if (packet.Length < HistoryHeaderLength + (HistoryRecordCount * HistoryRecordStride))
            return false;

        for (var index = 0; index < HistoryRecordCount; index++)
        {
            var record = packet[(HistoryHeaderLength + (index * HistoryRecordStride))..];
            var price = BinaryPrimitives.ReadUInt32LittleEndian(record[HistoryPriceOffset..]);
            var quantity = BinaryPrimitives.ReadUInt32LittleEndian(record[HistoryQuantityOffset..]);
            if (price == 0 || quantity == 0)
                return price == 0;
            entryCount++;
        }

        return true;
    }

    private void LogTerminalFailure()
    {
        var current = gate.Snapshot;
        if (current.IsFailed)
        {
            log.Warning(
                "[MarketMafioso] Market-board browse {OperationId} failed closed. Code={Code} Message={Message}",
                current.OperationId,
                current.FailureCode ?? "Unknown",
                current.Message);
        }
    }

    private static void RequireAddress(nint address, string name)
    {
        if (address == 0)
            throw new InvalidOperationException($"{name} address is unavailable.");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        DisposeHooks();
        IsAvailable = false;
    }

    private void DisposeHooks()
    {
        historyHook?.Dispose();
        historyHook = null;
        addPageHook?.Dispose();
        addPageHook = null;
        headerHook?.Dispose();
        headerHook = null;
        requestDataHook?.Dispose();
        requestDataHook = null;
    }
}
