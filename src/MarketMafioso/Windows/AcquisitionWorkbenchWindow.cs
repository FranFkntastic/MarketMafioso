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

public sealed class AcquisitionWorkbenchWindow : Window
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
    private WorkbenchPane activePane = WorkbenchPane.Build;

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly string[] QuantityModes = ["TargetQuantity", "AllBelowThreshold"];
    private static readonly string[] HqPolicies = ["Either", "HqOnly", "NqOnly"];

    public AcquisitionWorkbenchWindow(
        Configuration config,
        IDataManager dataManager,
        Func<MarketAcquisitionQuickShopScope> getScope,
        Func<bool> isRouteActive,
        Func<bool> isBusy,
        Func<string> getStatus,
        Func<MarketAcquisitionQuickShopDraft, Task<bool>> createRoute)
        : base("Acquisition Workbench##MarketAcquisitionWorkbench", ImGuiWindowFlags.None)
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
            MinimumSize = new Vector2(900, 560),
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
        DrawBody(scope, validation);
        ImGui.Separator();
        DrawPhaseStrip();
    }

    private void DrawHeader(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        ImGui.TextColored(ColHeader, "Acquisition Workbench");
        ImGui.TextWrapped("Build, sync, and monitor client-created acquisition routes from one popout.");
        ImGui.Spacing();

        if (!ImGui.BeginTable("AcquisitionWorkbenchHeader", 5, ImGuiTableFlags.SizingStretchSame))
            return;

        DrawMetric("Target", scope.HasScope ? $"{scope.CharacterName} @ {scope.World}" : "Unavailable", scope.HasScope);
        DrawMetric("Route", FormatRouteMode(draft), true);
        DrawMetric("Lines", draft.Lines.Count.ToString("N0"), draft.Lines.Count > 0);
        DrawMetric("Ready", validation.IsValid ? "Yes" : "Needs input", validation.IsValid);
        DrawMetric("Sync", isBusy() ? "Working" : "Idle", !isBusy());
        ImGui.EndTable();

        var status = getStatus();
        if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextColored(ColMuted, status);
    }

    private void DrawBody(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        var available = ImGui.GetContentRegionAvail();
        var phaseStripHeight = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().FramePadding.Y * 2f + ImGui.GetStyle().ItemSpacing.Y * 2f;
        var bodyHeight = MathF.Max(300f, available.Y - phaseStripHeight);
        var stack = available.X < 760f;

        if (stack)
        {
            DrawPanel("Draft", DrawDraftBuilder, Math.Clamp(bodyHeight * 0.50f, 260f, 420f));
            ImGui.Spacing();
            DrawPanel("Route", () => DrawMainPane(scope, validation), Math.Clamp(bodyHeight * 0.32f, 190f, 320f));
            ImGui.Spacing();
            DrawPanel("Details", () => DrawSidePane(validation), MathF.Max(160f, ImGui.GetContentRegionAvail().Y));
            return;
        }

        if (!ImGui.BeginTable("AcquisitionWorkbenchBody", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Draft", ImGuiTableColumnFlags.WidthFixed, Math.Clamp(available.X * 0.32f, 320f, 460f));
        ImGui.TableSetupColumn("Route", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthFixed, Math.Clamp(available.X * 0.24f, 260f, 380f));
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawPanel("Draft", DrawDraftBuilder, bodyHeight);
        ImGui.TableNextColumn();
        DrawPanel("Route", () => DrawMainPane(scope, validation), bodyHeight);
        ImGui.TableNextColumn();
        DrawPanel("Details", () => DrawSidePane(validation), bodyHeight);
        ImGui.EndTable();
    }

    private static void DrawPanel(string title, Action drawContent, float height)
    {
        ImGui.BeginChild($"##AcquisitionWorkbench{title}", new Vector2(0, height), true);
        ImGui.TextColored(ColHeader, title);
        ImGui.Separator();
        drawContent();
        ImGui.EndChild();
    }

    private void DrawDraftBuilder()
    {
        RouteScopeSelector.Draw(
            "workbench",
            AcquisitionRouteScope.FromDraft(draft),
            ApplyRouteScope,
            ColMuted,
            ColError);

        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Line");
        ImGui.Separator();
        ItemAutocompleteControl.Draw(
            "workbench",
            itemOptions,
            itemAutocomplete,
            null,
            ColMuted,
            ColSuccess,
            ColError);
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

    private void DrawMainPane(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        switch (activePane)
        {
            case WorkbenchPane.Build:
                DrawQueuedLines();
                break;
            case WorkbenchPane.Appraise:
                DrawPlaceholder("No craft or stock evidence is loaded for the selected draft line.");
                break;
            case WorkbenchPane.Run:
                DrawPlaceholder(isRouteActive()
                    ? "A guided route is active. Route controls are still shown in the Market Acquisition tab."
                    : "No prepared route is active.");
                break;
            case WorkbenchPane.Recover:
                DrawPlaceholder("No stopped route is available for recovery.");
                break;
        }

        ImGui.Spacing();
        DrawSubmit(scope, validation);
    }

    private void DrawSidePane(MarketAcquisitionQuickShopValidationResult validation)
    {
        ImGui.TextColored(ColHeader, "Sync");
        ImGui.Separator();
        if (validation.IsValid)
        {
            ImGui.TextColored(ColSuccess, "Draft can be synced as a monitored route.");
        }
        else
        {
            foreach (var error in validation.Errors.Take(5))
                ImGui.TextColored(ColError, error);
            if (validation.Errors.Count > 5)
                ImGui.TextColored(ColError, $"{validation.Errors.Count - 5:N0} more issue(s).");
        }

        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Market Board Read");
        ImGui.Separator();
        ImGui.TextColored(ColMuted, "No live market-board read is active.");
    }

    private void DrawQueuedLines()
    {
        if (draft.Lines.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No acquisition lines queued.");
            return;
        }

        var tableHeight = MathF.Max(160f, ImGui.GetContentRegionAvail().Y - 92f);
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("AcquisitionWorkbenchQueuedLines", 6, flags, new Vector2(0, tableHeight)))
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
            if (ImGui.SmallButton($"Duplicate##workbenchDuplicate{index}"))
                DuplicateLine(index);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##workbenchRemove{index}"))
                RemoveLine(index);
        }

        ImGui.EndTable();
    }

    private void DrawSubmit(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        if (isRouteActive())
        {
            ImGui.TextColored(ColMuted, "A guided route is active. Finish or stop it before syncing a new draft.");
        }
        else if (!scope.HasScope)
        {
            var message = scope.IsTemporarilyUnavailable
                ? "Character scope is temporarily unavailable during route travel."
                : "Log into a character before syncing a route.";
            ImGui.TextColored(ColError, message);
        }
        else if (validation.IsValid)
        {
            ImGui.TextColored(ColSuccess, "Ready to sync, claim, and accept automatically.");
        }

        if (ImGuiUi.Button("Sync Route", !isBusy() && !isRouteActive() && validation.IsValid))
            _ = SubmitAsync();
        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Draft", HasDraftInput))
            ClearDraft();
    }

    private void DrawPhaseStrip()
    {
        DrawPhaseButton(WorkbenchPane.Build, "Build");
        ImGui.SameLine();
        DrawPhaseButton(WorkbenchPane.Appraise, "Appraise");
        ImGui.SameLine();
        DrawPhaseButton(WorkbenchPane.Run, "Run");
        ImGui.SameLine();
        DrawPhaseButton(WorkbenchPane.Recover, "Recover");
    }

    private void DrawPhaseButton(WorkbenchPane pane, string label)
    {
        var active = activePane == pane;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, ColHeader);
        if (ImGui.Button($"{label}##workbenchPane{pane}"))
            activePane = pane;
        if (active)
            ImGui.PopStyleColor();
    }

    private static void DrawPlaceholder(string message)
    {
        ImGui.TextColored(ColMuted, message);
    }

    private async Task SubmitAsync()
    {
        var submittedDraft = draft;
        if (await createRoute(submittedDraft).ConfigureAwait(false))
            ClearDraft();
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
        ImGui.InputText($"##workbench{label}", ref value, 128);
    }

    private static void DrawIndexedCombo(string label, IReadOnlyList<string> options, ref int index)
    {
        ImGui.TextColored(ColMuted, label);
        var current = options[Math.Clamp(index, 0, options.Count - 1)];
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##workbench{label}", current))
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

    private enum WorkbenchPane
    {
        Build,
        Appraise,
        Run,
        Recover,
    }
}
