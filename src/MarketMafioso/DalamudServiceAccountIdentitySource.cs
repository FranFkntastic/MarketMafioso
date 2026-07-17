using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.Reflection;

namespace MarketMafioso;

/// <summary>
/// Produces an opaque, profile-scoped service-account identity for grouping characters.
/// AutoRetainer supplies the account ordinal when available; the Dalamud profile path
/// keeps identical ordinals from separate launcher profiles distinct without uploading it.
/// </summary>
public sealed class DalamudServiceAccountIdentitySource
{
    private const string AutoRetainerInternalName = "AutoRetainer";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    public DalamudServiceAccountIdentitySource(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Resolve(ulong localContentId)
    {
        var serviceAccount = ResolveAutoRetainerServiceAccount(localContentId) ?? 0;
        var profileScope = pluginInterface.GetPluginConfigDirectory()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var input = Encoding.UTF8.GetBytes($"{profileScope}|{serviceAccount}");
        return Convert.ToHexStringLower(SHA256.HashData(input))[..24];
    }

    private int? ResolveAutoRetainerServiceAccount(ulong localContentId)
    {
        if (localContentId == 0 ||
            !pluginInterface.InstalledPlugins.Any(plugin =>
                plugin.IsLoaded && string.Equals(plugin.InternalName, AutoRetainerInternalName, StringComparison.OrdinalIgnoreCase)))
            return null;

        try
        {
            if (!DalamudReflector.TryGetDalamudPlugin(
                    AutoRetainerInternalName,
                    out var instance,
                    out _,
                    suppressErrors: true,
                    ignoreCache: true) ||
                instance is null ||
                Read(instance, "config") is not { } config ||
                Read(config, "OfflineData") is not IEnumerable characters)
                return null;

            foreach (var character in characters)
            {
                if (character is null ||
                    !TryConvert(Read(character, "CID"), out ulong contentId) ||
                    contentId != localContentId)
                    continue;

                return TryConvert(Read(character, "ServiceAccount"), out int serviceAccount)
                    ? serviceAccount
                    : null;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MarketMafioso] AutoRetainer service-account metadata is unavailable");
        }

        return null;
    }

    private static object? Read(object source, string name)
    {
        var type = source.GetType();
        return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source)
               ?? type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
    }

    private static bool TryConvert<T>(object? source, out T value) where T : struct
    {
        try
        {
            if (source is not null)
            {
                value = (T)Convert.ChangeType(source, typeof(T));
                return true;
            }
        }
        catch (Exception) when (source is not null)
        {
        }

        value = default;
        return false;
    }
}
