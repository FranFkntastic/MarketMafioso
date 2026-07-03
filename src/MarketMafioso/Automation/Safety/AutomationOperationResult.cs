using System;
using System.Collections.Generic;

namespace MarketMafioso.Automation.Safety;

public sealed record AutomationOperationResult(
    bool IsSuccess,
    AutomationFailureKind FailureKind,
    string Message,
    IReadOnlyDictionary<string, string?> Details)
{
    public static AutomationOperationResult Success(
        string message,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(true, AutomationFailureKind.None, message, details ?? new Dictionary<string, string?>());

    public static AutomationOperationResult Fail(
        AutomationFailureKind failureKind,
        string message,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        if (failureKind == AutomationFailureKind.None)
            throw new ArgumentException("Failure results must use a non-None failure kind.", nameof(failureKind));

        return new AutomationOperationResult(false, failureKind, message, details ?? new Dictionary<string, string?>());
    }
}
