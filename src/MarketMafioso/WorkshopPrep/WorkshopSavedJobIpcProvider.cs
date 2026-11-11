using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace MarketMafioso.WorkshopPrep;

/// <summary>
/// Lets another plugin hand MarketMafioso a shared workshop saved job. The job is only stored;
/// it is never loaded into the active queue or run.
/// </summary>
public sealed class WorkshopSavedJobIpcProvider : IDisposable
{
    public const string ImportChannel = "MarketMafioso.v1.ImportWorkshopSavedJob";
    public const string ResponseSchema = "gooseworks-workshop-saved-job-import-response/v1";

    private readonly ICallGateProvider<string, string> provider;
    private readonly IFramework framework;
    private readonly Func<string?, WorkshopQueueOperationResult> import;

    public WorkshopSavedJobIpcProvider(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        Func<string?, WorkshopQueueOperationResult> import)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        this.framework = framework ?? throw new ArgumentNullException(nameof(framework));
        this.import = import ?? throw new ArgumentNullException(nameof(import));
        provider = pluginInterface.GetIpcProvider<string, string>(ImportChannel);
        provider.RegisterFunc(Import);
    }

    public void Dispose() => provider.UnregisterFunc();

    private string Import(string json)
    {
        WorkshopQueueOperationResult result;
        try
        {
            // Saved jobs live in the configuration the UI edits, so mutate it on the framework thread.
            result = framework.RunOnFrameworkThread(() => import(json)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            result = new WorkshopQueueOperationResult(false, $"Saved job import failed: {exception.Message}");
        }

        return JsonSerializer.Serialize(new
        {
            Schema = ResponseSchema,
            Accepted = result.Success,
            result.Message,
            Name = result.Success ? result.Name : null,
        });
    }
}
