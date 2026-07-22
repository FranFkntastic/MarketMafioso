using System;

namespace MarketMafioso.RetainerRestock;

// Deserialization-only migration payload retained until Quartermaster's import window closes.
[Serializable]
public sealed class RetainerRestockPlanItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int DesiredPlayerQuantity { get; set; }
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = string.Empty;
}
