using System;
using System.Collections.Generic;

namespace MarketMafioso.AgentBridge;

public sealed record AgentBridgeRenderedUiSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<AgentBridgeRenderedAddonSnapshot> Addons);

public sealed record AgentBridgeRenderedAddonSnapshot(
    string Name,
    bool Present,
    bool Ready,
    bool Visible,
    uint NodeCount,
    IReadOnlyList<AgentBridgeRenderedTextNode> TextNodes,
    string? Diagnostic = null);

public sealed record AgentBridgeRenderedTextNode(
    string NodePath,
    uint NodeId,
    ushort NodeType,
    string Text,
    float X,
    float Y,
    ushort Width,
    ushort Height);
