using MarketMafioso.Dashboard.Models;

namespace MarketMafioso.Dashboard.Services;

public sealed class DashboardStatusService
{
    public event Action? Changed;

    public DashboardNotice? Current { get; private set; }

    public void Info(string message)
    {
        Current = new DashboardNotice { Message = message, IsError = false };
        Changed?.Invoke();
    }

    public void Error(string message)
    {
        Current = new DashboardNotice { Message = message, IsError = true };
        Changed?.Invoke();
    }

    public void Clear()
    {
        Current = null;
        Changed?.Invoke();
    }
}
