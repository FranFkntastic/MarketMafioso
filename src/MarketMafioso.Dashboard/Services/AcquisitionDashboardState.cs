using MarketMafioso.Dashboard.Models;

namespace MarketMafioso.Dashboard.Services;

public sealed class AcquisitionDashboardState
{
    private readonly DashboardApiClient api;
    private readonly DashboardStatusService status;

    public AcquisitionDashboardState(DashboardApiClient api, DashboardStatusService status)
    {
        this.api = api;
        this.status = status;
    }

    public event Action? Changed;

    public IReadOnlyList<MarketAcquisitionRequestView> Requests { get; private set; } = [];
    public MarketAcquisitionRequestView? SelectedRequest { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IncludeTerminal { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await RefreshCoreAsync(cancellationToken);
        }, "Refresh failed.");
    }

    public void ApplySnapshot(IReadOnlyList<MarketAcquisitionRequestView> requests)
    {
        Requests = IncludeTerminal ? requests : requests.Where(request => !IsTerminalRequest(request)).ToArray();
        ReconcileSelectedRequest();
        Changed?.Invoke();
    }

    public async Task SetIncludeTerminalAsync(
        bool includeTerminal,
        CancellationToken cancellationToken = default)
    {
        if (IncludeTerminal == includeTerminal)
            return;

        IncludeTerminal = includeTerminal;
        await RefreshAsync(cancellationToken);
    }

    public void SelectRequest(MarketAcquisitionRequestView? request)
    {
        SelectedRequest = request;
        Changed?.Invoke();
    }

    public async Task CancelAsync(string requestId, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await api.CancelAcquisitionRequestAsync(requestId, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
            status.Info("Request cancelled.");
        }, "Cancel failed.");
    }

    public async Task ResendAsync(string requestId, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await api.ResendAcquisitionRequestAsync(requestId, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
            status.Info("Request resent.");
        }, "Resend failed.");
    }

    public async Task<bool> StageAsync(
        MarketAcquisitionBatchCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(async () =>
        {
            await api.CreateAcquisitionBatchAsync(request, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
            status.Info($"Staged acquisition batch with {request.Lines.Count:N0} line(s).");
        }, "Stage queue failed.");
    }

    public async Task<bool> AppendAsync(
        string requestId,
        MarketAcquisitionBatchAppendLinesRequest request,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(async () =>
        {
            await api.AppendAcquisitionBatchLinesAsync(requestId, request, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
            status.Info($"Added {request.Lines.Count:N0} line(s) to pending acquisition batch.");
        }, "Append queue failed.");
    }

    public async Task<MarketAcquisitionRequestView?> ReplaceAsync(
        string requestId,
        MarketAcquisitionBatchReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        MarketAcquisitionRequestView? updated = null;
        var succeeded = await RunAsync(async () =>
        {
            updated = await api.ReplaceAcquisitionBatchAsync(requestId, request, cancellationToken);
            UpsertRequest(updated);
            status.Info($"Updated acquisition request with {request.Lines.Count:N0} line(s).");
        }, "Update request failed.");

        return succeeded ? updated : null;
    }

    public void UpsertRequest(MarketAcquisitionRequestView request)
    {
        var list = Requests.ToList();
        var index = list.FindIndex(existing => existing.Id == request.Id);
        if (index >= 0)
            list[index] = request;
        else if (IncludeTerminal || !IsTerminalRequest(request))
            list.Insert(0, request);

        Requests = list;
        SelectedRequest = request;
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        Requests = await api.GetAcquisitionRequestsAsync(IncludeTerminal, cancellationToken);
        ReconcileSelectedRequest();
    }

    private async Task<bool> RunAsync(Func<Task> action, string failurePrefix)
    {
        try
        {
            IsBusy = true;
            Changed?.Invoke();
            await action();
            return true;
        }
        catch (Exception ex)
        {
            status.Error($"{failurePrefix} {ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    private void ReconcileSelectedRequest()
    {
        if (SelectedRequest is null)
            return;

        SelectedRequest = Requests.FirstOrDefault(request => request.Id == SelectedRequest.Id);
    }

    private static bool IsTerminalRequest(MarketAcquisitionRequestView request) =>
        request.Status is "Complete" or "Failed" or "Cancelled" or "Rejected" or "Expired";
}
