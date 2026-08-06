using CoopGameServer.Api.Domain.Inventories;
using CoopGameServer.Api.Domain.Players;
using CoopGameServer.Api.Domain.Rewards;
using CoopGameServer.Api.Domain.Wallets;
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
    /// player_wallets 테이블에 대응하는 플레이어 지갑 집합입니다.
    /// </summary>
    public DbSet<PlayerWallet> PlayerWallets => Set<PlayerWallet>();

    /// <summary>
    /// inventories 테이블에 대응하는 플레이어별 아이템 보유 정보 집합입니다.
    /// </summary>
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    /// <summary>
    /// reward_audits 테이블에 대응하는 보상 지급 이력 집합입니다.
    /// </summary>
    public DbSet<RewardAudit> RewardAudits => Set<RewardAudit>();

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

        var playerWallet = modelBuilder.Entity<PlayerWallet>();

        // 플레이어 한 명당 지갑은 하나이므로 player_id 자체를 기본 키로 사용합니다.
        playerWallet.ToTable(
            "player_wallets",
            table => table.HasCheckConstraint("CK_player_wallets_gold_nonnegative", "gold >= 0"));
        playerWallet.HasKey(entity => entity.PlayerId);
        playerWallet.Property(entity => entity.PlayerId)
            .HasColumnName("player_id")
            .ValueGeneratedNever();
        playerWallet.Property(entity => entity.Gold)
            .HasColumnName("gold")
            .HasColumnType("bigint")
            .IsRequired();
        playerWallet.Property(entity => entity.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        playerWallet.HasOne<Player>()
            .WithOne()
            .HasForeignKey<PlayerWallet>(entity => entity.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        var inventoryItem = modelBuilder.Entity<InventoryItem>();

        // 동일 플레이어가 같은 종류의 아이템을 여러 행으로 중복 보유하지 않도록 복합 기본 키를 사용합니다.
        inventoryItem.ToTable(
            "inventories",
            table =>
            {
                table.HasCheckConstraint("CK_inventories_item_id_positive", "item_id > 0");
                table.HasCheckConstraint("CK_inventories_quantity_positive", "quantity > 0");
            });
        inventoryItem.HasKey(entity => new { entity.PlayerId, entity.ItemId });
        inventoryItem.Property(entity => entity.PlayerId)
            .HasColumnName("player_id")
            .ValueGeneratedNever();
        inventoryItem.Property(entity => entity.ItemId)
            .HasColumnName("item_id")
            .ValueGeneratedNever();
        inventoryItem.Property(entity => entity.Quantity)
            .HasColumnName("quantity")
            .IsRequired();
        inventoryItem.Property(entity => entity.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        inventoryItem.HasOne<Player>()
            .WithMany()
            .HasForeignKey(entity => entity.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        var rewardAudit = modelBuilder.Entity<RewardAudit>();

        // request_id에 UNIQUE 제약 조건을 두어 같은 멱등성 키를 DB 차원에서 한 번만 기록할 수 있게 합니다.
        rewardAudit.ToTable(
            "reward_audits",
            table =>
            {
                table.HasCheckConstraint("CK_reward_audits_gold_nonnegative", "gold_amount >= 0");
                table.HasCheckConstraint(
                    "CK_reward_audits_item_reward_shape",
                    "(item_id IS NULL AND item_quantity IS NULL) OR (item_id IS NOT NULL AND item_quantity IS NOT NULL AND item_id > 0 AND item_quantity > 0)");
                table.HasCheckConstraint(
                    "CK_reward_audits_has_reward",
                    "gold_amount > 0 OR item_id IS NOT NULL");
            });
        rewardAudit.HasKey(entity => entity.Id);
        rewardAudit.Property(entity => entity.Id)
            .HasColumnName("reward_audit_id")
            .ValueGeneratedNever();
        rewardAudit.Property(entity => entity.RequestId)
            .HasColumnName("request_id")
            .ValueGeneratedNever();
        rewardAudit.HasIndex(entity => entity.RequestId)
            .IsUnique();
        rewardAudit.Property(entity => entity.PlayerId)
            .HasColumnName("player_id")
            .IsRequired();
        rewardAudit.Property(entity => entity.GoldAmount)
            .HasColumnName("gold_amount")
            .HasColumnType("bigint")
            .IsRequired();
        rewardAudit.Property(entity => entity.ItemId)
            .HasColumnName("item_id");
        rewardAudit.Property(entity => entity.ItemQuantity)
            .HasColumnName("item_quantity");
        rewardAudit.Property(entity => entity.Reason)
            .HasColumnName("reason")
            .HasMaxLength(RewardAudit.MaxReasonLength)
            .IsRequired();
        rewardAudit.Property(entity => entity.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamp with time zone")
            .IsRequired();
        // 감사 기록은 추후 운영 추적에 필요하므로 플레이어 삭제가 자동으로 이력을 지우지 않도록 Restrict를 사용합니다.
        rewardAudit.HasOne<Player>()
            .WithMany()
            .HasForeignKey(entity => entity.PlayerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
