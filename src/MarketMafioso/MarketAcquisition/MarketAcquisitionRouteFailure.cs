using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MarketMafioso.MarketAcquisition;

// Route-owned exception metadata only: never enumerate Exception.Data or serialize requests.
internal static class MarketAcquisitionRouteFailure
{
    private sealed class Context
    {
        internal readonly Dictionary<string, string?> Details = new(StringComparer.Ordinal);
        internal bool Reported;
    }

    private static readonly ConditionalWeakTable<Exception, Context> Contexts = new();

    internal static void Annotate(Exception exception, string stage, string? target = null,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        try
        {
            var context = Contexts.GetOrCreateValue(exception);
            context.Details.TryAdd("failureStage", stage);
            context.Details["failureStages"] = context.Details.TryGetValue("failureStages", out var stages)
                ? stages + " <- " + stage : stage;
            if (target != null)
                context.Details.TryAdd("persistenceTarget", target);
            if (details != null)
                foreach (var detail in details)
                    if (detail.Value != null)
                        context.Details.TryAdd(detail.Key, detail.Value);
        }
        catch { /* Diagnostics must never replace the original failure. */ }
    }

    internal static bool TryBeginReport(Exception exception)
    {
        var context = Contexts.GetOrCreateValue(exception);
        if (context.Reported)
            return false;
        context.Reported = true;
        return true;
    }

    internal static Dictionary<string, string?> Describe(Exception exception)
    {
        var details = new Dictionary<string, string?>(Contexts.GetOrCreateValue(exception).Details, StringComparer.Ordinal)
        {
            ["exceptionType"] = exception.GetType().FullName,
            ["exceptionMessage"] = exception.Message,
            ["exceptionDetails"] = exception.ToString(),
        };
        AddException(exception, "exception", details);
        return details;
    }

    private static void AddException(Exception exception, string key, Dictionary<string, string?> details)
    {
        details[key + ".type"] = exception.GetType().FullName;
        details[key + ".hresult"] = $"0x{exception.HResult:X8}";
        if (exception is Win32Exception native)
            details[key + ".nativeErrorCode"] = native.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (exception is AggregateException aggregate)
        {
            for (var index = 0; index < aggregate.InnerExceptions.Count; index++)
                AddException(aggregate.InnerExceptions[index], key + $".inner[{index}]", details);
        }
        else if (exception.InnerException is { } inner)
            AddException(inner, key + ".inner", details);
    }
}
