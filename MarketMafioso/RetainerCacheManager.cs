using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace MarketMafioso;

/// <summary>
/// Hooks the game's retainer inventory addon to snapshot retainer data
/// into <see cref="Configuration.RetainerCache"/> each time a retainer
/// window is closed.
/// </summary>
public class RetainerCacheManager : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly InventoryScanner scanner;
    private readonly HttpReporter reporter;
    private bool isBatchRefreshActive;

    // Both addon names are registered so the handler fires regardless of
    // which layout the game uses (depends on player's bag count / resolution).
    private const string LargeAddon = "InventoryRetainerLarge";
    private const string SmallAddon = "InventoryRetainer";

    // Retainer ID captured when the window opens; used when it closes.
    private ulong _activeRetainerId;
    private string _activeRetainerName = string.Empty;

    /// <summary>Raised after a retainer has been successfully cached.</summary>
    public event Action? RetainerCached;

    public RetainerCacheManager(
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        Configuration config,
        InventoryScanner scanner,
        HttpReporter reporter)
    {
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.config = config;
        this.scanner = scanner;
        this.reporter = reporter;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, LargeAddon, OnRetainerWindowOpen);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, LargeAddon, OnRetainerWindowClose);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, SmallAddon, OnRetainerWindowOpen);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, SmallAddon, OnRetainerWindowClose);
    }


    private unsafe void OnRetainerWindowOpen(AddonEvent type, AddonArgs args)
    {
        try
        {
            var rm = RetainerManager.Instance();
            if (rm == null) return;

            var activeRetainer = rm->GetActiveRetainer();
            if (activeRetainer == null || activeRetainer->RetainerId == 0)
            {
                log.Warning("[MarketMafioso] Retainer window opened but no active retainer was found.");
                return;
            }

            _activeRetainerId = activeRetainer->RetainerId;
            fixed (byte* namePtr = activeRetainer->Name)
            {
                _activeRetainerName = Marshal.PtrToStringUTF8((nint)namePtr, 32)
                                     ?.Split('\0')[0]
                                     ?? string.Empty;
            }

            log.Debug($"[MarketMafioso] Retainer window opened for '{_activeRetainerName}' (id={_activeRetainerId})");
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Error in OnRetainerWindowOpen");
        }
    }

    private unsafe void OnRetainerWindowClose(AddonEvent type, AddonArgs args)
    {
        if (_activeRetainerId == 0)
        {
            log.Warning("[MarketMafioso] Retainer window closed but active retainer ID is unknown - skipping cache.");
            return;
        }

        try
        {
            var bags = scanner.ScanCurrentRetainer(config);

            var cachedBags = bags
                .Select(b => new CachedBag
                {
                    BagName = b.BagName,
                    Items = b.Items
                        .Select(i => new CachedItem
                        {
                            ItemId = i.ItemId,
                            ItemName = i.ItemName,
                            ItemType = i.ItemType,
                            Quantity = i.Quantity,
                            IsHQ = i.IsHQ,
                            Condition = i.Condition,
                        })
                        .ToList(),
                })
                .ToList();

            var totalItems = cachedBags.Sum(b => b.Items.Count);

            config.RetainerCache[_activeRetainerId] = new CachedRetainer
            {
                RetainerId = _activeRetainerId,
                RetainerName = _activeRetainerName,
                LastUpdated = DateTime.UtcNow,
                Gil = scanner.ScanCurrentRetainerGil(),
                Bags = cachedBags,
                MarketListings = scanner.ScanCurrentRetainerMarketListings(config)
                    .Select(i => new CachedMarketListing
                    {
                        ItemId = i.ItemId,
                        ItemName = i.ItemName,
                        ItemType = i.ItemType,
                        Quantity = i.Quantity,
                        IsHQ = i.IsHQ,
                        Condition = i.Condition,
                        UnitPrice = i.UnitPrice,
                        ListedAt = i.ListedAt,
                    })
                    .ToList(),
            };

            config.Save();

            log.Information(
                $"[MarketMafioso] Cached retainer '{_activeRetainerName}' - {totalItems} item(s) across {cachedBags.Count} bag(s).");

            RetainerCached?.Invoke();

            if (config.AutoSendOnRetainerClose && !isBatchRefreshActive)
                _ = reporter.SendReportAsync();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Error caching retainer inventory");
        }
        finally
        {
            _activeRetainerId = 0;
            _activeRetainerName = string.Empty;
        }
    }




    public void BeginBatchRefresh()
    {
        isBatchRefreshActive = true;
    }

    public void EndBatchRefresh()
    {
        isBatchRefreshActive = false;
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, LargeAddon, OnRetainerWindowOpen);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, LargeAddon, OnRetainerWindowClose);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, SmallAddon, OnRetainerWindowOpen);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, SmallAddon, OnRetainerWindowClose);
    }
}
