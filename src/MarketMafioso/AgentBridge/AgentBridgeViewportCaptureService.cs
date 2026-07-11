using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace MarketMafioso.AgentBridge;

public sealed class AgentBridgeViewportCaptureService
{
    private readonly string captureDirectory;
    private readonly Func<Action, Task> dispatchOnFramework;
    private readonly ITextureProvider textureProvider;
    private readonly ITextureReadbackProvider readbackProvider;
    private readonly SemaphoreSlim captureLock = new(1, 1);

    public AgentBridgeViewportCaptureService(
        string configDirectory,
        Func<Action, Task> dispatchOnFramework,
        ITextureProvider textureProvider,
        ITextureReadbackProvider readbackProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        this.dispatchOnFramework = dispatchOnFramework ?? throw new ArgumentNullException(nameof(dispatchOnFramework));
        this.textureProvider = textureProvider ?? throw new ArgumentNullException(nameof(textureProvider));
        this.readbackProvider = readbackProvider ?? throw new ArgumentNullException(nameof(readbackProvider));
        captureDirectory = Path.Combine(configDirectory, "agent-bridge", "captures");
    }

    public async Task<AgentBridgeCaptureReceipt> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task<IDalamudTextureWrap>? textureTask = null;
            await dispatchOnFramework(() =>
            {
                textureTask = textureProvider.CreateFromImGuiViewportAsync(
                    new ImGuiViewportTextureArgs
                    {
                        ViewportId = ImGui.GetMainViewport().ID,
                        AutoUpdate = false,
                        TakeBeforeImGuiRender = false,
                        KeepTransparency = false,
                    },
                    "MarketMafioso agent bridge viewport capture",
                    cancellationToken);
            }).ConfigureAwait(false);

            using var texture = await (textureTask ?? throw new InvalidOperationException("Viewport capture was not scheduled."))
                .ConfigureAwait(false);
            var captureId = Guid.NewGuid().ToString("N");
            var fileName = $"{captureId}.png";
            Directory.CreateDirectory(captureDirectory);
            var path = Path.Combine(captureDirectory, fileName);
            var pngCodec = readbackProvider.GetSupportedImageEncoderInfos()
                .Single(codec => codec.MimeTypes.Contains("image/png", StringComparer.OrdinalIgnoreCase));
            await readbackProvider.SaveToFileAsync(
                texture,
                pngCodec.ContainerGuid,
                path,
                new Dictionary<string, object>(),
                leaveWrapOpen: true,
                cancellationToken).ConfigureAwait(false);

            var capturedAtUtc = DateTimeOffset.UtcNow;
            await using var captureStream = File.OpenRead(path);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(
                captureStream,
                cancellationToken).ConfigureAwait(false));
            var receipt = new AgentBridgeCaptureReceipt
            {
                SchemaVersion = 1,
                CaptureId = captureId,
                FileName = fileName,
                CapturedAtUtc = capturedAtUtc,
                Width = texture.Width,
                Height = texture.Height,
                Sha256 = sha256,
                ProcessId = Environment.ProcessId,
            };
            await File.WriteAllTextAsync(
                Path.ChangeExtension(path, ".json"),
                JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
            PruneOldCaptures();
            return receipt;
        }
        finally
        {
            captureLock.Release();
        }
    }

    private void PruneOldCaptures()
    {
        foreach (var file in new DirectoryInfo(captureDirectory)
                     .EnumerateFiles("*.png")
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(20))
        {
            file.Delete();
            var metadataPath = Path.ChangeExtension(file.FullName, ".json");
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
    }
}

public sealed record AgentBridgeCaptureReceipt
{
    public required int SchemaVersion { get; init; }
    public required string CaptureId { get; init; }
    public required string FileName { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Sha256 { get; init; }
    public required int ProcessId { get; init; }
}
