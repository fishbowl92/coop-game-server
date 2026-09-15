namespace CoopGameServer.Contracts.Players;

/// <summary>외부 API에 반환하는 플레이어 인벤토리 항목입니다.</summary>
public sealed record PlayerInventoryItemResponse(int ItemId, int Quantity);
