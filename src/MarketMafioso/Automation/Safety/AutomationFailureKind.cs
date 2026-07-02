namespace MarketMafioso.Automation.Safety;

public enum AutomationFailureKind
{
    None,
    MissingAddon,
    StaleState,
    CapacityUnknown,
    IdentityChanged,
    Timeout,
    UserStopped,
    UnsafeContext,
    ExternalDependencyUnavailable,
    VerificationFailed,
}
