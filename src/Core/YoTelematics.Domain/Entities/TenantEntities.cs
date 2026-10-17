using YoTelematics.Domain.Common;

namespace YoTelematics.Domain.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public Guid? ParentTenantId { get; set; }
    public string HierarchyPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? CustomDomain { get; set; }
    public string TlsStatus { get; set; } = "None";
    public string DefaultLocale { get; set; } = "bn-BD";
    public string Timezone { get; set; } = "Asia/Dhaka";
    public string ThemeJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Tenant? ParentTenant { get; set; }
    public ICollection<Tenant> SubTenants { get; set; } = new List<Tenant>();
    public ICollection<TenantHoliday> Holidays { get; set; } = new List<TenantHoliday>();
    public ICollection<TenantProviderConfig> ProviderConfigs { get; set; } = new List<TenantProviderConfig>();
}

public class TenantHoliday : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateOnly HolidayDate { get; set; }
    public string Description { get; set; } = string.Empty;

    public Tenant? Tenant { get; set; }
}

public class TenantProviderConfig : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string ProviderType { get; set; } = string.Empty;
    public byte[] EncryptedConfigBytes { get; set; } = Array.Empty<byte>();
    public bool IsActive { get; set; } = true;

    public Tenant? Tenant { get; set; }
}
