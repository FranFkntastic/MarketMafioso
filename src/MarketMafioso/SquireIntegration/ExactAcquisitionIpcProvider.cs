using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using MarketMafioso.MarketAcquisition.ExactAuthority;

namespace MarketMafioso.SquireIntegration;

public sealed class ExactAcquisitionIpcProvider : IDisposable
{
    public const string StageChannel = "MarketMafioso.v1.StageExactAcquisition";
    public const string ResponseSchema = "gooseworks-exact-acquisition-stage-response/v1";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ICallGateProvider<string, string> provider;
    private readonly Action<ExactAcquisitionWorkbenchTransfer> stage;

    public ExactAcquisitionIpcProvider(
        IDalamudPluginInterface pluginInterface,
        Action<ExactAcquisitionWorkbenchTransfer> stage)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        this.stage = stage ?? throw new ArgumentNullException(nameof(stage));
        provider = pluginInterface.GetIpcProvider<string, string>(StageChannel);
        provider.RegisterFunc(Stage);
    }

    public void Dispose() => provider.UnregisterFunc();

    private string Stage(string json)
    {
        try
        {
            var transfer = JsonSerializer.Deserialize<ExactAcquisitionWorkbenchTransfer>(json, JsonOptions)
                ?? throw new InvalidOperationException("The exact-acquisition transfer was empty.");
            stage(transfer);
            return JsonSerializer.Serialize(new
            {
                Schema = ResponseSchema,
                Accepted = true,
                Error = (string?)null,
            });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return JsonSerializer.Serialize(new
            {
                Schema = ResponseSchema,
                Accepted = false,
                Error = exception.Message,
            });
        }
    }
}
