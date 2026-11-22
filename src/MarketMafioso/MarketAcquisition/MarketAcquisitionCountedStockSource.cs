using System;

namespace MarketMafioso.MarketAcquisition;

/// <summary>
/// Stock on one source (a character's bags or retainers) that a plan line counted toward its target
/// when the plan was finalized. Container is "bags" or "retainers".
///
public sealed record MarketAcquisitionCountedStockSource(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName,
    string Container,
    uint Quantity,
    DateTimeOffset ObservedAtUtc);
