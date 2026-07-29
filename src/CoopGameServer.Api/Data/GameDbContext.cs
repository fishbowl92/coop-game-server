using CoopGameServer.Api.Domain.Players;
using Microsoft.EntityFrameworkCore;

namespace CoopGameServer.Api.Data;

/// <summary>
/// 게임 서버가 PostgreSQL 데이터베이스와 통신할 때 사용하는 EF Core의 작업 단위입니다.
/// </summary>
/// <remarks>
/// DbContext는 C# 객체와 데이터베이스 테이블 사이의 연결 정보를 보관합니다.
/// 이 클래스에는 어떤 엔티티가 어떤 테이블과 열에 저장되는지를 명시합니다.
/// </remarks>
public sealed class GameDbContext : DbContext
{
    /// <summary>
    /// ASP.NET Core의 의존성 주입 컨테이너가 데이터베이스 연결 옵션을 전달해 생성합니다.
    /// </summary>
    /// <param name="options">PostgreSQL 연결 정보와 EF Core 동작 설정입니다.</param>
    public GameDbContext(DbContextOptions<GameDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// players 테이블에 대응하는 플레이어 집합입니다.
    /// LINQ 질의와 추가·수정·삭제의 시작점으로 사용합니다.
    /// </summary>
    public DbSet<Player> Players => Set<Player>();

    /// <summary>
    /// C# Player 객체와 PostgreSQL players 테이블 사이의 세부 규칙을 정의합니다.
    /// </summary>
    /// <param name="modelBuilder">엔티티·테이블 매핑을 구성하는 EF Core 도구입니다.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var player = modelBuilder.Entity<Player>();

        // C# 클래스 이름과 무관하게 DB에서는 소문자 복수형 players 테이블을 사용합니다.
        player.ToTable("players");

        // player_id는 각 행을 고유하게 식별하는 기본 키(Primary Key, PK)입니다.
        player.HasKey(entity => entity.Id);
        player.Property(entity => entity.Id)
            .HasColumnName("player_id")
            .ValueGeneratedNever();

        // 닉네임은 필수이며, 도메인 규칙과 같은 최대 길이 20자를 DB에도 적용합니다.
        player.Property(entity => entity.Nickname)
            .HasColumnName("nickname")
            .HasMaxLength(Player.MaxNicknameLength)
            .IsRequired();

        // 동시 접속 중 같은 닉네임이 중복 생성되지 않도록 DB에서도 유일성을 보장합니다.
        player.HasIndex(entity => entity.Nickname)
            .IsUnique();

        // 모든 시간은 UTC(Coordinated Universal Time, 협정 세계시) 기준으로 저장합니다.
        player.Property(entity => entity.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        player.Property(entity => entity.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
    }
}
