namespace MarketMafioso.Dashboard.Services;

public sealed class DashboardUnauthorizedException : Exception
{
    public DashboardUnauthorizedException()
        : base("Dashboard session is required.")
    {
    }
}
