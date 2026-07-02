using System;
using System.Collections.Generic;
using MarketMafioso.Automation.Diagnostics;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyDiagnostics : IDisposable
{
    private static readonly WorkshopAssemblyDiagnostics DisabledInstance = new(AutomationDiagnosticsLog.Disabled);

    private readonly AutomationDiagnosticsLog log;

    private WorkshopAssemblyDiagnostics(AutomationDiagnosticsLog log)
    {
        this.log = log;
    }

    public static WorkshopAssemblyDiagnostics Disabled => DisabledInstance;

    public bool IsEnabled => log.IsEnabled;

    public string? FilePath => log.FilePath;

    public static WorkshopAssemblyDiagnostics CreateEnabled(string directory, DateTimeOffset startedAt)
    {
        return new WorkshopAssemblyDiagnostics(
            AutomationDiagnosticsLog.CreateEnabled(
                directory,
                startedAt,
                "assembly",
                "Workshop assembly diagnostics started.",
                null));
    }

    public void Record(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        log.Record(eventName, message, details);
    }

    public void Complete(string message)
    {
        log.Complete(message);
    }

    public void Fail(string message, Exception? exception = null)
    {
        log.Fail(message, exception);
    }

    public void Dispose()
    {
        log.Dispose();
    }
}
