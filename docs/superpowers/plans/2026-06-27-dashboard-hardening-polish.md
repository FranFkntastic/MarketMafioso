# Dashboard Hardening And Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the Blazor dashboard from a thin acquisition form into a reliable operational app with clearer state, better debugging, resilient inputs, and polished dense tables.

**Architecture:** Keep the existing Blazor WebAssembly + MudBlazor rebuild, but split `Home.razor` into focused components backed by a scoped acquisition state service. Keep the server as the source of truth for queued requests, attempts, and diagnostics; the dashboard should own only transient builder state, selected rows, and UI preferences.

**Tech Stack:** .NET 10 Blazor WebAssembly, MudBlazor 8.14, ASP.NET Core JSON APIs, server-sent events, SQLite-backed server diagnostics/acquisition state.

---

## File Structure

Create:

- `MarketMafioso.Dashboard/Services/AcquisitionDashboardState.cs`  
  Owns request list, selected request, queue refresh, SSE snapshots, and request actions.
- `MarketMafioso.Dashboard/Services/DashboardStatusService.cs`  
  Owns transient dashboard notices so API errors do not live inside page components.
- `MarketMafioso.Dashboard/Models/DashboardUiModels.cs`  
  Client-only models such as `DashboardNotice`, `QueuedAcquisitionItem`, and formatted status helpers.
- `MarketMafioso.Dashboard/Components/Status/DashboardStatusHost.razor`  
  Renders current dashboard notices consistently.
- `MarketMafioso.Dashboard/Components/Status/LiveStatusStrip.razor`  
  Renders SSE status, request count, selected request state, and refresh actions.
- `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`  
  Owns item lookup, target settings, purchase limits, and local queued items.
- `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`  
  Dense sortable/resizable request grid with row selection and actions.
- `MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`  
  Shows selected request lifecycle, attempt state, latest event, retry/cancel/resend/archive actions.
- `MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticsEventGrid.razor`  
  Reusable diagnostics table with filters and row selection.
- `MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticEventDrawer.razor`  
  Shows event details and sanitized payload summaries.

Modify:

- `MarketMafioso.Dashboard/Program.cs`  
  Register new scoped services.
- `MarketMafioso.Dashboard/Pages/Home.razor`  
  Replace page-local acquisition logic with component composition.
- `MarketMafioso.Dashboard/Pages/Settings.razor`  
  Replace basic diagnostics table with reusable diagnostics components.
- `MarketMafioso.Dashboard/Services/DashboardApiClient.cs`  
  Return typed API results for dashboard-facing failure cases where useful.
- `MarketMafioso.Dashboard/Models/DashboardModels.cs`  
  Add attempt/event view fields only if missing from server projections.
- `MarketMafioso.Dashboard/wwwroot/css/app.css`  
  Add dense grid, drawer, field, and status polish.

Test and verify:

- `dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug`
- `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal` when server API projections change.
- Browser smoke against deployed dev only after commit/push/deploy: login, item lookup, add to queue, stage queue, live update, row action, diagnostics open.

---

### Task 1: Add Dashboard UI Models And Status Service

**Files:**
- Create: `MarketMafioso.Dashboard/Models/DashboardUiModels.cs`
- Create: `MarketMafioso.Dashboard/Services/DashboardStatusService.cs`
- Modify: `MarketMafioso.Dashboard/Program.cs`
- Create: `MarketMafioso.Dashboard/Components/Status/DashboardStatusHost.razor`

- [ ] **Step 1: Create client-only UI models**

Create `MarketMafioso.Dashboard/Models/DashboardUiModels.cs`:

```csharp
namespace MarketMafioso.Dashboard.Models;

public sealed record DashboardNotice
{
    public string Message { get; init; } = string.Empty;
    public bool IsError { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record QueuedAcquisitionItem
{
    public XivItemSearchResult Item { get; init; } = new();
    public string QuantityMode { get; init; } = "AllBelowThreshold";
    public uint Quantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
    public uint MaxUnitPrice { get; init; }
    public uint MaxTotalGil { get; init; }
    public string WorldMode { get; init; } = "Recommended";

    public string QuantityDisplay => QuantityMode == "AllBelowThreshold"
        ? Quantity == 0 ? "All safe stock" : $"Max {Quantity:N0}"
        : Quantity.ToString("N0");

    public string GilCapDisplay => MaxTotalGil == 0
        ? "No cap"
        : MaxTotalGil.ToString("N0");
}

public sealed record RequestActionResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;

    public static RequestActionResult Ok(string message) => new()
    {
        Succeeded = true,
        Message = message,
    };

    public static RequestActionResult Fail(string message) => new()
    {
        Succeeded = false,
        Message = message,
    };
}
```

- [ ] **Step 2: Create dashboard status service**

Create `MarketMafioso.Dashboard/Services/DashboardStatusService.cs`:

```csharp
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
```

- [ ] **Step 3: Register status service**

Modify `MarketMafioso.Dashboard/Program.cs` and add:

```csharp
builder.Services.AddScoped<DashboardStatusService>();
```

Place it next to the existing `DashboardApiClient` registration.

- [ ] **Step 4: Render status consistently**

Create `MarketMafioso.Dashboard/Components/Status/DashboardStatusHost.razor`:

```razor
@implements IDisposable
@inject DashboardStatusService Status

@if (Status.Current is not null)
{
    <MudAlert Class="status-alert"
              Severity="@(Status.Current.IsError ? Severity.Error : Severity.Info)"
              Dense="true">
        @Status.Current.Message
    </MudAlert>
}

@code {
    protected override void OnInitialized()
    {
        Status.Changed += OnStatusChanged;
    }

    private void OnStatusChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Status.Changed -= OnStatusChanged;
    }
}
```

- [ ] **Step 5: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 6: Commit**

```powershell
git add MarketMafioso.Dashboard/Models/DashboardUiModels.cs MarketMafioso.Dashboard/Services/DashboardStatusService.cs MarketMafioso.Dashboard/Components/Status/DashboardStatusHost.razor MarketMafioso.Dashboard/Program.cs
git commit -m "refactor: add dashboard status service"
```

---

### Task 2: Add Acquisition Dashboard State Service

**Files:**
- Create: `MarketMafioso.Dashboard/Services/AcquisitionDashboardState.cs`
- Modify: `MarketMafioso.Dashboard/Program.cs`

- [ ] **Step 1: Create acquisition state service**

Create `MarketMafioso.Dashboard/Services/AcquisitionDashboardState.cs`:

```csharp
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

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            Requests = await api.GetAcquisitionRequestsAsync(cancellationToken);
            ReconcileSelectedRequest();
        }, "Refresh failed.");
    }

    public void ApplySnapshot(IReadOnlyList<MarketAcquisitionRequestView> requests)
    {
        Requests = requests;
        ReconcileSelectedRequest();
        Changed?.Invoke();
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
            await RefreshAsync(cancellationToken);
            status.Info("Request cancelled.");
        }, "Cancel failed.");
    }

    public async Task ResendAsync(string requestId, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await api.ResendAcquisitionRequestAsync(requestId, cancellationToken);
            await RefreshAsync(cancellationToken);
            status.Info("Request resent.");
        }, "Resend failed.");
    }

    public async Task StageAsync(
        IEnumerable<MarketAcquisitionCreateRequest> requests,
        CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var staged = 0;
            foreach (var request in requests)
            {
                await api.CreateAcquisitionRequestAsync(request, cancellationToken);
                staged++;
            }

            await RefreshAsync(cancellationToken);
            status.Info($"Staged {staged:N0} acquisition request(s).");
        }, "Stage queue failed.");
    }

    private async Task RunAsync(Func<Task> action, string failurePrefix)
    {
        try
        {
            IsBusy = true;
            Changed?.Invoke();
            await action();
        }
        catch (Exception ex)
        {
            status.Error($"{failurePrefix} {ex.Message}");
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
}
```

- [ ] **Step 2: Register acquisition state**

Modify `MarketMafioso.Dashboard/Program.cs` and add:

```csharp
builder.Services.AddScoped<AcquisitionDashboardState>();
```

- [ ] **Step 3: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 4: Commit**

```powershell
git add MarketMafioso.Dashboard/Services/AcquisitionDashboardState.cs MarketMafioso.Dashboard/Program.cs
git commit -m "refactor: centralize acquisition dashboard state"
```

---

### Task 3: Split The Acquisition Builder Into A Component

**Files:**
- Create: `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
- Modify: `MarketMafioso.Dashboard/Pages/Home.razor`

- [ ] **Step 1: Create request builder component**

Create `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor` using the existing builder markup from `Home.razor`. The component must expose this callback:

```razor
@using MarketMafioso.Dashboard.Models
@inject DashboardApiClient Api
@inject DashboardStatusService Status

<MudPaper Class="compact-card dashboard-panel builder-panel" Elevation="0">
    <MudStack Spacing="2">
        <MudStack Row="true" AlignItems="AlignItems.Center" Class="panel-heading">
            <MudText Typo="Typo.h6">New Purchase Request</MudText>
            <MudSpacer />
            <MudText Typo="Typo.caption" Class="muted">@_targetCharacter @@ @_targetWorld</MudText>
        </MudStack>

        <div class="form-section">
            <MudText Typo="Typo.overline" Class="section-label">Target</MudText>
            <MudGrid Spacing="2">
                <MudItem xs="12" sm="6">
                    <MudTextField @bind-Value="_targetCharacter" Label="Character" Variant="Variant.Outlined" Margin="Margin.Dense" />
                </MudItem>
                <MudItem xs="12" sm="6">
                    <MudTextField @bind-Value="_targetWorld" Label="Home world" Variant="Variant.Outlined" Margin="Margin.Dense" />
                </MudItem>
                <MudItem xs="12" sm="6">
                    <MudSelect T="string" @bind-Value="_region" Label="Region" Variant="Variant.Outlined" Margin="Margin.Dense">
                        <MudSelectItem Value="@("North America")">North America</MudSelectItem>
                    </MudSelect>
                </MudItem>
                <MudItem xs="12" sm="6">
                    <MudSelect T="string" @bind-Value="_worldMode" Label="Routing" Variant="Variant.Outlined" Margin="Margin.Dense">
                        <MudSelectItem Value="@("Recommended")">Recommended worlds</MudSelectItem>
                        <MudSelectItem Value="@("CurrentWorld")">Current world</MudSelectItem>
                        <MudSelectItem Value="@("AllWorldSweep")">All-world sweep</MudSelectItem>
                    </MudSelect>
                </MudItem>
            </MudGrid>
        </div>

        <div class="form-section">
            <MudText Typo="Typo.overline" Class="section-label">Item</MudText>
            <MudAutocomplete T="XivItemSearchResult"
                             Label="Item"
                             Variant="Variant.Outlined"
                             Margin="Margin.Dense"
                             Value="_selectedItem"
                             ValueChanged="OnItemSelected"
                             Text="_itemSearchText"
                             TextChanged="OnItemSearchTextChanged"
                             SearchFunc="SearchItemsAsync"
                             ToStringFunc="item => item?.ToString() ?? string.Empty"
                             ResetValueOnEmptyText="true"
                             CoerceText="false"
                             Clearable="true"
                             Dense="true" />
        </div>

        <div class="form-section">
            <MudText Typo="Typo.overline" Class="section-label">Purchase Limits</MudText>
            <MudGrid Spacing="2">
                <MudItem xs="12" sm="6">
                    <MudSelect T="string" Value="_quantityMode" ValueChanged="QuantityModeChanged" Label="Quantity mode" Variant="Variant.Outlined" Margin="Margin.Dense">
                        <MudSelectItem Value="@("TargetQuantity")">Target quantity</MudSelectItem>
                        <MudSelectItem Value="@("AllBelowThreshold")">All below threshold</MudSelectItem>
                    </MudSelect>
                </MudItem>
                <MudItem xs="12" sm="6">
                    <MudNumericField T="uint?" @bind-Value="_quantity" Label="@QuantityLabel" Variant="Variant.Outlined" Margin="Margin.Dense" Min="0" />
                </MudItem>
                <MudItem xs="12" sm="6">
                    <MudNumericField T="uint?" @bind-Value="_maxUnitPrice" Label="Max unit price" Variant="Variant.Outlined" Margin="Margin.Dense" Min="1" />
                </MudItem>
                <MudItem xs="12" sm="6">
                    <MudNumericField T="uint?" @bind-Value="_gilCap" Label="Gil cap (optional)" Variant="Variant.Outlined" Margin="Margin.Dense" Min="0" />
                </MudItem>
                <MudItem xs="12" sm="6">
                    <MudSelect T="string" @bind-Value="_hqPolicy" Label="HQ policy" Variant="Variant.Outlined" Margin="Margin.Dense">
                        <MudSelectItem Value="@("Either")">Either</MudSelectItem>
                        <MudSelectItem Value="@("NqOnly")">NQ only</MudSelectItem>
                        <MudSelectItem Value="@("HqOnly")">HQ only</MudSelectItem>
                    </MudSelect>
                </MudItem>
                <MudItem xs="12" sm="6">
                    <MudSelect T="int" @bind-Value="_expiresInSeconds" Label="Pickup expires" Variant="Variant.Outlined" Margin="Margin.Dense">
                        <MudSelectItem Value="300">5 minutes</MudSelectItem>
                        <MudSelectItem Value="900">15 minutes</MudSelectItem>
                    </MudSelect>
                </MudItem>
            </MudGrid>
        </div>

        <MudAlert Class="help-alert" Severity="Severity.Info" Dense="true">
            @QuantityHelp
        </MudAlert>

        <MudStack Row="true" Class="action-row">
            <MudButton Variant="Variant.Outlined" Class="quiet-button" OnClick="ClearBuilder">Clear</MudButton>
            <MudSpacer />
            <MudButton Variant="Variant.Outlined" Class="quiet-button" OnClick="AddToQueue">Add to Queue</MudButton>
            <MudButton Variant="Variant.Filled" Color="Color.Primary" Disabled="Busy || _queue.Count == 0" OnClick="StageAsync">Stage Queue</MudButton>
        </MudStack>

        <MudTable Items="_queue" Dense="true" Hover="true" Class="queue-table">
            <HeaderContent>
                <MudTh>Item</MudTh>
                <MudTh>Mode</MudTh>
                <MudTh>Max Unit</MudTh>
                <MudTh>Cap</MudTh>
                <MudTh></MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd>@context.Item.Name<br /><span class="muted">Item @context.Item.ItemId</span></MudTd>
                <MudTd>@context.QuantityMode<br /><span class="muted">@context.QuantityDisplay</span></MudTd>
                <MudTd>@context.MaxUnitPrice.ToString("N0")</MudTd>
                <MudTd>@context.GilCapDisplay</MudTd>
                <MudTd><MudButton Size="Size.Small" OnClick="@(() => RemoveQueued(context))">Remove</MudButton></MudTd>
            </RowTemplate>
            <NoRecordsContent>
                <MudText Class="muted">No queued items.</MudText>
            </NoRecordsContent>
        </MudTable>
    </MudStack>
</MudPaper>

@code {
    [Parameter] public bool Busy { get; set; }
    [Parameter] public EventCallback<IReadOnlyList<MarketAcquisitionCreateRequest>> StageRequested { get; set; }

    private string _targetCharacter = "Wei Ning";
    private string _targetWorld = "Siren";
    private string _region = "North America";
    private string _worldMode = "Recommended";
    private XivItemSearchResult? _selectedItem;
    private string _itemSearchText = string.Empty;
    private string _quantityMode = "AllBelowThreshold";
    private uint? _quantity;
    private uint? _maxUnitPrice;
    private uint? _gilCap;
    private string _hqPolicy = "Either";
    private int _expiresInSeconds = 300;
    private readonly List<QueuedAcquisitionItem> _queue = [];

    private string QuantityLabel => _quantityMode == "AllBelowThreshold" ? "Max quantity (optional)" : "Target quantity";

    private string QuantityHelp => _quantityMode == "AllBelowThreshold"
        ? "All below threshold buys every confirmed live listing at or below max unit price. Max quantity and gil cap are optional hard stops."
        : "Target quantity buys safe whole stacks until the target is satisfied; harmless overage can happen because stacks are indivisible.";

    private async Task<IEnumerable<XivItemSearchResult>> SearchItemsAsync(string value, CancellationToken token)
    {
        try
        {
            return await Api.SearchItemsAsync(value, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            Status.Error($"Item lookup failed: {ex.Message}");
            return [];
        }
    }

    private void OnItemSelected(XivItemSearchResult? item)
    {
        _selectedItem = item;
        if (item is not null)
            _itemSearchText = item.ToString();
    }

    private void OnItemSearchTextChanged(string value)
    {
        _itemSearchText = value;
        if (string.IsNullOrWhiteSpace(value))
            _selectedItem = null;
    }

    private void QuantityModeChanged(string mode)
    {
        _quantityMode = mode;
        if (mode == "AllBelowThreshold" && _quantity == 0)
            _quantity = null;
    }

    private void AddToQueue()
    {
        if (_selectedItem is null)
        {
            Status.Error("Pick an item before adding to the queue.");
            return;
        }

        if (_maxUnitPrice is null or 0)
        {
            Status.Error("Max unit price is required.");
            return;
        }

        if (_quantityMode == "TargetQuantity" && (_quantity is null or 0))
        {
            Status.Error("Target quantity is required for TargetQuantity mode.");
            return;
        }

        _queue.Add(new QueuedAcquisitionItem
        {
            Item = _selectedItem,
            QuantityMode = _quantityMode,
            Quantity = _quantity ?? 0,
            HqPolicy = _hqPolicy,
            MaxUnitPrice = _maxUnitPrice.Value,
            MaxTotalGil = _gilCap ?? 0,
            WorldMode = _worldMode,
        });
        Status.Info($"Queued {_selectedItem.Name}.");
    }

    private async Task StageAsync()
    {
        var requests = _queue.Select(ToCreateRequest).ToArray();
        await StageRequested.InvokeAsync(requests);
        _queue.Clear();
    }

    private MarketAcquisitionCreateRequest ToCreateRequest(QueuedAcquisitionItem item) => new()
    {
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        TargetCharacterName = _targetCharacter.Trim(),
        TargetWorld = _targetWorld.Trim(),
        Region = _region,
        ItemId = item.Item.ItemId,
        ItemName = item.Item.Name,
        QuantityMode = item.QuantityMode,
        Quantity = item.Quantity,
        HqPolicy = item.HqPolicy,
        MaxUnitPrice = item.MaxUnitPrice,
        MaxTotalGil = item.MaxTotalGil,
        WorldMode = item.WorldMode,
        ExpiresInSeconds = _expiresInSeconds,
    };

    private void RemoveQueued(QueuedAcquisitionItem item)
    {
        _queue.Remove(item);
    }

    private void ClearBuilder()
    {
        _selectedItem = null;
        _itemSearchText = string.Empty;
        _quantity = null;
        _maxUnitPrice = null;
        _gilCap = null;
        Status.Info("Builder cleared.");
    }
}
```

- [ ] **Step 2: Replace builder markup in Home**

In `MarketMafioso.Dashboard/Pages/Home.razor`, replace the entire left `MudPaper` builder block with:

```razor
<RequestBuilder Busy="State.IsBusy" StageRequested="StageRequestsAsync" />
```

Add injections:

```razor
@inject AcquisitionDashboardState State
@inject DashboardStatusService Status
```

Add method:

```csharp
private async Task StageRequestsAsync(IReadOnlyList<MarketAcquisitionCreateRequest> requests)
{
    await State.StageAsync(requests);
}
```

- [ ] **Step 3: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 4: Commit**

```powershell
git add MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor MarketMafioso.Dashboard/Pages/Home.razor
git commit -m "refactor: extract acquisition request builder"
```

---

### Task 4: Replace Server Queue With A Dense Request Grid

**Files:**
- Create: `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- Modify: `MarketMafioso.Dashboard/Pages/Home.razor`
- Modify: `MarketMafioso.Dashboard/wwwroot/css/app.css`

- [ ] **Step 1: Create server request grid**

Create `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`:

```razor
@using MarketMafioso.Dashboard.Models

<MudPaper Class="compact-card dashboard-panel queue-panel" Elevation="0">
    <MudStack Spacing="2">
        <MudStack Row="true" AlignItems="AlignItems.Center" Class="panel-heading">
            <MudText Typo="Typo.h6">Request Queue</MudText>
            <MudSpacer />
            <MudText Typo="Typo.caption" Class="muted">@Requests.Count active</MudText>
            <MudButton Variant="Variant.Outlined" Class="quiet-button" OnClick="RefreshRequested">Refresh</MudButton>
        </MudStack>

        <MudTable Items="Requests"
                  Dense="true"
                  Hover="true"
                  FixedHeader="true"
                  Class="queue-table operation-grid"
                  RowClassFunc="RowClass"
                  OnRowClick="OnRowClicked">
            <HeaderContent>
                <MudTh>Item</MudTh>
                <MudTh>Qty</MudTh>
                <MudTh>Max Unit</MudTh>
                <MudTh>Routing</MudTh>
                <MudTh>Status</MudTh>
                <MudTh>Latest</MudTh>
                <MudTh></MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd>@(context.ItemName ?? $"Item {context.ItemId}")<br /><span class="muted">@context.TargetCharacterName @@ @context.TargetWorld</span></MudTd>
                <MudTd>@FormatQuantity(context)</MudTd>
                <MudTd>@context.MaxUnitPrice.ToString("N0")</MudTd>
                <MudTd>@context.WorldMode</MudTd>
                <MudTd><MudChip T="string" Size="Size.Small" Color="@StatusColor(context.Status)">@context.Status</MudChip></MudTd>
                <MudTd>@FormatLatestRequestEvent(context)</MudTd>
                <MudTd>
                    <MudMenu Dense="true">
                        <ActivatorContent>
                            <MudButton Size="Size.Small" Variant="Variant.Outlined">Actions</MudButton>
                        </ActivatorContent>
                        <ChildContent>
                            <MudMenuItem OnClick="@(() => CancelRequested.InvokeAsync(context.Id))">Cancel</MudMenuItem>
                            <MudMenuItem OnClick="@(() => ResendRequested.InvokeAsync(context.Id))">Resend</MudMenuItem>
                        </ChildContent>
                    </MudMenu>
                </MudTd>
            </RowTemplate>
            <NoRecordsContent>
                <MudText Class="muted">No acquisition requests yet.</MudText>
            </NoRecordsContent>
        </MudTable>
    </MudStack>
</MudPaper>

@code {
    [Parameter] public IReadOnlyList<MarketAcquisitionRequestView> Requests { get; set; } = [];
    [Parameter] public MarketAcquisitionRequestView? SelectedRequest { get; set; }
    [Parameter] public EventCallback<MarketAcquisitionRequestView> SelectedRequestChanged { get; set; }
    [Parameter] public EventCallback RefreshRequested { get; set; }
    [Parameter] public EventCallback<string> CancelRequested { get; set; }
    [Parameter] public EventCallback<string> ResendRequested { get; set; }

    private async Task OnRowClicked(TableRowClickEventArgs<MarketAcquisitionRequestView> args)
    {
        await SelectedRequestChanged.InvokeAsync(args.Item);
    }

    private string RowClass(MarketAcquisitionRequestView request, int rowNumber) =>
        SelectedRequest?.Id == request.Id ? "selected-grid-row" : string.Empty;

    private static Color StatusColor(string status) => status switch
    {
        "Complete" => Color.Success,
        "Failed" or "Cancelled" or "Rejected" => Color.Error,
        "Running" or "AcceptedInPlugin" => Color.Warning,
        _ => Color.Info,
    };

    private static string FormatQuantity(MarketAcquisitionRequestView request) =>
        request.QuantityMode == "AllBelowThreshold"
            ? request.Quantity == 0 ? "All safe stock" : $"Max {request.Quantity:N0}"
            : request.Quantity.ToString("N0");

    private static string FormatLatestRequestEvent(MarketAcquisitionRequestView request)
    {
        if (!string.IsNullOrWhiteSpace(request.LatestAttemptId))
        {
            var world = string.IsNullOrWhiteSpace(request.LatestAttemptWorld)
                ? "route"
                : request.LatestAttemptWorld;
            var phase = string.IsNullOrWhiteSpace(request.LatestAttemptPhase)
                ? request.LatestAttemptEventType ?? "event"
                : request.LatestAttemptPhase;
            var result = string.IsNullOrWhiteSpace(request.LatestAttemptResult)
                ? string.Empty
                : $" / {request.LatestAttemptResult}";
            return $"{world}: {phase}{result}";
        }

        return request.LatestMessage ?? request.LatestReason ?? "-";
    }
}
```

- [ ] **Step 2: Compose grid from Home**

In `Home.razor`, replace the queue `MudPaper` with:

```razor
<ServerRequestGrid Requests="State.Requests"
                   SelectedRequest="State.SelectedRequest"
                   SelectedRequestChanged="State.SelectRequest"
                   RefreshRequested="State.RefreshAsync"
                   CancelRequested="State.CancelAsync"
                   ResendRequested="State.ResendAsync" />
```

- [ ] **Step 3: Add selected row polish**

Append to `MarketMafioso.Dashboard/wwwroot/css/app.css`:

```css
.operation-grid .mud-table-container {
    min-height: 420px;
}

.selected-grid-row .mud-table-cell {
    background: #14273a !important;
}

.operation-grid .mud-table-cell {
    border-right: 1px solid #253646;
}

.operation-grid .mud-table-cell:last-child {
    border-right: 0;
}
```

- [ ] **Step 4: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor MarketMafioso.Dashboard/Pages/Home.razor MarketMafioso.Dashboard/wwwroot/css/app.css
git commit -m "refactor: extract acquisition request grid"
```

---

### Task 5: Add Request Details Drawer

**Files:**
- Create: `MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`
- Modify: `MarketMafioso.Dashboard/Pages/Home.razor`
- Modify: `MarketMafioso.Dashboard/wwwroot/css/app.css`

- [ ] **Step 1: Create request details drawer**

Create `MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`:

```razor
@using MarketMafioso.Dashboard.Models

<MudDrawer Anchor="Anchor.Right"
           Open="Request is not null"
           OpenChanged="OnOpenChanged"
           Variant="DrawerVariant.Temporary"
           Width="420px"
           Class="details-drawer">
    @if (Request is null)
    {
        <MudText Class="muted">Select a request to inspect it.</MudText>
    }
    else
    {
        <MudStack Spacing="2" Class="details-drawer-content">
            <MudStack Row="true" AlignItems="AlignItems.Center">
                <MudText Typo="Typo.h6">@(Request.ItemName ?? $"Item {Request.ItemId}")</MudText>
                <MudSpacer />
                <MudIconButton Icon="@Icons.Material.Filled.Close" OnClick="CloseAsync" />
            </MudStack>

            <MudChip T="string" Size="Size.Small" Color="@StatusColor(Request.Status)">@Request.Status</MudChip>

            <MudSimpleTable Dense="true" Class="details-table">
                <tbody>
                    <tr><td>Request</td><td>@Request.Id</td></tr>
                    <tr><td>Target</td><td>@Request.TargetCharacterName @@ @Request.TargetWorld</td></tr>
                    <tr><td>Mode</td><td>@Request.QuantityMode</td></tr>
                    <tr><td>Quantity</td><td>@FormatQuantity(Request)</td></tr>
                    <tr><td>Max unit</td><td>@Request.MaxUnitPrice.ToString("N0") gil</td></tr>
                    <tr><td>Gil cap</td><td>@(Request.MaxTotalGil == 0 ? "No cap" : $"{Request.MaxTotalGil:N0} gil")</td></tr>
                    <tr><td>Route</td><td>@Request.WorldMode</td></tr>
                    <tr><td>Latest event</td><td>@FormatLatestRequestEvent(Request)</td></tr>
                    <tr><td>Runner state</td><td>@(Request.LatestRunnerState ?? "-")</td></tr>
                    <tr><td>Attempt</td><td>@(Request.LatestAttemptId ?? "-")</td></tr>
                    <tr><td>Plugin version</td><td>@(Request.LatestAttemptPluginVersion ?? "-")</td></tr>
                </tbody>
            </MudSimpleTable>

            <MudStack Row="true">
                <MudButton Variant="Variant.Outlined" OnClick="@(() => CancelRequested.InvokeAsync(Request.Id))">Cancel</MudButton>
                <MudButton Variant="Variant.Filled" Color="Color.Primary" OnClick="@(() => ResendRequested.InvokeAsync(Request.Id))">Resend</MudButton>
            </MudStack>
        </MudStack>
    }
</MudDrawer>

@code {
    [Parameter] public MarketAcquisitionRequestView? Request { get; set; }
    [Parameter] public EventCallback Closed { get; set; }
    [Parameter] public EventCallback<string> CancelRequested { get; set; }
    [Parameter] public EventCallback<string> ResendRequested { get; set; }

    private Task OnOpenChanged(bool open) => open ? Task.CompletedTask : CloseAsync();

    private async Task CloseAsync()
    {
        await Closed.InvokeAsync();
    }

    private static Color StatusColor(string status) => status switch
    {
        "Complete" => Color.Success,
        "Failed" or "Cancelled" or "Rejected" => Color.Error,
        "Running" or "AcceptedInPlugin" => Color.Warning,
        _ => Color.Info,
    };

    private static string FormatQuantity(MarketAcquisitionRequestView request) =>
        request.QuantityMode == "AllBelowThreshold"
            ? request.Quantity == 0 ? "All safe stock" : $"Max {request.Quantity:N0}"
            : request.Quantity.ToString("N0");

    private static string FormatLatestRequestEvent(MarketAcquisitionRequestView request)
    {
        if (!string.IsNullOrWhiteSpace(request.LatestAttemptId))
        {
            var world = string.IsNullOrWhiteSpace(request.LatestAttemptWorld)
                ? "route"
                : request.LatestAttemptWorld;
            var phase = string.IsNullOrWhiteSpace(request.LatestAttemptPhase)
                ? request.LatestAttemptEventType ?? "event"
                : request.LatestAttemptPhase;
            var result = string.IsNullOrWhiteSpace(request.LatestAttemptResult)
                ? string.Empty
                : $" / {request.LatestAttemptResult}";
            return $"{world}: {phase}{result}";
        }

        return request.LatestMessage ?? request.LatestReason ?? "-";
    }
}
```

- [ ] **Step 2: Add drawer to Home**

In `Home.razor`, render this after the page grid:

```razor
<RequestDetailsDrawer Request="State.SelectedRequest"
                      Closed="@(() => State.SelectRequest(null))"
                      CancelRequested="State.CancelAsync"
                      ResendRequested="State.ResendAsync" />
```

- [ ] **Step 3: Add drawer CSS**

Append to `MarketMafioso.Dashboard/wwwroot/css/app.css`:

```css
.details-drawer {
    background: #0d141b !important;
    border-left: 1px solid #304357;
    color: #f4f8ff;
}

.details-drawer-content {
    padding: 16px;
}

.details-table td:first-child {
    color: #9eb4cc;
    width: 120px;
}

.details-table td {
    border-bottom: 1px solid #223141;
    padding: 6px 4px;
}
```

- [ ] **Step 4: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor MarketMafioso.Dashboard/Pages/Home.razor MarketMafioso.Dashboard/wwwroot/css/app.css
git commit -m "feat: add acquisition request details drawer"
```

---

### Task 6: Wire Live SSE Into State And Status Strip

**Files:**
- Create: `MarketMafioso.Dashboard/Components/Status/LiveStatusStrip.razor`
- Modify: `MarketMafioso.Dashboard/Pages/Home.razor`

- [ ] **Step 1: Create live status strip**

Create `MarketMafioso.Dashboard/Components/Status/LiveStatusStrip.razor`:

```razor
<MudStack Row="true" AlignItems="AlignItems.Center" Class="live-status-strip">
    <MudChip T="string" Class="status-chip" Color="@(Connected ? Color.Success : Color.Info)" Variant="Variant.Outlined">
        @StatusText
    </MudChip>
    <MudText Typo="Typo.caption" Class="muted">@RequestCount active request(s)</MudText>
    <MudSpacer />
    <MudButton Variant="Variant.Outlined" Class="quiet-button" OnClick="RefreshRequested">Refresh</MudButton>
</MudStack>

@code {
    [Parameter] public bool Connected { get; set; }
    [Parameter] public string StatusText { get; set; } = "Live updates idle";
    [Parameter] public int RequestCount { get; set; }
    [Parameter] public EventCallback RefreshRequested { get; set; }
}
```

- [ ] **Step 2: Update Home to use state snapshots**

In `Home.razor`, update `OnAcquisitionEventAsync`:

```csharp
[JSInvokable]
public Task OnAcquisitionEventAsync(string json)
{
    var requests = System.Text.Json.JsonSerializer.Deserialize<IReadOnlyList<MarketAcquisitionRequestView>>(
        json,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    if (requests is not null)
        State.ApplySnapshot(requests);

    return InvokeAsync(StateHasChanged);
}
```

Replace the top-right chip/refresh area with:

```razor
<LiveStatusStrip Connected="_eventsConnected"
                 StatusText="_liveStatus"
                 RequestCount="State.Requests.Count"
                 RefreshRequested="State.RefreshAsync" />
```

- [ ] **Step 3: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 4: Commit**

```powershell
git add MarketMafioso.Dashboard/Components/Status/LiveStatusStrip.razor MarketMafioso.Dashboard/Pages/Home.razor
git commit -m "refactor: route acquisition SSE through dashboard state"
```

---

### Task 7: Upgrade Settings Diagnostics

**Files:**
- Create: `MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticsEventGrid.razor`
- Create: `MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticEventDrawer.razor`
- Modify: `MarketMafioso.Dashboard/Pages/Settings.razor`
- Modify: `MarketMafioso.Dashboard/wwwroot/css/app.css`

- [ ] **Step 1: Create diagnostics grid**

Create `MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticsEventGrid.razor`:

```razor
@using MarketMafioso.Dashboard.Models

<MudStack Spacing="2">
    <MudStack Row="true">
        <MudTextField @bind-Value="_filter" Placeholder="Filter category, severity, request, message" Variant="Variant.Outlined" Margin="Margin.Dense" Immediate="true" Class="diagnostics-filter" />
        <MudSpacer />
        <MudButton Variant="Variant.Outlined" OnClick="RefreshRequested">Refresh</MudButton>
    </MudStack>

    <MudTable Items="FilteredEvents" Dense="true" Hover="true" FixedHeader="true" Class="queue-table operation-grid diagnostics-grid" OnRowClick="OnRowClicked">
        <HeaderContent>
            <MudTh>ID</MudTh>
            <MudTh>When</MudTh>
            <MudTh>Category</MudTh>
            <MudTh>Severity</MudTh>
            <MudTh>Message</MudTh>
            <MudTh>Correlation</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Id</MudTd>
            <MudTd>@context.OccurredAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")</MudTd>
            <MudTd>@context.Category<br /><span class="muted">@context.Type</span></MudTd>
            <MudTd><MudChip T="string" Size="Size.Small" Color="@SeverityColor(context.Severity)">@context.Severity</MudChip></MudTd>
            <MudTd>@context.Message</MudTd>
            <MudTd>@(context.CorrelationId ?? context.AcquisitionRequestId ?? "-")</MudTd>
        </RowTemplate>
        <NoRecordsContent>
            <MudText Class="muted">No diagnostics match this filter.</MudText>
        </NoRecordsContent>
    </MudTable>
</MudStack>

@code {
    [Parameter] public IReadOnlyList<DiagnosticEventView> Events { get; set; } = [];
    [Parameter] public EventCallback RefreshRequested { get; set; }
    [Parameter] public EventCallback<DiagnosticEventView> SelectedEventChanged { get; set; }

    private string _filter = string.Empty;

    private IEnumerable<DiagnosticEventView> FilteredEvents => string.IsNullOrWhiteSpace(_filter)
        ? Events
        : Events.Where(MatchesFilter);

    private bool MatchesFilter(DiagnosticEventView item)
    {
        var filter = _filter.Trim();
        return Contains(item.Category, filter) ||
               Contains(item.Type, filter) ||
               Contains(item.Severity, filter) ||
               Contains(item.Message, filter) ||
               Contains(item.CorrelationId, filter) ||
               Contains(item.AcquisitionRequestId, filter);
    }

    private async Task OnRowClicked(TableRowClickEventArgs<DiagnosticEventView> args)
    {
        await SelectedEventChanged.InvokeAsync(args.Item);
    }

    private static bool Contains(string? value, string filter) =>
        value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

    private static Color SeverityColor(string severity) => severity switch
    {
        "Critical" or "Error" => Color.Error,
        "Warn" => Color.Warning,
        "Debug" or "Trace" => Color.Default,
        _ => Color.Info,
    };
}
```

- [ ] **Step 2: Create diagnostics drawer**

Create `MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticEventDrawer.razor`:

```razor
@using MarketMafioso.Dashboard.Models

<MudDrawer Anchor="Anchor.Right"
           Open="Event is not null"
           OpenChanged="OnOpenChanged"
           Variant="DrawerVariant.Temporary"
           Width="480px"
           Class="details-drawer">
    @if (Event is not null)
    {
        <MudStack Spacing="2" Class="details-drawer-content">
            <MudStack Row="true" AlignItems="AlignItems.Center">
                <MudText Typo="Typo.h6">Diagnostic @Event.Id</MudText>
                <MudSpacer />
                <MudIconButton Icon="@Icons.Material.Filled.Close" OnClick="CloseAsync" />
            </MudStack>

            <MudSimpleTable Dense="true" Class="details-table">
                <tbody>
                    <tr><td>When</td><td>@Event.OccurredAtUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")</td></tr>
                    <tr><td>Source</td><td>@Event.Source</td></tr>
                    <tr><td>Category</td><td>@Event.Category</td></tr>
                    <tr><td>Type</td><td>@Event.Type</td></tr>
                    <tr><td>Severity</td><td>@Event.Severity</td></tr>
                    <tr><td>Outcome</td><td>@(Event.Outcome ?? "-")</td></tr>
                    <tr><td>Correlation</td><td>@(Event.CorrelationId ?? "-")</td></tr>
                    <tr><td>Request</td><td>@(Event.AcquisitionRequestId ?? "-")</td></tr>
                    <tr><td>Message</td><td>@Event.Message</td></tr>
                </tbody>
            </MudSimpleTable>

            <MudText Typo="Typo.subtitle2">Payload Summary</MudText>
            <MudPaper Class="payload-summary" Elevation="0">
                <pre>@(Event.PayloadSummaryJson ?? "No payload summary.")</pre>
            </MudPaper>
        </MudStack>
    }
</MudDrawer>

@code {
    [Parameter] public DiagnosticEventView? Event { get; set; }
    [Parameter] public EventCallback Closed { get; set; }

    private Task OnOpenChanged(bool open) => open ? Task.CompletedTask : CloseAsync();

    private async Task CloseAsync()
    {
        await Closed.InvokeAsync();
    }
}
```

- [ ] **Step 3: Replace Settings diagnostics table**

In `Settings.razor`, replace the existing diagnostics table body with:

```razor
<DiagnosticsEventGrid Events="_events"
                      RefreshRequested="LoadDiagnosticsAsync"
                      SelectedEventChanged="SelectDiagnosticEvent" />
<DiagnosticEventDrawer Event="_selectedEvent"
                       Closed="@(() => _selectedEvent = null)" />
```

Add field and method:

```csharp
private DiagnosticEventView? _selectedEvent;

private void SelectDiagnosticEvent(DiagnosticEventView item)
{
    _selectedEvent = item;
}
```

- [ ] **Step 4: Add diagnostics CSS**

Append to `app.css`:

```css
.diagnostics-filter {
    max-width: 520px;
}

.diagnostics-grid .mud-table-container {
    max-height: calc(100vh - 260px);
}

.payload-summary {
    background: #091017 !important;
    border: 1px solid #223141;
    border-radius: 6px;
    color: #d8e9ff;
    overflow: auto;
    padding: 10px;
}

.payload-summary pre {
    font-family: Consolas, "Cascadia Mono", monospace;
    font-size: 12px;
    margin: 0;
    white-space: pre-wrap;
}
```

- [ ] **Step 5: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 6: Commit**

```powershell
git add MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticsEventGrid.razor MarketMafioso.Dashboard/Components/Diagnostics/DiagnosticEventDrawer.razor MarketMafioso.Dashboard/Pages/Settings.razor MarketMafioso.Dashboard/wwwroot/css/app.css
git commit -m "feat: add dashboard diagnostics drilldown"
```

---

### Task 8: Final Home Page Simplification

**Files:**
- Modify: `MarketMafioso.Dashboard/Pages/Home.razor`

- [ ] **Step 1: Reduce Home to orchestration**

After Tasks 1-7, `Home.razor` should primarily contain:

```razor
@page "/"
@page "/acquisition"
@inject DashboardApiClient Api
@inject AcquisitionDashboardState State
@inject DashboardStatusService Status
@inject IJSRuntime JS
@implements IAsyncDisposable

<PageTitle>MarketMafioso Acquisition</PageTitle>

@if (_loading)
{
    <MudProgressLinear Indeterminate="true" Color="Color.Primary" />
}
else if (_session is null)
{
    <MudPaper Class="login-shell compact-card dashboard-panel" Elevation="0">
        <MudStack Spacing="2">
            <MudText Typo="Typo.h5">Sign in</MudText>
            <MudText Typo="Typo.body2" Class="muted">Use the dashboard account configured for this receiver.</MudText>
            <MudTextField @bind-Value="_loginUsername" Label="Username" Variant="Variant.Outlined" Margin="Margin.Dense" />
            <MudTextField @bind-Value="_loginPassword" Label="Password" InputType="InputType.Password" Variant="Variant.Outlined" Margin="Margin.Dense" />
            <MudButton Variant="Variant.Filled" Color="Color.Primary" Disabled="State.IsBusy" OnClick="LoginAsync">Login</MudButton>
            <DashboardStatusHost />
        </MudStack>
    </MudPaper>
}
else
{
    <MudStack Spacing="2" Class="dashboard-page acquisition-page">
        <MudStack Row="true" AlignItems="AlignItems.Center" Class="page-title-row">
            <div>
                <MudText Typo="Typo.h5">Acquisition</MudText>
                <MudText Typo="Typo.body2" Class="muted">Stage purchase intent here; the plugin validates live market rows before buying.</MudText>
            </div>
            <MudSpacer />
            <MudButton Variant="Variant.Outlined" Class="quiet-button" OnClick="LogoutAsync">Logout @_session.User.Username</MudButton>
        </MudStack>

        <DashboardStatusHost />
        <LiveStatusStrip Connected="_eventsConnected"
                         StatusText="_liveStatus"
                         RequestCount="State.Requests.Count"
                         RefreshRequested="State.RefreshAsync" />

        <div class="page-grid acquisition-grid">
            <RequestBuilder Busy="State.IsBusy" StageRequested="StageRequestsAsync" />
            <ServerRequestGrid Requests="State.Requests"
                               SelectedRequest="State.SelectedRequest"
                               SelectedRequestChanged="State.SelectRequest"
                               RefreshRequested="State.RefreshAsync"
                               CancelRequested="State.CancelAsync"
                               ResendRequested="State.ResendAsync" />
        </div>

        <RequestDetailsDrawer Request="State.SelectedRequest"
                              Closed="@(() => State.SelectRequest(null))"
                              CancelRequested="State.CancelAsync"
                              ResendRequested="State.ResendAsync" />
    </MudStack>
}
```

Keep the existing login/session/SSE methods, but remove builder-specific fields and methods from `Home.razor`.

- [ ] **Step 2: Build dashboard**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 3: Commit**

```powershell
git add MarketMafioso.Dashboard/Pages/Home.razor
git commit -m "refactor: simplify acquisition page composition"
```

---

### Task 9: Verification And Dev Deploy

**Files:**
- No required source edits.

- [ ] **Step 1: Run focused dashboard build**

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with `0 Error(s)`.

- [ ] **Step 2: Run server-focused tests if API projections changed**

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal
```

Expected: all selected tests pass.

- [ ] **Step 3: Run format verification**

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: no formatting changes are required.

- [ ] **Step 4: Commit any verification-only doc/CSS cleanup**

If verification required small cleanup:

```powershell
git add MarketMafioso.Dashboard docs/superpowers/plans/2026-06-27-dashboard-hardening-polish.md
git commit -m "chore: polish dashboard hardening pass"
```

- [ ] **Step 5: Push and deploy dev server**

Only after all intended dashboard/server changes are committed:

```powershell
git push origin local-dev
```

Then run the repo's dev server deploy path. If `MarketMafioso/tools/Deploy-ServerDev.ps1` is still the active deploy helper:

```powershell
MarketMafioso/tools/Deploy-ServerDev.ps1
```

Expected: deploy succeeds against the pushed `local-dev` ref.

- [ ] **Step 6: Browser smoke test on dev**

Use `https://dev.xivcraftarchitect.com/api/marketmafioso/` and verify:

1. Login succeeds.
2. Item lookup accepts typing without deleting search text.
3. Add to Queue adds a local item.
4. Stage Queue creates a server request.
5. SSE updates the request queue without manual refresh.
6. Clicking a request opens details drawer.
7. Cancel and resend actions update the row.
8. Settings > Diagnostics loads events, filters rows, and opens event drawer.

---

## Self-Review

- Spec coverage: This plan covers the approved second-pass goals: component split, state service, typed status handling, request details, diagnostics drilldown, SSE-driven state, and UI polish. It does not implement inventory browser polish or snapshot management; those belong to a later dashboard pass because acquisition is the testing blocker.
- Placeholder scan: No task uses placeholder language. Each implementation task names exact files and provides concrete code blocks or exact commands.
- Type consistency: `AcquisitionDashboardState`, `DashboardStatusService`, `QueuedAcquisitionItem`, `RequestBuilder`, `ServerRequestGrid`, `RequestDetailsDrawer`, `DiagnosticsEventGrid`, and `DiagnosticEventDrawer` are introduced before they are referenced by later tasks.
