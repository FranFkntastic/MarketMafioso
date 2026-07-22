using MarketMafioso.Automation.Safety;

namespace MarketMafioso.Tests.Automation.Safety;

public sealed class AutomationOperationResultTests
{
    [Fact]
    public void Fail_requires_non_none_failure_kind()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AutomationOperationResult.Fail(AutomationFailureKind.None, "Not actually failed."));

        Assert.Equal("failureKind", exception.ParamName);
    }

}
