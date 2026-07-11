using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private readonly byte[] protectionEntropy;
    private readonly Func<AgentBridgeCaptureRegion?> captureRegion;
    private readonly Func<Action, Task> dispatchOnFramework;
    private readonly ITextureProvider textureProvider;
    private readonly ITextureReadbackProvider readbackProvider;
    private readonly SemaphoreSlim captureLock = new(1, 1);

    public AgentBridgeViewportCaptureService(
        string configDirectory,
        string pluginInstanceId,
        Func<AgentBridgeCaptureRegion?> captureRegion,
        Func<Action, Task> dispatchOnFramework,
        ITextureProvider textureProvider,
        ITextureReadbackProvider readbackProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginInstanceId);
        this.captureRegion = captureRegion ?? throw new ArgumentNullException(nameof(captureRegion));
        this.dispatchOnFramework = dispatchOnFramework ?? throw new ArgumentNullException(nameof(dispatchOnFramework));
        this.textureProvider = textureProvider ?? throw new ArgumentNullException(nameof(textureProvider));
        this.readbackProvider = readbackProvider ?? throw new ArgumentNullException(nameof(readbackProvider));
        captureDirectory = Path.Combine(configDirectory, "agent-bridge", "captures");
        protectionEntropy = Encoding.UTF8.GetBytes(pluginInstanceId);
    }

    public async Task<AgentBridgeCaptureReceipt> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var region = captureRegion();
            if (region == null || DateTimeOffset.UtcNow - region.RenderedAtUtc > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("MMF is not currently rendered; no screenshot was captured.");

            Task<IDalamudTextureWrap>? textureTask = null;
            await dispatchOnFramework(() =>
            {
                var currentRegion = captureRegion();
                if (currentRegion == null || DateTimeOffset.UtcNow - currentRegion.RenderedAtUtc > TimeSpan.FromSeconds(5))
                    throw new InvalidOperationException("MMF is not currently rendered; no screenshot was captured.");

                textureTask = textureProvider.CreateFromImGuiViewportAsync(
                    new ImGuiViewportTextureArgs
                    {
                        ViewportId = ImGui.GetMainViewport().ID,
                        AutoUpdate = false,
                        TakeBeforeImGuiRender = false,
                        KeepTransparency = false,
                        Uv0 = currentRegion.GetUv0(),
                        Uv1 = currentRegion.GetUv1(),
                    },
                    "MarketMafioso agent bridge viewport capture",
                    cancellationToken);
            }).ConfigureAwait(false);

            using var texture = await (textureTask ?? throw new InvalidOperationException("Viewport capture was not scheduled."))
                .ConfigureAwait(false);
            var captureId = Guid.NewGuid().ToString("N");
            var fileName = $"{captureId}.bin";
            Directory.CreateDirectory(captureDirectory);
            var path = Path.Combine(captureDirectory, fileName);
            var pngCodec = readbackProvider.GetSupportedImageEncoderInfos()
                .Single(codec => codec.MimeTypes.Contains("image/png", StringComparer.OrdinalIgnoreCase));
            await using var output = new MemoryStream();
            await readbackProvider.SaveToStreamAsync(
                texture,
                pngCodec.ContainerGuid,
                output,
                new Dictionary<string, object>(),
                leaveWrapOpen: true,
                leaveStreamOpen: true,
                cancellationToken).ConfigureAwait(false);

            var capturedAtUtc = DateTimeOffset.UtcNow;
            var pngBytes = output.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(pngBytes));
            var protectedBytes = ProtectedData.Protect(pngBytes, protectionEntropy, DataProtectionScope.CurrentUser);
            CryptographicOperations.ZeroMemory(pngBytes);
            await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(protectedBytes);
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
                     .EnumerateFiles("*.bin")
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(20))
        {
            file.Delete();
        }
    }
}

public sealed record AgentBridgeCaptureRegion(
    System.Numerics.Vector2 WindowPosition,
    System.Numerics.Vector2 WindowSize,
    System.Numerics.Vector2 ViewportPosition,
    System.Numerics.Vector2 ViewportSize,
    DateTimeOffset RenderedAtUtc)
{
    private const float PaddingPixels = 8f;

    public System.Numerics.Vector2 GetUv0()
    {
        var left = (WindowPosition.X - PaddingPixels - ViewportPosition.X) / ViewportSize.X;
        var top = (WindowPosition.Y - PaddingPixels - ViewportPosition.Y) / ViewportSize.Y;
        return new System.Numerics.Vector2(Math.Clamp(left, 0f, 1f), Math.Clamp(top, 0f, 1f));
    }

    public System.Numerics.Vector2 GetUv1()
    {
        var right = (WindowPosition.X + WindowSize.X + PaddingPixels - ViewportPosition.X) / ViewportSize.X;
        var bottom = (WindowPosition.Y + WindowSize.Y + PaddingPixels - ViewportPosition.Y) / ViewportSize.Y;
        return new System.Numerics.Vector2(Math.Clamp(right, 0f, 1f), Math.Clamp(bottom, 0f, 1f));
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
