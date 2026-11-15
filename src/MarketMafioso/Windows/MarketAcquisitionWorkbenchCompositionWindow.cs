using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.UI.Windows;
using MarketMafioso.Windows.MarketAcquisitionPanels;

namespace MarketMafioso.Windows;

public sealed class MarketAcquisitionWorkbenchCompositionWindow : Window, IDisposable
{
    private const float MinimumWidth = 760f;
    private const float MinimumHeight = 440f;
    private readonly MarketAcquisitionWorkbenchCompositionPanel panel;
    private readonly Func<MarketAcquisitionWorkbenchCompositionContext> createContext;
    private readonly CompanionWindowActivationState activation = new();
    private Vector2 anchorPosition;
    private Vector2 anchorSize;
    private Vector2 windowSize = new(MinimumWidth, MinimumHeight);
    private DalamudOwnedWindowSurface? ownerSurface;
    private bool hasAnchor;

    public MarketAcquisitionWorkbenchCompositionWindow(
        MarketAcquisitionWorkbenchCompositionPanel panel,
        Func<MarketAcquisitionWorkbenchCompositionContext> createContext)
        : base("Compositions##MarketMafiosoAcquisitionCompositions")
    {
        this.panel = panel ?? throw new ArgumentNullException(nameof(panel));
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(MinimumWidth, MinimumHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Opaque: the window often sits over the Workbench, and its controls must not ghost through.
        BgAlpha = 1f;

        // A collapsed picker is just a stray title strip; closing it is the only way to put it away.
        Flags |= ImGuiWindowFlags.NoCollapse;
    }

    public int Count => panel.Count;

    public void ToggleOpen() => IsOpen = activation.Toggle(IsOpen);

    public override void OnOpen() => panel.OnOpened();

    public void AnchorTo(Vector2 position, Vector2 size, DalamudOwnedWindowSurface owner)
    {
        anchorPosition = position;
        anchorSize = size;
        ownerSurface = owner;
        hasAnchor = size.X > 0f && size.Y > 0f;
    }

    public override void PreDraw()
    {
        if (activation.ConsumeFocusRequest())
            ImGui.SetNextWindowFocus();

        if (!hasAnchor)
            return;

        // Free-floating: placed relative to the main window each time it opens, then movable.
        const float gap = 8f;
        var viewport = ImGui.GetMainViewport();
        var width = Math.Max(MinimumWidth, windowSize.X);
        var height = Math.Max(MinimumHeight, windowSize.Y);
        var placement = CompanionWindowPlacement.Calculate(
            anchorPosition,
            anchorSize,
            new Vector2(width, height),
            viewport.WorkPos,
            viewport.WorkSize,
            gap);
        var position = placement.Position;
        if (placement.Kind == CompanionWindowPlacementKind.Overlay)
        {
            // No room beside the main window: float over the middle of the Workbench instead of
            // sitting on the main window's title bar.
            var workMax = viewport.WorkPos + viewport.WorkSize;
            var centered = anchorPosition + ((anchorSize - new Vector2(width, height)) * 0.5f);
            position = new Vector2(
                Math.Clamp(centered.X, viewport.WorkPos.X, Math.Max(viewport.WorkPos.X, workMax.X - width)),
                Math.Clamp(centered.Y, viewport.WorkPos.Y, Math.Max(viewport.WorkPos.Y, workMax.Y - height)));
        }

        ImGui.SetNextWindowPos(position, ImGuiCond.Appearing);
    }

    public override void Draw()
    {
        ownerSurface?.KeepCurrentWindowAboveOwner();
        windowSize = ImGui.GetWindowSize();
        panel.Draw(createContext());
    }

    public void Dispose()
    {
    }
}
