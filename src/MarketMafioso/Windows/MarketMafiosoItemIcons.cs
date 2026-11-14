using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Windows;

/// <summary>Game item icons for UI threads. Shared textures load asynchronously; callers retry per frame.</summary>
internal static class MarketMafiosoItemIcons
{
    private static readonly Dictionary<uint, ISharedImmediateTexture?> Cache = new();

    public static ISharedImmediateTexture? Resolve(uint itemId)
    {
        if (itemId == 0)
            return null;
        if (Cache.TryGetValue(itemId, out var cached))
            return cached;

        ISharedImmediateTexture? texture = null;
        try
        {
            var item = Plugin.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            if (item is not null)
                texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Value.Icon));
        }
        catch
        {
            return null;
        }

        Cache[itemId] = texture;
        return texture;
    }

    /// <summary>The icon's texture handle once loaded, otherwise null.</summary>
    public static ImTextureID? Handle(uint itemId) =>
        Resolve(itemId)?.TryGetWrap(out var wrap, out _) == true ? wrap.Handle : null;
}
