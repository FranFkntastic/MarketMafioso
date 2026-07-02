using MarketMafioso.Automation.Safety;

namespace MarketMafioso.Tests.Automation.Safety;

public sealed class AutomationOperationResultTests
{
    [Fact]
    public void Success_creates_successful_result_with_message()
    {
        var result = AutomationOperationResult.Success("Ready.");

        Assert.True(result.IsSuccess);
        Assert.Equal(AutomationFailureKind.None, result.FailureKind);
        Assert.Equal("Ready.", result.Message);
        Assert.Empty(result.Details);
    }

    [Fact]
    public void Fail_requires_non_none_failure_kind()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AutomationOperationResult.Fail(AutomationFailureKind.None, "Not actually failed."));

        Assert.Equal("failureKind", exception.ParamName);
    }

    [Fact]
    public void Fail_creates_failed_result_with_details()
    {
        var result = AutomationOperationResult.Fail(
            AutomationFailureKind.MissingAddon,
            "Retainer list is not visible.",
            new Dictionary<string, string?> { ["addon"] = "RetainerList" });

        Assert.False(result.IsSuccess);
        Assert.Equal(AutomationFailureKind.MissingAddon, result.FailureKind);
        Assert.Equal("Retainer list is not visible.", result.Message);
        Assert.Equal("RetainerList", result.Details["addon"]);
    }

    [Fact]
    public void Verification_success_records_before_and_after_values()
    {
        var verification = AutomationStateVerification.Success(
            "PlayerQuantity",
            before: "10",
            after: "5",
            "Quantity decreased as expected.");

        Assert.True(verification.IsVerified);
        Assert.Equal("PlayerQuantity", verification.Subject);
        Assert.Equal("10", verification.Before);
        Assert.Equal("5", verification.After);
    }

    [Fact]
    public void Verification_failure_records_failure_kind()
    {
        var verification = AutomationStateVerification.Fail(
            "ListingIdentity",
            before: "listing-1",
            after: "listing-2",
            AutomationFailureKind.IdentityChanged,
            "Listing changed before purchase.");

        Assert.False(verification.IsVerified);
        Assert.Equal(AutomationFailureKind.IdentityChanged, verification.FailureKind);
    }
}
