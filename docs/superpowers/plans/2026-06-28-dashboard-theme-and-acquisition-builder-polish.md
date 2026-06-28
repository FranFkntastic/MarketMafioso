# Dashboard Theme And Acquisition Builder Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Centralize MarketMafioso dashboard colors behind a theme service, then use that foundation to flatten and polish the acquisition request builder.

**Architecture:** Add a small dashboard theming layer that owns both MudBlazor theme values and semantic CSS variables. Refactor the dashboard shell to emit those variables once, then replace hardcoded CSS colors with semantic tokens and simplify the acquisition builder markup so controls are readable without card-in-card visual noise.

**Tech Stack:** Blazor WebAssembly, MudBlazor, CSS custom properties, existing MarketMafioso dashboard services.

---

## File Structure

- Create `MarketMafioso.Dashboard/Services/DashboardThemeService.cs`
  - Owns the default warm dark MarketMafioso palette.
  - Exposes `MudTheme Theme`.
  - Exposes a CSS variable style string for root-level application.
- Modify `MarketMafioso.Dashboard/Program.cs`
  - Register `DashboardThemeService`.
- Modify `MarketMafioso.Dashboard/Layout/MainLayout.razor`
  - Inject the theme service.
  - Pass `DashboardThemeService.Theme` to `MudThemeProvider`.
  - Apply the generated CSS variables to the dashboard root layout.
  - Remove the inline hardcoded `MudTheme`.
- Modify `MarketMafioso.Dashboard/wwwroot/css/app.css`
  - Replace dashboard hardcoded colors with semantic `--mmf-*` variables.
  - Warm the palette by default via variables emitted by the service.
  - Stop compressing Mud input internals in ways that break floating-label positioning.
  - Add explicit-label input styling for the acquisition builder.
- Modify `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
  - Replace nested card-like form sections with flatter field groups.
  - Switch acquisition builder controls away from floating labels where practical.
  - Remove duplicate item labels.
  - Make the item selector occupy normal form space instead of a large isolated card.
- Optional modify `MarketMafioso.Dashboard/Pages/Home.razor`
  - Adjust left/right grid proportions only if the builder polish needs a better default width.

---

## Task 1: Add The Dashboard Theme Service

**Files:**
- Create: `MarketMafioso.Dashboard/Services/DashboardThemeService.cs`
- Modify: `MarketMafioso.Dashboard/Program.cs`
- Modify: `MarketMafioso.Dashboard/Layout/MainLayout.razor`

- [ ] **Step 1: Create the theme palette and service**

Create `MarketMafioso.Dashboard/Services/DashboardThemeService.cs`:

```csharp
using System.Globalization;
using System.Text;
using MudBlazor;

namespace MarketMafioso.Dashboard.Services;

public sealed class DashboardThemeService
{
    public DashboardThemeService()
    {
        Palette = DashboardThemePalette.WarmDark();
        Theme = BuildTheme(Palette);
        RootStyle = BuildRootStyle(Palette);
    }

    public DashboardThemePalette Palette { get; }

    public MudTheme Theme { get; }

    public string RootStyle { get; }

    private static MudTheme BuildTheme(DashboardThemePalette palette)
    {
        return new MudTheme
        {
            PaletteDark = new PaletteDark
            {
                Primary = palette.Accent,
                Secondary = palette.AccentMuted,
                Surface = palette.Surface,
                Background = palette.PageBackground,
                BackgroundGray = palette.SurfaceMuted,
                AppbarBackground = palette.AppBarBackground,
                AppbarText = palette.TextPrimary,
                DrawerBackground = palette.Surface,
                TextPrimary = palette.TextPrimary,
                TextSecondary = palette.TextMuted,
                LinesDefault = palette.Border,
                TableLines = palette.Border,
                TableStriped = palette.TableStripe,
                ActionDefault = palette.TextMuted,
                ActionDisabled = palette.Disabled,
                Error = palette.Danger,
                Info = palette.Info,
                Success = palette.Success,
                Warning = palette.Warning,
            },
        };
    }

    private static string BuildRootStyle(DashboardThemePalette palette)
    {
        var variables = new Dictionary<string, string>
        {
            ["--mmf-page-bg"] = palette.PageBackground,
            ["--mmf-app-bg"] = palette.AppBarBackground,
            ["--mmf-surface"] = palette.Surface,
            ["--mmf-surface-raised"] = palette.SurfaceRaised,
            ["--mmf-surface-muted"] = palette.SurfaceMuted,
            ["--mmf-field-bg"] = palette.FieldBackground,
            ["--mmf-table-header"] = palette.TableHeader,
            ["--mmf-table-stripe"] = palette.TableStripe,
            ["--mmf-border"] = palette.Border,
            ["--mmf-border-strong"] = palette.BorderStrong,
            ["--mmf-text"] = palette.TextPrimary,
            ["--mmf-text-muted"] = palette.TextMuted,
            ["--mmf-text-subtle"] = palette.TextSubtle,
            ["--mmf-accent"] = palette.Accent,
            ["--mmf-accent-hover"] = palette.AccentHover,
            ["--mmf-accent-muted"] = palette.AccentMuted,
            ["--mmf-info"] = palette.Info,
            ["--mmf-info-bg"] = palette.InfoBackground,
            ["--mmf-success"] = palette.Success,
            ["--mmf-warning"] = palette.Warning,
            ["--mmf-danger"] = palette.Danger,
            ["--mmf-danger-bg"] = palette.DangerBackground,
            ["--mmf-disabled"] = palette.Disabled,
        };

        var builder = new StringBuilder();
        foreach (var (name, value) in variables)
            builder.Append(CultureInfo.InvariantCulture, $"{name}: {value};");

        return builder.ToString();
    }
}

public sealed record DashboardThemePalette(
    string PageBackground,
    string AppBarBackground,
    string Surface,
    string SurfaceRaised,
    string SurfaceMuted,
    string FieldBackground,
    string TableHeader,
    string TableStripe,
    string Border,
    string BorderStrong,
    string TextPrimary,
    string TextMuted,
    string TextSubtle,
    string Accent,
    string AccentHover,
    string AccentMuted,
    string Info,
    string InfoBackground,
    string Success,
    string Warning,
    string Danger,
    string DangerBackground,
    string Disabled)
{
    public static DashboardThemePalette WarmDark()
    {
        return new DashboardThemePalette(
            PageBackground: "#0d0f10",
            AppBarBackground: "#14120f",
            Surface: "#171512",
            SurfaceRaised: "#1e1a15",
            SurfaceMuted: "#12100e",
            FieldBackground: "#0f1112",
            TableHeader: "#211d17",
            TableStripe: "#17130f",
            Border: "#3a3329",
            BorderStrong: "#5a4a35",
            TextPrimary: "#f8f1e7",
            TextMuted: "#c6b8a5",
            TextSubtle: "#8e8170",
            Accent: "#d2a247",
            AccentHover: "#e0b65e",
            AccentMuted: "#8e6734",
            Info: "#77b8d9",
            InfoBackground: "#10212a",
            Success: "#45d18a",
            Warning: "#f0c15b",
            Danger: "#ff796f",
            DangerBackground: "#331919",
            Disabled: "#6f675c");
    }
}
```

- [ ] **Step 2: Register the service**

In `MarketMafioso.Dashboard/Program.cs`, add the scoped service next to the other dashboard services:

```csharp
builder.Services.AddScoped<DashboardThemeService>();
```

- [ ] **Step 3: Use the service in the layout**

In `MarketMafioso.Dashboard/Layout/MainLayout.razor`, replace the inline `_theme` field with service injection:

```razor
@inherits LayoutComponentBase
@inject DashboardThemeService ThemeService

<MudThemeProvider Theme="ThemeService.Theme" IsDarkMode="true" />
<MudPopoverProvider />
<MudDialogProvider />
<MudSnackbarProvider />

<MudLayout Style="@ThemeService.RootStyle">
    ...
</MudLayout>
```

Remove the existing `@code` block that creates `private readonly MudTheme _theme = new()`.

- [ ] **Step 4: Verify the dashboard still builds**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with 0 errors.

---

## Task 2: Tokenize The Dashboard CSS And Warm The Default Skin

**Files:**
- Modify: `MarketMafioso.Dashboard/wwwroot/css/app.css`

- [ ] **Step 1: Replace global hardcoded dashboard colors with CSS variables**

Replace the core dashboard color declarations with variables:

```css
html,
body {
    background: var(--mmf-page-bg);
    color: var(--mmf-text);
}

.app-bar {
    background: var(--mmf-app-bg) !important;
    border-bottom: 1px solid var(--mmf-border);
    color: var(--mmf-text) !important;
}

.dashboard-panel {
    background: var(--mmf-surface) !important;
    border: 1px solid var(--mmf-border);
    border-radius: 7px;
    box-shadow: none !important;
    color: var(--mmf-text);
    overflow: hidden;
}

.muted {
    color: var(--mmf-text-muted) !important;
}

.mud-button-filled-primary {
    background: var(--mmf-accent) !important;
    border: 1px solid var(--mmf-accent-hover);
    color: #16110b !important;
}

.mud-button-filled-primary:hover {
    background: var(--mmf-accent-hover) !important;
}
```

- [ ] **Step 2: Replace table, popover, alert, and selected-row hardcoded colors**

Change the existing table/popover/alert rules to consume tokens:

```css
.mud-popover {
    background: var(--mmf-surface-raised) !important;
    border: 1px solid var(--mmf-border);
    color: var(--mmf-text);
}

.queue-table .mud-table-root,
.queue-table .mud-table-container,
.operation-grid .mud-table-container,
.inventory-grid .mud-table-container,
.listings-grid .mud-table-container,
.diagnostics-grid .mud-table-container,
.snapshots-grid .mud-table-container {
    background: var(--mmf-surface-muted) !important;
}

.queue-table .mud-table-head,
.operation-grid .mud-table-head,
.inventory-grid .mud-table-head,
.listings-grid .mud-table-head,
.diagnostics-grid .mud-table-head,
.snapshots-grid .mud-table-head {
    background: var(--mmf-table-header) !important;
}

.selected-grid-row .mud-table-cell {
    background: var(--mmf-table-stripe) !important;
}

.mud-alert-standard-info {
    background: var(--mmf-info-bg) !important;
    border-color: color-mix(in srgb, var(--mmf-info) 45%, var(--mmf-border));
}

.mud-alert-standard-error,
.mud-alert-outlined-error,
.mud-alert-filled-error {
    background: var(--mmf-danger-bg) !important;
}
```

- [ ] **Step 3: Remove input compression that causes label collisions**

Replace the global input overrides that shrink labels and input boxes with safer defaults:

```css
.mud-input-control {
    margin-top: 0;
}

.mud-input-control > .mud-input-control-input-container > div.mud-input.mud-input-outlined {
    background: var(--mmf-field-bg);
    border-radius: 5px;
    min-height: 44px;
}

.mud-input > input.mud-input-root,
.mud-input > textarea.mud-input-root,
.mud-select .mud-input-slot {
    color: var(--mmf-text) !important;
    font-size: 13px;
    line-height: 1.35;
}

.mud-input-control .mud-input-label {
    color: var(--mmf-text-muted) !important;
}

.mud-input-outlined-border {
    border-color: var(--mmf-border) !important;
}

.mud-input:hover .mud-input-outlined-border,
.mud-input.mud-input-focused .mud-input-outlined-border {
    border-color: var(--mmf-accent) !important;
}
```

- [ ] **Step 4: Run a CSS hardcoded color scan**

Run:

```powershell
rg -n "#[0-9a-fA-F]{3,8}" MarketMafioso.Dashboard/wwwroot/css/app.css
```

Expected: only intentionally local colors remain, such as pure text contrast values. Any dashboard surface, border, accent, alert, table, or field color still hardcoded should be moved to `DashboardThemePalette`.

---

## Task 3: Flatten The Acquisition Builder

**Files:**
- Modify: `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
- Modify: `MarketMafioso.Dashboard/wwwroot/css/app.css`

- [ ] **Step 1: Replace nested card sections with flat field groups**

In `RequestBuilder.razor`, change each `div class="form-section"` to `div class="builder-section"` and remove the visual card behavior from the section CSS.

Use this section shape:

```razor
<div class="builder-section">
    <MudText Typo="Typo.overline" Class="section-label">Target</MudText>
    ...
</div>
```

- [ ] **Step 2: Convert acquisition builder controls to explicit labels**

For the builder controls, remove `Label="..."` from inputs and selects where a nearby text label is better. Add `.field-label` text above each control.

Example target character control:

```razor
<MudItem xs="12">
    <MudText Typo="Typo.caption" Class="field-label">Character</MudText>
    <MudSelect T="long?"
               Value="_selectedCharacterId"
               ValueChanged="OnCharacterChanged"
               Placeholder="Choose character"
               Variant="Variant.Outlined"
               Margin="Margin.Dense"
               Disabled="@(!Characters.Any())">
        @foreach (var character in Characters)
        {
            <MudSelectItem T="long?" Value="@character.Id">@character.DisplayName</MudSelectItem>
        }
    </MudSelect>
    @if (SelectedCharacter is not null)
    {
        <MudText Typo="Typo.caption" Class="muted field-note">Home world: @SelectedCharacter.HomeWorld</MudText>
    }
</MudItem>
```

Example quantity control:

```razor
<MudItem xs="12" sm="6">
    <MudText Typo="Typo.caption" Class="field-label">@QuantityLabel</MudText>
    <MudNumericField T="uint?"
                     @bind-Value="_quantity"
                     Placeholder="@QuantityLabel"
                     Variant="Variant.Outlined"
                     Margin="Margin.Dense"
                     Min="0" />
</MudItem>
```

- [ ] **Step 3: Fix duplicate item labeling**

Replace the current item section label plus `Label="Item"` autocomplete with one explicit label:

```razor
<div class="builder-section item-section">
    <MudText Typo="Typo.caption" Class="field-label">Item</MudText>
    <MudAutocomplete T="XivItemSearchResult"
                     Placeholder="Search by item name"
                     Variant="Variant.Outlined"
                     Margin="Margin.Dense"
                     Value="_selectedItem"
                     ValueChanged="OnItemSelected"
                     Text="@_itemSearchText"
                     TextChanged="OnItemSearchTextChanged"
                     SearchFunc="SearchItemsAsync"
                     ToStringFunc="item => item?.ToString() ?? string.Empty"
                     ResetValueOnEmptyText="true"
                     CoerceText="false"
                     Clearable="true"
                     Dense="true"
                     DebounceInterval="250" />
</div>
```

- [ ] **Step 4: Add flatter builder CSS**

In `app.css`, replace `.form-section` styling with flatter section rules:

```css
.builder-section {
    border-top: 1px solid var(--mmf-border);
    padding: 14px 0 0;
}

.builder-section:first-of-type {
    border-top: 0;
    padding-top: 0;
}

.builder-section .mud-grid {
    margin-top: 2px;
}

.section-label {
    color: var(--mmf-accent);
    display: block;
    font-size: 10px;
    font-weight: 700;
    letter-spacing: .04em;
    line-height: 1;
    margin-bottom: 9px;
}

.field-label {
    color: var(--mmf-text-muted) !important;
    display: block;
    font-size: 11px;
    line-height: 1.2;
    margin: 0 0 5px 2px;
}

.builder-panel .mud-input-control {
    margin-bottom: 2px;
}

.builder-panel .mud-input > input.mud-input-root,
.builder-panel .mud-select .mud-input-slot {
    padding-left: 12px !important;
    padding-right: 12px !important;
}
```

- [ ] **Step 5: Reduce item field visual overemphasis**

Keep `.item-section` unboxed and use the same width behavior as other sections. Do not add a standalone card or unique background.

If the item autocomplete still appears overly large, constrain only its popup/list behavior, not the field itself:

```css
.item-section .mud-input-control {
    max-width: 100%;
}
```

- [ ] **Step 6: Build after markup and CSS changes**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds with 0 errors.

---

## Task 4: Verify The Polished Dashboard Behavior

**Files:**
- No planned source edits unless verification finds an issue.

- [ ] **Step 1: Run focused dashboard build**

Run:

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
```

Expected: build succeeds.

- [ ] **Step 2: Run broader solution build**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
```

Expected: build succeeds.

- [ ] **Step 3: Run formatter verification**

Run:

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: no formatting changes required.

- [ ] **Step 4: Deploy server dashboard to dev**

Run:

```powershell
& "MarketMafioso/tools/Deploy-ServerDev.ps1" -Ref main -TimeoutSeconds 900
```

Expected: GitHub Actions deploy succeeds and public smoke checks pass.

- [ ] **Step 5: Browser visual smoke**

Open:

```text
https://dev.xivcraftarchitect.com/marketmafioso/
```

Check:

- The acquisition builder has one outer panel and no bordered cards inside it.
- Field text does not overlap labels after typing in `Max quantity`, `Max unit price`, and `Gil cap`.
- The item selector is labeled once and has normal form weight.
- The color palette reads warmer than the previous blue-heavy dashboard.
- The queue table remains readable and structurally separated from the builder.
- Live updates badge and success states remain green enough to stand out.

---

## Self-Review

- Spec coverage: The plan covers theme centralization, CSS tokenization, warmer palette, no cards-in-cards, label collision fixes, duplicate item label cleanup, and field spacing.
- Placeholder scan: No `TBD`, `TODO`, or unspecified implementation steps remain.
- Type consistency: `DashboardThemeService`, `DashboardThemePalette`, `Theme`, `RootStyle`, and `WarmDark()` are consistently named across tasks.

