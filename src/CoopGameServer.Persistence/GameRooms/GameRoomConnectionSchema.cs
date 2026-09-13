namespace CoopGameServer.Persistence.GameRooms;

/// <summary>연결 상태별 DB 불변 조건입니다. 열거형 번호 0~4는 저장 계약이므로 변경하지 않습니다.</summary>
internal static class GameRoomConnectionSchema
{
    internal const string CheckConstraint = """
        connection_generation >= 0 AND (
          (connection_status = 0 AND connection_generation = 0
            AND connection_id IS NULL AND last_seen_at IS NULL AND lease_expires_at IS NULL
            AND disconnected_at IS NULL AND reconnect_deadline IS NULL AND abandoned_at IS NULL)
          OR (connection_status = 1 AND connection_generation > 0
            AND connection_id IS NOT NULL AND connection_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND last_seen_at IS NOT NULL AND lease_expires_at IS NOT NULL AND last_seen_at < lease_expires_at
            AND disconnected_at IS NULL AND reconnect_deadline IS NULL AND abandoned_at IS NULL)
          OR (connection_status = 2 AND connection_generation > 0 AND connection_id IS NULL
            AND last_seen_at IS NOT NULL AND lease_expires_at IS NOT NULL
            AND disconnected_at IS NOT NULL AND reconnect_deadline IS NOT NULL
            AND last_seen_at <= disconnected_at AND lease_expires_at = disconnected_at
            AND disconnected_at < reconnect_deadline AND abandoned_at IS NULL)
          OR (connection_status = 3 AND connection_id IS NULL AND abandoned_at IS NOT NULL AND (
            (connection_generation = 0 AND last_seen_at IS NULL AND lease_expires_at IS NULL
              AND disconnected_at IS NULL AND reconnect_deadline IS NULL)
            OR (connection_generation > 0 AND last_seen_at IS NOT NULL AND lease_expires_at IS NOT NULL
              AND disconnected_at IS NOT NULL AND reconnect_deadline IS NOT NULL
              AND last_seen_at <= disconnected_at AND lease_expires_at = disconnected_at
              AND disconnected_at < reconnect_deadline AND reconnect_deadline <= abandoned_at)))
          OR (connection_status = 4 AND connection_id IS NULL AND lease_expires_at IS NULL)
        )
        """;
}
