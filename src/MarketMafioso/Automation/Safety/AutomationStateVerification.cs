using System;

namespace MarketMafioso.Automation.Safety;

public sealed record AutomationStateVerification(
    bool IsVerified,
    string Subject,
    string? Before,
    string? After,
    AutomationFailureKind FailureKind,
    string Message)
{
    public static AutomationStateVerification Success(
        string subject,
        string? before,
        string? after,
        string message) =>
        new(true, subject, before, after, AutomationFailureKind.None, message);

    public static AutomationStateVerification Fail(
        string subject,
        string? before,
        string? after,
        AutomationFailureKind failureKind,
        string message)
    {
        if (failureKind == AutomationFailureKind.None)
            throw new ArgumentException("Failure results must use a non-None failure kind.", nameof(failureKind));

        return new AutomationStateVerification(false, subject, before, after, failureKind, message);
    }
}
