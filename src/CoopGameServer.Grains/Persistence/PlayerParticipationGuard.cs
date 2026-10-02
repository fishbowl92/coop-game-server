using CoopGameServer.GrainContracts.Matchmaking;
using CoopGameServer.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Grains.Persistence;

/// <summary>서로 다른 파티·대기열 Grain의 참가 변경을 동일한 PostgreSQL Player 행에서 직렬화합니다.</summary>
internal static class PlayerParticipationGuard
{
    /// <summary>호출자의 트랜잭션이 끝날 때까지 플레이어를 잠급니다. 잠금 후 참가 여부를 다시 읽어야 합니다.</summary>
    internal static async Task<bool> LockAsync(GameDbContext context, Guid playerId)
    {
        var players = await context.Players.FromSqlInterpolated($"""
            SELECT player_id, nickname, created_at, updated_at FROM players
            WHERE player_id = {playerId} FOR UPDATE
            """).AsNoTracking().ToListAsync();
        return players.Count == 1;
    }

    /// <summary>대기·게임 배정 상태의 솔로 티켓이 있으면 새 파티 참가를 거부합니다.</summary>
    internal static Task<bool> HasActiveSoloTicketAsync(GameDbContext context, Guid playerId) =>
        (from member in context.MatchQueueMembers
         join ticket in context.MatchQueueTickets on member.TicketId equals ticket.TicketId
         where member.PlayerId == playerId && ticket.EntryKind == (int)MatchQueueEntryKind.SoloPlayer &&
             (ticket.Status == (int)MatchQueueTicketStatus.Queued || ticket.Status == (int)MatchQueueTicketStatus.Matched)
         select member).AnyAsync();
}
