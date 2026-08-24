using System.Globalization;
using System.Reflection;
using CoopGameServer.GrainContracts.GameRooms;
using CoopGameServer.GrainContracts.Players;

namespace CoopGameServer.UnitTests.GrainContracts.Players;

/// <summary>
/// API와 Silo 사이에서 사용하는 PlayerGrain 공개 계약이 의도치 않게 바뀌지 않는지 확인합니다.
/// </summary>
/// <remarks>
/// 이 테스트는 Orleans 자체를 다시 시험하는 것이 아닙니다. 배포 뒤 필드 이름을 정리하다가
/// 직렬화 Id를 바꾸거나, 게임 완료 명령에 클라이언트 지정 보상 수량을 추가하는 회귀를 막습니다.
/// </remarks>
public sealed class PlayerGrainContractTests
{
    [Fact]
    public void InterfaceUsesGuidPlayerKeyAndExpectedMethods()
    {
        // Guid 형식의 Grain 기본 키 하나를 playerId의 유일한 원본으로 사용해야 합니다.
        Assert.True(typeof(IGrainWithGuidKey).IsAssignableFrom(typeof(IPlayerGrain)));

        var methods = typeof(IPlayerGrain).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        // 현재 설계에서 허용한 세 계약만 노출하고, HTTP 요청 수명에 묶인 취소 토큰은 넣지 않습니다.
        Assert.Equal(3, methods.Length);
        Assert.DoesNotContain(
            methods.SelectMany(method => method.GetParameters()),
            parameter => parameter.ParameterType == typeof(CancellationToken));

        AssertMethod<GrantPlayerRewardCommand, PlayerRewardCommandResult>(
            nameof(IPlayerGrain.GrantAdminRewardAsync));
        AssertMethod<CompletePlayerGameCommand, PlayerRewardCommandResult>(
            nameof(IPlayerGrain.CompleteGameAsync));
        AssertMethod<GetPlayerProgressionPageQuery, PlayerProgressionPageResult>(
            nameof(IPlayerGrain.GetProgressionPageAsync));
    }

    [Fact]
    public void SerializableRecordsKeepStableFieldIds()
    {
        // Id 번호는 각 타입 안에서 독립적으로 0부터 시작하며, 배포 뒤 기존 번호를 바꾸면 안 됩니다.
        AssertSerializerIds<GrantPlayerRewardCommand>(
            (nameof(GrantPlayerRewardCommand.RequestId), 0),
            (nameof(GrantPlayerRewardCommand.GoldAmount), 1),
            (nameof(GrantPlayerRewardCommand.ItemId), 2),
            (nameof(GrantPlayerRewardCommand.ItemQuantity), 3),
            (nameof(GrantPlayerRewardCommand.Reason), 4));

        AssertSerializerIds<CompletePlayerGameCommand>(
            (nameof(CompletePlayerGameCommand.RequestId), 0),
            (nameof(CompletePlayerGameCommand.RoomId), 1),
            (nameof(CompletePlayerGameCommand.QueueKey), 2),
            (nameof(CompletePlayerGameCommand.Outcome), 3),
            (nameof(CompletePlayerGameCommand.RewardPolicyVersion), 4));

        AssertSerializerIds<PlayerRewardCommandResult>(
            (nameof(PlayerRewardCommandResult.IsReplay), 0),
            (nameof(PlayerRewardCommandResult.Status), 1),
            (nameof(PlayerRewardCommandResult.Error), 2),
            (nameof(PlayerRewardCommandResult.Receipt), 3));

        AssertSerializerIds<PlayerRewardReceipt>(
            (nameof(PlayerRewardReceipt.RewardAuditId), 0),
            (nameof(PlayerRewardReceipt.RequestId), 1),
            (nameof(PlayerRewardReceipt.PlayerId), 2),
            (nameof(PlayerRewardReceipt.GoldAmount), 3),
            (nameof(PlayerRewardReceipt.ItemId), 4),
            (nameof(PlayerRewardReceipt.ItemQuantity), 5),
            (nameof(PlayerRewardReceipt.Reason), 6),
            (nameof(PlayerRewardReceipt.CreatedAt), 7));

        AssertSerializerIds<GetPlayerProgressionPageQuery>(
            (nameof(GetPlayerProgressionPageQuery.PageSize), 0),
            (nameof(GetPlayerProgressionPageQuery.ContinuationToken), 1));

        AssertSerializerIds<PlayerProgressionPageResult>(
            (nameof(PlayerProgressionPageResult.Error), 0),
            (nameof(PlayerProgressionPageResult.Gold), 1),
            (nameof(PlayerProgressionPageResult.Items), 2),
            (nameof(PlayerProgressionPageResult.NextContinuationToken), 3));

        AssertSerializerIds<PlayerInventoryItemSnapshot>(
            (nameof(PlayerInventoryItemSnapshot.ItemId), 0),
            (nameof(PlayerInventoryItemSnapshot.Quantity), 1),
            (nameof(PlayerInventoryItemSnapshot.UpdatedAt), 2));
    }

    [Fact]
    public void EnumNumericValuesRemainStable()
    {
        AssertGenerateSerializerAttribute<GameOutcome>();
        AssertGenerateSerializerAttribute<PlayerRewardCommandStatus>();
        AssertGenerateSerializerAttribute<PlayerRewardCommandError>();
        AssertGenerateSerializerAttribute<PlayerProgressionQueryError>();

        Assert.Equal(0, (int)GameOutcome.None);
        Assert.Equal(1, (int)GameOutcome.Victory);
        Assert.Equal(2, (int)GameOutcome.Defeat);
        Assert.Equal(3, (int)GameOutcome.Cancelled);

        Assert.Equal(0, (int)PlayerRewardCommandStatus.Applied);
        Assert.Equal(1, (int)PlayerRewardCommandStatus.NoReward);
        Assert.Equal(2, (int)PlayerRewardCommandStatus.Rejected);

        Assert.Equal(0, (int)PlayerRewardCommandError.None);
        Assert.Equal(1, (int)PlayerRewardCommandError.InvalidRequest);
        Assert.Equal(2, (int)PlayerRewardCommandError.PlayerNotFound);
        Assert.Equal(3, (int)PlayerRewardCommandError.UnsupportedRewardPolicy);
        Assert.Equal(4, (int)PlayerRewardCommandError.IdempotencyConflict);

        Assert.Equal(0, (int)PlayerProgressionQueryError.None);
        Assert.Equal(1, (int)PlayerProgressionQueryError.InvalidPageSize);
        Assert.Equal(2, (int)PlayerProgressionQueryError.InvalidContinuationToken);
        Assert.Equal(3, (int)PlayerProgressionQueryError.PlayerNotFound);
    }

    [Fact]
    public void CompleteGameCommandContainsServerResultButNoClientChosenRewardAmount()
    {
        var propertyNames = typeof(CompletePlayerGameCommand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(CompletePlayerGameCommand.Outcome), propertyNames);
        Assert.Contains(nameof(CompletePlayerGameCommand.RewardPolicyVersion), propertyNames);
        Assert.DoesNotContain(nameof(GrantPlayerRewardCommand.GoldAmount), propertyNames);
        Assert.DoesNotContain(nameof(GrantPlayerRewardCommand.ItemId), propertyNames);
        Assert.DoesNotContain(nameof(GrantPlayerRewardCommand.ItemQuantity), propertyNames);
    }

    [Fact]
    public void ConstructorsPreserveValues()
    {
        var requestId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 8, 24, 12, 30, 0, TimeSpan.Zero);
        var receipt = new PlayerRewardReceipt(
            Guid.NewGuid(),
            requestId,
            playerId,
            500,
            1001,
            2,
            "contract-test",
            createdAt);
        var result = new PlayerRewardCommandResult(
            false,
            PlayerRewardCommandStatus.Applied,
            PlayerRewardCommandError.None,
            receipt);
        var items = new[] { new PlayerInventoryItemSnapshot(1001, 2, createdAt) };
        var page = new PlayerProgressionPageResult(
            PlayerProgressionQueryError.None,
            500,
            items,
            "next-page");

        Assert.Equal(requestId, result.Receipt?.RequestId);
        Assert.Equal(playerId, result.Receipt?.PlayerId);
        Assert.Equal(500, result.Receipt?.GoldAmount);
        Assert.Same(items, page.Items);
        Assert.Equal("next-page", page.NextContinuationToken);
    }

    private static void AssertMethod<TCommand, TResult>(string methodName)
    {
        var method = typeof(IPlayerGrain).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: [typeof(TCommand)],
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<TResult>), method.ReturnType);
    }

    private static void AssertSerializerIds<T>(params (string PropertyName, int Id)[] expectedProperties)
    {
        var type = typeof(T);
        AssertGenerateSerializerAttribute<T>();
        Assert.Equal(expectedProperties.Length, type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Length);

        foreach (var (propertyName, expectedId) in expectedProperties)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(property);

            var idAttribute = property.CustomAttributes.SingleOrDefault(
                attribute => attribute.AttributeType == typeof(IdAttribute));
            Assert.NotNull(idAttribute);

            var actualId = Convert.ToInt32(
                idAttribute.ConstructorArguments.Single().Value,
                CultureInfo.InvariantCulture);
            Assert.Equal(expectedId, actualId);
        }
    }

    private static void AssertGenerateSerializerAttribute<T>()
    {
        var serializerAttribute = typeof(T).CustomAttributes.SingleOrDefault(
            attribute => attribute.AttributeType == typeof(GenerateSerializerAttribute));

        Assert.NotNull(serializerAttribute);
    }
}
