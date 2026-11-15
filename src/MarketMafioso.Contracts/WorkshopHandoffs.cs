namespace MarketMafioso.Contracts.Workshop;

public sealed record WorkshopCharacter(string CharacterName, string World);
public sealed record WorkshopHandoffItem(uint WorkshopItemId, int Quantity);
public sealed record WorkshopHandoffOffer(Guid SourceJobId, string JobName, WorkshopCharacter Sender,
    WorkshopCharacter Target, IReadOnlyList<WorkshopHandoffItem> Items);
public sealed record WorkshopHandoffCommand(WorkshopCharacter Actor, DateTimeOffset? ExpectedUpdatedAt = null);
public sealed record WorkshopHandoffDto(Guid HandoffId, Guid SourceJobId, string JobName,
    WorkshopCharacter Sender, WorkshopCharacter Target, IReadOnlyList<WorkshopHandoffItem> Items,
    string State, DateTimeOffset UpdatedAt);
public sealed record WorkshopHandoffList(IReadOnlyList<WorkshopCharacter> KnownAssemblers,
    IReadOnlyList<WorkshopHandoffDto> Outgoing, IReadOnlyList<WorkshopHandoffDto> Incoming);
