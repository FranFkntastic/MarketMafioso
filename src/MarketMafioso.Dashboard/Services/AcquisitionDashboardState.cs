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

    public Task ShelfAsync(MarketAcquisitionRequestView workOrder, CancellationToken cancellationToken = default) =>
        ApplyWorkOrderCommandAsync(workOrder, "shelf", "Work order shelved.", cancellationToken);

    public Task RestoreAsync(MarketAcquisitionRequestView workOrder, CancellationToken cancellationToken = default) =>
        ApplyWorkOrderCommandAsync(workOrder, "restore", "Work order returned to the inbox.", cancellationToken);

    public Task ArchiveAsync(MarketAcquisitionRequestView workOrder, CancellationToken cancellationToken = default) =>
        ApplyWorkOrderCommandAsync(workOrder, "archive", "Work order archived.", cancellationToken);

    public async Task CloneAsync(MarketAcquisitionRequestView workOrder, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await api.CloneAcquisitionWorkOrderAsync(workOrder.Id, workOrder.Revision, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
            status.Info("Reusable copy added to the inbox.");
        }, "Clone failed.");
    }

    public async Task MergeAsync(
        MarketAcquisitionRequestView target,
        MarketAcquisitionRequestView source,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var preview = await api.PreviewAcquisitionWorkOrderMergeAsync(target.Id, source.Id, cancellationToken);
            if (!preview.CanMerge)
            {
                var conflicts = string.Join("; ", preview.Conflicts.Select(conflict => conflict.Message));
                throw new InvalidOperationException($"Merge blocked: {conflicts}");
            }

            await api.MergeAcquisitionWorkOrdersAsync(target, source, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
            status.Info($"Merged work orders into {preview.ResultLineCount:N0} inbox line(s); the source was archived.");
        }, "Merge failed.");
    }

    public async Task<bool> StageAsync(
        MarketAcquisitionBatchCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(async () =>
        {
            await api.CreateAcquisitionBatchAsync(request, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
            status.Info($"Published work order with {request.Lines.Count:N0} line(s) to the inbox.");
        }, "Publish failed.");
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

    private async Task ApplyWorkOrderCommandAsync(
        MarketAcquisitionRequestView workOrder,
        string action,
        string successMessage,
        CancellationToken cancellationToken)
    {
        await RunAsync(async () =>
        {
            await api.ApplyAcquisitionWorkOrderCommandAsync(workOrder.Id, action, workOrder.Revision, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
            status.Info(successMessage);
        }, $"{action} failed.");
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
        request.Status is "Complete" or "Failed" or "Cancelled" or "Rejected" or "Expired" or "Archived";
}
