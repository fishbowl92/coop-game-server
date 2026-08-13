using CoopGameServer.Domain.Inventories;

namespace CoopGameServer.UnitTests.Domain.Inventories;

/// <summary>
/// 인벤토리 아이템의 식별자와 수량 규칙을 검증합니다.
/// </summary>
public sealed class InventoryItemTests
{
    [Fact]
    public void ConstructorCreatesInventoryItemWithPositiveQuantity()
    {
        var playerId = Guid.Parse("f152ec53-5b7c-4525-aeec-980a09a1b8e8");
        var createdAt = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);

        var inventoryItem = new InventoryItem(playerId, 1001, 3, createdAt);

        Assert.Equal(playerId, inventoryItem.PlayerId);
        Assert.Equal(1001, inventoryItem.ItemId);
        Assert.Equal(3, inventoryItem.Quantity);
        Assert.Equal(createdAt, inventoryItem.UpdatedAt);
    }

    [Fact]
    public void AddQuantityIncreasesQuantityAndUpdatesTimestamp()
    {
        var createdAt = new DateTimeOffset(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(3);
        var inventoryItem = new InventoryItem(Guid.NewGuid(), 1001, 3, createdAt);

        inventoryItem.AddQuantity(2, updatedAt);

        Assert.Equal(5, inventoryItem.Quantity);
        Assert.Equal(updatedAt, inventoryItem.UpdatedAt);
    }

    [Fact]
    public void ConstructorRejectsZeroQuantity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new InventoryItem(Guid.NewGuid(), 1001, 0, DateTimeOffset.UtcNow));
    }
}
