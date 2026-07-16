using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.UI.Windows;
using MarketMafioso.Windows.MarketAcquisitionPanels;

namespace MarketMafioso.Windows;

public sealed class MarketAcquisitionWorkbenchCompositionWindow : Window, IDisposable
{
    private readonly MarketAcquisitionWorkbenchCompositionPanel panel;
    private readonly Func<MarketAcquisitionWorkbenchCompositionContext> createContext;
    private Vector2 anchorPosition;
    private Vector2 anchorSize;
    private Vector2 windowSize = new(680, 420);
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
            MinimumSize = new Vector2(680, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public int Count => panel.Count;

    public void AnchorTo(Vector2 position, Vector2 size)
    {
        anchorPosition = position;
        anchorSize = size;
        hasAnchor = size.X > 0f && size.Y > 0f;
    }

    public override void PreDraw()
    {
        if (!hasAnchor)
            return;

        const float gap = 8f;
        var viewport = ImGui.GetMainViewport();
        var width = Math.Max(680f, windowSize.X);
        var height = Math.Max(420f, windowSize.Y);
        var placement = CompanionWindowPlacement.Calculate(
            anchorPosition,
            anchorSize,
            new Vector2(width, height),
            viewport.WorkPos,
            viewport.WorkSize,
            gap);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(placement.Position, ImGuiCond.Always);
    }

    public override void Draw()
    {
        windowSize = ImGui.GetWindowSize();
        panel.Draw(createContext());
    }

    public void Dispose()
    {
    }
}
