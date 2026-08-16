using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using Lumina.Excel.Sheets;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record ControlledMarketActorListingProbeView(
    string State,
    string Message,
    bool Active,
    string? ItemName,
    uint? ItemId,
    string? RetainerName,
    int? Quantity,
    bool? IsHq,
    uint? UnitPrice,
    int? ListingSlot,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Owns one deliberately narrow, reversible listing fixture for controlled actor-identity tests.
/// It is not a general retainer listing API: quantity and price are fixed, identity is name-first,
/// and cleanup must consume the exact receipt produced by posting.
/// </summary>
internal sealed class ControlledMarketActorListingProbe : IDisposable
{
    private const int FixtureQuantity = 1;
    private const uint FixtureUnitPrice = 999_999_999;
    private readonly object sync = new();
    private readonly Configuration configuration;
    private readonly IDataManager dataManager;
    private readonly ICondition condition;
    private readonly IRetainerAutomationSession session;
    private readonly IAutoRetainerIpc autoRetainer;
    private readonly IPluginLog log;
    private CancellationTokenSource? operation;
    private RetainerAutomationTarget? retainer;
    private RetainerMarketListingTarget? listing;
    private ControlledMarketActorListingProbeView view = new(
        "Idle", "No controlled listing has been posted.", false, null, null, null, null, null, null, null, DateTimeOffset.UtcNow);

    public ControlledMarketActorListingProbe(
        Configuration configuration,
        IDataManager dataManager,
        ICondition condition,
        IFramework framework,
        IGameGui gameGui,
        IPluginLog log,
        IObjectTable objects,
        ITargetManager targets,
        ISigScanner sigScanner,
        IDalamudPluginInterface pluginInterface,
        IGameInventory gameInventory)
    {
        this.configuration = configuration;
        this.dataManager = dataManager;
        this.condition = condition;
        this.log = log;
        session = new DalamudRetainerAutomationSession(framework, gameGui, dataManager, log, objects, targets, sigScanner, gameInventory);
        autoRetainer = new DalamudAutoRetainerIpc(pluginInterface);
    }

    public ControlledMarketActorListingProbeView Snapshot()
    {
        lock (sync) return view;
    }

    public ControlledMarketActorListingProbeView BeginPost(string itemName, bool otherAutomationBusy)
    {
        var resolved = ResolveExactItem(itemName);
        lock (sync)
        {
            if (operation is not null)
                return view with { Message = "A controlled listing operation is already active." };
            if (listing is not null)
                return view with { Message = "Remove the existing controlled listing before posting another." };
            if (!configuration.EnableMarketDiagnostics)
                return view with { Message = "Market Diagnostics must be enabled for controlled listing evidence." };
            if (otherAutomationBusy)
                return view with { Message = "Another MarketMafioso automation owns the client." };
            if (condition[ConditionFlag.Crafting] || condition[ConditionFlag.PreparingToCraft])
                return view with { Message = "Wait for crafting to finish before posting the controlled listing." };
            if (resolved is null)
                return view with { Message = $"Exactly one marketable item named '{itemName.Trim()}' was not found." };

            operation = new CancellationTokenSource();
            view = new(
                "Posting", $"Posting one {resolved.Value.Name} at the fixed control price.", true,
                resolved.Value.Name, resolved.Value.ItemId, null, FixtureQuantity, null, FixtureUnitPrice, null, DateTimeOffset.UtcNow);
            _ = RunPostAsync(resolved.Value.ItemId, resolved.Value.Name, operation.Token);
            return view;
        }
    }

    public ControlledMarketActorListingProbeView BeginRemoval(bool otherAutomationBusy)
    {
        lock (sync)
        {
            if (operation is not null)
                return view with { Message = "A controlled listing operation is already active." };
            if (listing is null || retainer is null)
                return view with { Message = "There is no exact controlled listing receipt to remove." };
            if (otherAutomationBusy)
                return view with { Message = "Another MarketMafioso automation owns the client." };

            operation = new CancellationTokenSource();
            view = view with
            {
                State = "Removing",
                Message = $"Removing the exact {view.ItemName} control listing.",
                Active = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _ = RunRemovalAsync(retainer, listing, operation.Token);
            return view;
        }
    }

    private async Task RunPostAsync(uint itemId, string itemName, CancellationToken cancellationToken)
    {
        try
        {
            if (!AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var suppression, out var suppressionMessage))
            {
                Finish("Failed", suppressionMessage);
                return;
            }
            using (suppression)
            {
                var ready = await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
                if (!ready.Success) { Finish("Failed", $"{ready.Code}: {ready.Message}"); return; }
                var roster = await session.ScanAvailableRetainersAsync(cancellationToken).ConfigureAwait(false);
                if (!roster.Success || roster.Retainers.Count == 0)
                {
                    Finish("Failed", $"{roster.Code}: {roster.Message}");
                    return;
                }

                var playerStacks = await session.ScanPlayerInventoryAsync(new HashSet<uint> { itemId }, cancellationToken).ConfigureAwait(false);
                var source = playerStacks
                    .Where(stack => stack.ItemId == itemId && stack.Quantity >= FixtureQuantity)
                    .OrderBy(stack => stack.IsHighQuality)
                    .ThenBy(stack => stack.Container)
                    .ThenBy(stack => stack.SlotIndex)
                    .FirstOrDefault();
                if (source is null)
                {
                    Finish("Failed", $"No live player-inventory stack of {itemName} is available.");
                    return;
                }

                foreach (var candidate in roster.Retainers)
                {
                    var opened = await session.OpenRetainerAsync(candidate, cancellationToken).ConfigureAwait(false);
                    if (!opened.Success) { Finish("Failed", $"{opened.Code}: {opened.Message}"); return; }
                    var selling = await session.OpenSellingListAsync(cancellationToken).ConfigureAwait(false);
                    if (!selling.Success) { Finish("Failed", $"{selling.Code}: {selling.Message}"); return; }

                    var posted = await session.PostMarketListingAsync(source, FixtureQuantity, FixtureUnitPrice, cancellationToken).ConfigureAwait(false);
                    if (posted.Success && posted.Listing is not null)
                    {
                        lock (sync)
                        {
                            retainer = candidate;
                            listing = posted.Listing;
                            view = view with
                            {
                                State = "Listed",
                                Message = $"Posted and verified one {itemName}. {suppressionMessage}",
                                Active = false,
                                RetainerName = candidate.RetainerName,
                                IsHq = source.IsHighQuality,
                                ListingSlot = posted.Listing.SlotIndex,
                                UpdatedAtUtc = DateTimeOffset.UtcNow,
                            };
                        }
                        await CloseSessionAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (posted.RequestSent || posted.Code != "RetainerMarketFull")
                    {
                        Finish(posted.RequestSent ? "Indeterminate" : "Failed", $"{posted.Code}: {posted.Message}");
                        return;
                    }

                    var returned = await session.ReturnToRetainerListAsync(cancellationToken).ConfigureAwait(false);
                    if (!returned.Success) { Finish("Failed", $"{returned.Code}: {returned.Message}"); return; }
                }

                Finish("Failed", "Every available retainer has a full market-listing book.");
            }
        }
        catch (RetainerMarketMutationIndeterminateException exception)
        {
            Finish("Indeterminate", $"{exception.Code}: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            Finish("Cancelled", "The controlled listing operation was cancelled before a committed receipt.");
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Controlled market actor listing post failed.");
            Finish("Failed", exception.Message);
        }
        finally
        {
            lock (sync) { operation?.Dispose(); operation = null; }
        }
    }

    private async Task RunRemovalAsync(
        RetainerAutomationTarget expectedRetainer,
        RetainerMarketListingTarget expectedListing,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var suppression, out var suppressionMessage))
            {
                Finish("RemovalFailed", suppressionMessage);
                return;
            }
            using (suppression)
            {
                var ready = await session.EnsureRetainerListAsync(cancellationToken).ConfigureAwait(false);
                if (!ready.Success) { Finish("RemovalFailed", $"{ready.Code}: {ready.Message}"); return; }
                var opened = await session.OpenRetainerAsync(expectedRetainer, cancellationToken).ConfigureAwait(false);
                if (!opened.Success) { Finish("RemovalFailed", $"{opened.Code}: {opened.Message}"); return; }
                var selling = await session.OpenSellingListAsync(cancellationToken).ConfigureAwait(false);
                if (!selling.Success) { Finish("RemovalFailed", $"{selling.Code}: {selling.Message}"); return; }

                var removed = await session.RemoveMarketListingToPlayerInventoryAsync(expectedListing, cancellationToken).ConfigureAwait(false);
                if (!removed.Success)
                {
                    Finish(removed.RequestSent ? "RemovalIndeterminate" : "RemovalFailed", $"{removed.Code}: {removed.Message}");
                    return;
                }

                await CloseSessionAsync(cancellationToken).ConfigureAwait(false);
                lock (sync)
                {
                    retainer = null;
                    listing = null;
                    view = view with
                    {
                        State = "Removed",
                        Message = $"Removed the exact controlled listing and verified the item returned to player inventory. {suppressionMessage}",
                        Active = false,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    };
                }
            }
        }
        catch (RetainerMarketMutationIndeterminateException exception)
        {
            Finish("RemovalIndeterminate", $"{exception.Code}: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            Finish("RemovalCancelled", "Removal was cancelled; re-scan the exact listing before retrying.");
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Controlled market actor listing removal failed.");
            Finish("RemovalFailed", exception.Message);
        }
        finally
        {
            lock (sync) { operation?.Dispose(); operation = null; }
        }
    }

    private async Task CloseSessionAsync(CancellationToken cancellationToken)
    {
        var returned = await session.ReturnToRetainerListAsync(cancellationToken).ConfigureAwait(false);
        if (returned.Success)
            await session.CloseRetainerListAsync(cancellationToken).ConfigureAwait(false);
    }

    private (uint ItemId, string Name)? ResolveExactItem(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        var matches = dataManager.GetExcelSheet<Item>()
            .Where(item => item.ItemSearchCategory.RowId != 0 && item.Name.ToString().Equals(itemName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.RowId, item.Name.ToString()))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void Finish(string state, string message)
    {
        lock (sync)
        {
            view = view with { State = state, Message = message, Active = false, UpdatedAtUtc = DateTimeOffset.UtcNow };
        }
    }

    public void Dispose()
    {
        lock (sync) operation?.Cancel();
        session.CancelActive();
    }
}
