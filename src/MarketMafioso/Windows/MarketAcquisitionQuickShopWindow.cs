using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.AcquisitionWorkbench;

namespace MarketMafioso.Windows;

public sealed class MarketAcquisitionQuickShopWindow : Window
{
    private readonly Configuration config;
    private readonly Func<MarketAcquisitionQuickShopScope> getScope;
    private readonly Func<bool> isRouteActive;
    private readonly Func<bool> isBusy;
    private readonly Func<string> getStatus;
    private readonly Func<MarketAcquisitionQuickShopDraft, Task<bool>> createRoute;
    private readonly IReadOnlyList<AcquisitionItemOption> itemOptions;

    private MarketAcquisitionQuickShopDraft draft = MarketAcquisitionQuickShopDraft.CreateDefault();
    private readonly ItemAutocompleteState itemAutocomplete = new();
    private string targetQuantityBuffer = string.Empty;
    private string maxQuantityBuffer = string.Empty;
    private string maxUnitPriceBuffer = string.Empty;
    private string gilCapBuffer = string.Empty;
    private int quantityModeIndex = 1;
    private int hqPolicyIndex;

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly string[] QuantityModes = ["TargetQuantity", "AllBelowThreshold"];
    private static readonly string[] HqPolicies = ["Either", "HqOnly", "NqOnly"];

    public MarketAcquisitionQuickShopWindow(
        Configuration config,
        IDataManager dataManager,
        Func<MarketAcquisitionQuickShopScope> getScope,
        Func<bool> isRouteActive,
        Func<bool> isBusy,
        Func<string> getStatus,
        Func<MarketAcquisitionQuickShopDraft, Task<bool>> createRoute)
        : base("Quick Shop Route Planner##MarketAcquisitionQuickShop", ImGuiWindowFlags.None)
    {
        this.config = config;
        this.getScope = getScope;
        this.isRouteActive = isRouteActive;
        this.isBusy = isBusy;
        this.getStatus = getStatus;
        this.createRoute = createRoute;
        itemOptions = ItemAutocompleteControl.LoadItemOptions(dataManager);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public int DraftLineCount => draft.Lines.Count;
    public bool HasDraftInput => DraftLineCount > 0 || HasLineInput();

    public override void Draw()
    {
        var scope = getScope();
        var validation = MarketAcquisitionQuickShopDraftValidator.Validate(
            draft,
            config.ApiKey,
            scope.CharacterName,
            scope.World);

        DrawHeader(scope, validation);
        ImGui.Separator();

        if (isRouteActive())
        {
            ImGui.TextColored(ColMuted, "Quick shop route creation is unavailable while a guided route is active.");
            return;
        }

        DrawBody(scope, validation);
    }

    private void DrawHeader(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        ImGui.TextColored(ColHeader, "Quick Shop Route Planner");
        ImGui.TextWrapped("Build a local acquisition route. The plugin creates, claims, and accepts it automatically; the dashboard receives the synced route for monitoring.");
        ImGui.Spacing();

        if (ImGui.BeginTable("QuickShopHeaderMetrics", 4, ImGuiTableFlags.SizingStretchSame))
        {
            DrawMetric("Target", scope.HasScope ? $"{scope.CharacterName} @ {scope.World}" : "Unavailable", scope.HasScope);
            DrawMetric("Route", FormatRouteMode(draft), true);
            DrawMetric("Lines", draft.Lines.Count.ToString("N0"), draft.Lines.Count > 0);
            DrawMetric("Ready", validation.IsValid ? "Yes" : "Needs input", validation.IsValid);
            ImGui.EndTable();
        }

        var status = getStatus();
        if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextColored(ColMuted, status);
    }

    private void DrawBody(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        var available = ImGui.GetContentRegionAvail();
        if (available.X < 720)
        {
            var routeHeight = Math.Clamp(available.Y * 0.22f, 116f, 172f);
            var addHeight = Math.Clamp(available.Y * 0.42f, 260f, 380f);
            var queueHeight = Math.Clamp(available.Y * 0.26f, 180f, 280f);
            DrawPanel("Route Settings", DrawRouteSettings, routeHeight);
            ImGui.Spacing();
            DrawPanel("Add Item", DrawLineEditor, addHeight);
            ImGui.Spacing();
            DrawPanel("Queued Batch", DrawQueuedLines, queueHeight);
            ImGui.Spacing();
            DrawPanel("Submit", () => DrawSubmit(scope, validation), MathF.Max(120f, ImGui.GetContentRegionAvail().Y));
            return;
        }

        var leftWidth = Math.Clamp(available.X * 0.42f, 340f, 520f);
        if (ImGui.BeginTable("QuickShopBody", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Builder", ImGuiTableColumnFlags.WidthFixed, leftWidth);
            ImGui.TableSetupColumn("Batch", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            var leftHeight = MathF.Max(360f, available.Y);
            var routeHeight = Math.Clamp(leftHeight * 0.25f, 124f, 178f);
            var addHeight = MathF.Max(272f, leftHeight - routeHeight - ImGui.GetStyle().ItemSpacing.Y);
            var submitHeight = Math.Clamp(leftHeight * 0.22f, 126f, 168f);
            var queueHeight = MathF.Max(260f, leftHeight - submitHeight - ImGui.GetStyle().ItemSpacing.Y);

            ImGui.TableNextColumn();
            DrawPanel("Route Settings", DrawRouteSettings, routeHeight);
            ImGui.Spacing();
            DrawPanel("Add Item", DrawLineEditor, addHeight);

            ImGui.TableNextColumn();
            DrawPanel("Queued Batch", DrawQueuedLines, queueHeight);
            ImGui.Spacing();
            DrawPanel("Submit", () => DrawSubmit(scope, validation), submitHeight);

            ImGui.EndTable();
        }
    }

    private static void DrawPanel(string title, Action drawContent, float height)
    {
        ImGui.BeginChild($"##{title}", new Vector2(0, height), true);
        ImGui.TextColored(ColHeader, title);
        ImGui.Separator();
        drawContent();
        ImGui.EndChild();
    }

    private void DrawRouteSettings()
    {
        RouteScopeSelector.Draw(
            "quickShop",
            AcquisitionRouteScope.FromDraft(draft),
            ApplyRouteScope,
            ColMuted,
            ColError);
    }

    private void DrawLineEditor()
    {
        DrawItemSearch();
        DrawIndexedCombo("Quantity Mode", QuantityModes, ref quantityModeIndex);

        if (QuantityModes[quantityModeIndex] == "TargetQuantity")
            DrawInput("Target Quantity", ref targetQuantityBuffer);
        else
            DrawInput("Max Quantity", ref maxQuantityBuffer);

        DrawIndexedCombo("HQ", HqPolicies, ref hqPolicyIndex);
        DrawInput("Max Unit Price", ref maxUnitPriceBuffer);
        DrawInput("Gil Cap", ref gilCapBuffer);

        ImGui.Spacing();
        if (ImGuiUi.Button("Add Line", CanAddLine()))
            AddLineFromBuffers();
        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Fields", HasLineInput()))
            ClearLineBuffers();
    }

    private void DrawQueuedLines()
    {
        if (draft.Lines.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No items queued yet.");
            return;
        }

        var tableHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y);
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("QuickShopQueuedLines", 6, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 108);
        ImGui.TableSetupColumn("Max Unit", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 138);
        ImGui.TableHeadersRow();

        for (var index = 0; index < draft.Lines.Count; index++)
        {
            var line = draft.Lines[index];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatQueuedItem(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(line.QuantityMode));
            ImGui.TableNextColumn();
            var quantity = line.QuantityMode == "TargetQuantity" ? line.TargetQuantity : line.MaxQuantity;
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatQuantity(line.QuantityMode, quantity));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(line.MaxUnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.HqPolicy);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Duplicate##quickShopDuplicate{index}"))
                DuplicateLine(index);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##quickShopRemove{index}"))
                RemoveLine(index);
        }

        ImGui.EndTable();
    }

    private void DrawSubmit(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        if (!scope.HasScope)
        {
            var message = scope.IsTemporarilyUnavailable
                ? "Character scope is temporarily unavailable during route travel."
                : "Log into a character before creating a route.";
            ImGui.TextColored(ColError, message);
        }
        else if (!validation.IsValid)
        {
            foreach (var error in validation.Errors.Take(3))
                ImGui.TextColored(ColError, error);
            if (validation.Errors.Count > 3)
                ImGui.TextColored(ColError, $"{validation.Errors.Count - 3:N0} more issue(s).");
        }
        else
        {
            ImGui.TextColored(ColSuccess, "Ready to create, claim, and accept a synced route.");
        }

        ImGui.Spacing();
        if (ImGuiUi.Button("Create, Claim & Accept", !isBusy() && validation.IsValid))
            _ = SubmitAsync();
        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Draft", HasDraftInput))
            ClearDraft();
    }

    private async Task SubmitAsync()
    {
        var submittedDraft = draft;
        if (await createRoute(submittedDraft).ConfigureAwait(false))
            ClearDraft();
    }

    private static void DrawMetric(string label, string value, bool positive)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, label);
        ImGui.TextColored(positive ? ColSuccess : ColMuted, value);
    }

    private static void DrawInput(string label, ref string value)
    {
        ImGui.TextColored(ColMuted, label);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText($"##quickShop{label}", ref value, 128);
    }

    private void DrawItemSearch()
    {
        ItemAutocompleteControl.Draw(
            "quickShop",
            itemOptions,
            itemAutocomplete,
            null,
            ColMuted,
            ColSuccess,
            ColError);
    }

    private void ApplyRouteScope(AcquisitionRouteScope scope)
    {
        draft = draft.WithNextRevision() with
        {
            Region = scope.Region,
            WorldMode = scope.WorldMode,
            SweepScope = scope.SweepScope,
            SweepDataCenters = scope.SweepDataCenters.ToList(),
        };
    }

    private static void DrawIndexedCombo(string label, IReadOnlyList<string> options, ref int index)
    {
        ImGui.TextColored(ColMuted, label);
        var current = options[Math.Clamp(index, 0, options.Count - 1)];
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##quickShop{label}", current))
            return;

        for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
        {
            var isSelected = optionIndex == index;
            if (ImGui.Selectable(options[optionIndex], isSelected))
                index = optionIndex;
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private bool CanAddLine() =>
        ResolveSelectedItem() is not null &&
        TryParseUInt(maxUnitPriceBuffer, out var maxUnitPrice) &&
        maxUnitPrice > 0 &&
        (QuantityModes[quantityModeIndex] != "TargetQuantity" ||
         TryParseUInt(targetQuantityBuffer, out var targetQuantity) && targetQuantity > 0) &&
        (string.IsNullOrWhiteSpace(gilCapBuffer) || TryParseUInt(gilCapBuffer, out _)) &&
        (string.IsNullOrWhiteSpace(maxQuantityBuffer) || TryParseUInt(maxQuantityBuffer, out _));

    private void AddLineFromBuffers()
    {
        var item = ResolveSelectedItem();
        if (item is null)
            return;

        _ = TryParseUInt(targetQuantityBuffer, out var targetQuantity);
        _ = TryParseUInt(maxQuantityBuffer, out var maxQuantity);
        _ = TryParseUInt(maxUnitPriceBuffer, out var maxUnitPrice);
        _ = TryParseUInt(gilCapBuffer, out var gilCap);

        var quantityMode = QuantityModes[quantityModeIndex];
        var lines = draft.Lines.ToList();
        lines.Add(new MarketAcquisitionQuickShopLineDraft
        {
            ItemId = item.ItemId,
            ItemName = item.Name,
            QuantityMode = quantityMode,
            TargetQuantity = quantityMode == "TargetQuantity" ? targetQuantity : 0,
            MaxQuantity = quantityMode == "AllBelowThreshold" ? maxQuantity : 0,
            HqPolicy = HqPolicies[hqPolicyIndex],
            MaxUnitPrice = maxUnitPrice,
            GilCap = gilCap,
        });

        draft = draft.WithNextRevision() with { Lines = lines };
        ClearLineBuffers();
    }

    private void DuplicateLine(int index)
    {
        if (index < 0 || index >= draft.Lines.Count)
            return;

        var lines = draft.Lines.ToList();
        lines.Insert(index + 1, lines[index]);
        draft = draft.WithNextRevision() with { Lines = lines };
    }

    private void RemoveLine(int index)
    {
        if (index < 0 || index >= draft.Lines.Count)
            return;

        var lines = draft.Lines.ToList();
        lines.RemoveAt(index);
        draft = draft.WithNextRevision() with { Lines = lines };
    }

    private void ClearDraft()
    {
        draft = MarketAcquisitionQuickShopDraft.CreateDefault();
        ClearLineBuffers();
    }

    private bool HasLineInput() =>
        !string.IsNullOrWhiteSpace(itemAutocomplete.SearchBuffer) ||
        !string.IsNullOrWhiteSpace(targetQuantityBuffer) ||
        !string.IsNullOrWhiteSpace(maxQuantityBuffer) ||
        !string.IsNullOrWhiteSpace(maxUnitPriceBuffer) ||
        !string.IsNullOrWhiteSpace(gilCapBuffer);

    private void ClearLineBuffers()
    {
        itemAutocomplete.SearchBuffer = string.Empty;
        itemAutocomplete.SelectedItem = null;
        targetQuantityBuffer = string.Empty;
        maxQuantityBuffer = string.Empty;
        maxUnitPriceBuffer = string.Empty;
        gilCapBuffer = string.Empty;
    }

    private static bool TryParseUInt(string value, out uint parsed) =>
        uint.TryParse(value?.Trim(), out parsed);

    private AcquisitionItemOption? ResolveSelectedItem() =>
        ItemAutocompletePresenter.ResolveSelectedItem(
            itemOptions,
            itemAutocomplete.SearchBuffer,
            itemAutocomplete.SelectedItem);

    private string FormatQueuedItem(MarketAcquisitionQuickShopLineDraft line)
    {
        if (string.IsNullOrWhiteSpace(line.ItemName))
            return $"Item {line.ItemId}";

        var option = itemOptions.FirstOrDefault(item => item.ItemId == line.ItemId);
        return option is null
            ? line.ItemName
            : ItemAutocompletePresenter.FormatDisplayName(itemOptions, option);
    }

    private static string FormatRouteMode(MarketAcquisitionQuickShopDraft draft)
    {
        if (draft.WorldMode != "AllWorldSweep")
            return "Recommended";

        return draft.SweepScope switch
        {
            "DataCenters" when draft.SweepDataCenters.Count > 0 => $"Sweep: {string.Join(", ", draft.SweepDataCenters)}",
            "CurrentDataCenter" => "Sweep: current DC",
            _ => "Sweep: region",
        };
    }

    private static string FormatGil(uint gil) => $"{gil:N0} gil";
}

public sealed record MarketAcquisitionQuickShopScope(
    bool HasScope,
    string CharacterName,
    string World,
    bool IsTemporarilyUnavailable);
