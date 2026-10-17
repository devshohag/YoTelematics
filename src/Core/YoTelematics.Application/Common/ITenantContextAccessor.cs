namespace YoTelematics.Application.Common;

public interface ITenantContextAccessor
{
    Guid? CurrentTenantId { get; set; }
    string? CurrentTenantPath { get; set; }
    Guid? CurrentBranchId { get; set; }
}

public sealed class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<TenantScopeHolder> Holder = new();

    public Guid? CurrentTenantId
    {
        get => Holder.Value?.TenantId;
        set => GetOrCreate().TenantId = value;
    }

    public string? CurrentTenantPath
    {
        get => Holder.Value?.TenantPath;
        set => GetOrCreate().TenantPath = value;
    }

    public Guid? CurrentBranchId
    {
        get => Holder.Value?.BranchId;
        set => GetOrCreate().BranchId = value;
    }

    private static TenantScopeHolder GetOrCreate()
    {
        return Holder.Value ??= new TenantScopeHolder();
    }

    private sealed class TenantScopeHolder
    {
        public Guid? TenantId { get; set; }
        public string? TenantPath { get; set; }
        public Guid? BranchId { get; set; }
    }
}
