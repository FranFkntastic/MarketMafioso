using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketMafioso.MarketAcquisition;

internal sealed class MarketAcquisitionItemContextMenu : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IGameGui gameGui;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly Func<bool> isAvailable;
    private readonly Func<bool> canStage;
    private readonly Func<uint, string, string> stage;

    public MarketAcquisitionItemContextMenu(
        IContextMenu contextMenu,
        IGameGui gameGui,
        IDataManager dataManager,
        IFramework framework,
        IChatGui chatGui,
        Func<bool> isAvailable,
        Func<bool> canStage,
        Func<uint, string, string> stage)
    {
        this.contextMenu = contextMenu ?? throw new ArgumentNullException(nameof(contextMenu));
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.chatGui = chatGui ?? throw new ArgumentNullException(nameof(chatGui));
        this.isAvailable = isAvailable ?? throw new ArgumentNullException(nameof(isAvailable));
        this.canStage = canStage ?? throw new ArgumentNullException(nameof(canStage));
        this.stage = stage ?? throw new ArgumentNullException(nameof(stage));

        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!isAvailable() || !TryResolveItem(args, out var itemId, out var itemName))
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Add to Market Acquisition",
            PrefixChar = 'M',
            IsEnabled = canStage(),
            OnClicked = _ => framework.RunOnTick(() =>
                chatGui.Print($"[MMF] {stage(itemId, itemName)}")),
        });
    }

    private bool TryResolveItem(IMenuOpenedArgs args, out uint itemId, out string itemName)
    {
        itemId = 0;
        itemName = string.Empty;

        if (args.MenuType == ContextMenuType.Inventory &&
            args.Target is MenuTargetInventory { TargetItem: { } targetItem })
        {
            itemId = NormalizeHoveredItemId(targetItem.ItemId);
        }
        else
        {
            itemId = NormalizeHoveredItemId(gameGui.HoveredItem);
        }

        if (itemId == 0)
            return false;

        var item = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
        itemName = item?.Name.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(itemName);
    }

    internal static uint NormalizeHoveredItemId(ulong hoveredItemId)
    {
        if (hoveredItemId == 0 || hoveredItemId >= 2_000_000)
            return 0;

        return checked((uint)(hoveredItemId % 500_000));
    }
}
