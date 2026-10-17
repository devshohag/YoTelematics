using YoTelematics.Domain.Common;

namespace YoTelematics.Domain.Entities;

public class CommissionRate : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DealerUserId { get; set; }
    public string AssetCategory { get; set; } = string.Empty;
    public decimal CommissionPercent { get; set; }
    public DateTimeOffset ValidFromUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidToUtc { get; set; }

    public User? DealerUser { get; set; }
}

public class LedgerAccount : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string AccountType { get; set; } = string.Empty;
    public string Currency { get; set; } = "BDT";

    public User? OwnerUser { get; set; }
}

public class LedgerTransaction : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? BkashTrxId { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Asset? ReferenceAsset { get; set; }
    public ICollection<LedgerEntry> Entries { get; set; } = new List<LedgerEntry>();
}

public class LedgerEntry : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid TransactionId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal? CommissionRateSnapshot { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public LedgerTransaction? Transaction { get; set; }
    public LedgerAccount? Account { get; set; }
}
