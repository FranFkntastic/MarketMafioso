namespace MarketMafioso.Windows.AcquisitionWorkbench;

public static class AcquisitionWorkbenchPricingFormatter
{
    public static string FormatOptionalGil(uint gil) =>
        gil == 0 ? "Unset" : $"{gil:N0} gil";
}
