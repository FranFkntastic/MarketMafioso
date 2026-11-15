namespace MarketMafioso.Contracts.Workshop;

public sealed record WorkshopCharacter(string CharacterName, string World);
public sealed record WorkshopHandoffItem(uint WorkshopItemId, int Quantity);
public sealed record WorkshopMaterialQuantity(uint ItemId, int Quantity);
public sealed record WorkshopHandoffOffer(Guid SourceJobId, string JobName, WorkshopCharacter Sender,
    WorkshopCharacter Target, IReadOnlyList<WorkshopHandoffItem> Items);
public sealed record WorkshopHandoffCommand(WorkshopCharacter Actor, DateTimeOffset? ExpectedUpdatedAt = null);
public sealed record WorkshopHandoffDto(Guid HandoffId, Guid SourceJobId, string JobName,
    WorkshopCharacter Sender, WorkshopCharacter Target, IReadOnlyList<WorkshopHandoffItem> Items,
    string State, DateTimeOffset UpdatedAt, IReadOnlyList<WorkshopMaterialQuantity>? HandedOver = null,
    int HandoverTotal = 0, DateTimeOffset? HandoverUpdatedAt = null);
public sealed record WorkshopHandoffList(IReadOnlyList<WorkshopCharacter> KnownAssemblers,
    IReadOnlyList<WorkshopHandoffDto> Outgoing, IReadOnlyList<WorkshopHandoffDto> Incoming,
    IReadOnlyList<WorkshopHandoffDto>? Received = null);
public sealed record WorkshopHandoverReport(WorkshopCharacter Actor,
    IReadOnlyList<WorkshopMaterialQuantity> HandedOver, int HandoverTotal);
public sealed record WorkshopCharacterHoldings(WorkshopCharacter Character, DateTimeOffset? CapturedAt,
    IReadOnlyList<WorkshopMaterialQuantity> Items);
