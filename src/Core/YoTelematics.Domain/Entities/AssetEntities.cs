using NetTopologySuite.Geometries;
using YoTelematics.Domain.Common;

namespace YoTelematics.Domain.Entities;

public class Asset : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid? OriginDealerId { get; set; }
    public string? CurrentImei { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? IdentifierLabel { get; set; }
    public long StateVersion { get; set; } = 1;
    public DateTimeOffset? ServiceModeUntilUtc { get; set; }
    public DateTimeOffset? LastTelemetryTimestampUtc { get; set; }
    public DateTimeOffset? LastHeartbeatUtc { get; set; }
    public DateTimeOffset SubscriptionExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Branch? Branch { get; set; }
    public User? OriginDealer { get; set; }
    public InventoryDevice? CurrentDevice { get; set; }
    public VehicleProfile? VehicleProfile { get; set; }
    public PersonPetProfile? PersonPetProfile { get; set; }
    public ICollection<AssetMember> Members { get; set; } = new List<AssetMember>();
    public ICollection<WatchContact> WatchContacts { get; set; } = new List<WatchContact>();
}

public class AssetMember : ITenantScopedEntity
{
    public Guid AssetId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string MemberRole { get; set; } = string.Empty;
    public short CallPriorityOrder { get; set; } = 1;
    public bool CanControlEngine { get; set; }
    public bool CanListenAudio { get; set; }

    public Asset? Asset { get; set; }
    public User? User { get; set; }
}

public class VehicleProfile : ITenantScopedEntity
{
    public Guid AssetId { get; set; }
    public Guid TenantId { get; set; }
    public string ArmedState { get; set; } = "Disarmed";
    public bool AutoArmEnabled { get; set; } = true;
    public short AutoArmDelaySeconds { get; set; } = 600;
    public DateTimeOffset? AutoArmDueAtUtc { get; set; }
    public DateTimeOffset? GraceExpiresAtUtc { get; set; }
    public short VibrationCountWindow { get; set; }
    public DateTimeOffset? LastVibrationUtc { get; set; }
    public string RelayState { get; set; } = "Unlocked";
    public Point? ParkedLocation { get; set; }
    public string ParkedLocationQuality { get; set; } = "GpsValid";
    public float? EstimatedKmPerLiter { get; set; }
    public long OdometerMeters { get; set; }

    public Asset? Asset { get; set; }
}

public class DriverSchedule : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid? DriverUserId { get; set; }
    public short[] ActiveDays { get; set; } = Array.Empty<short>();
    public TimeOnly DutyStartLocal { get; set; }
    public TimeOnly DutyEndLocal { get; set; }

    public Asset? Asset { get; set; }
    public User? DriverUser { get; set; }
}

public class PersonPetProfile : ITenantScopedEntity
{
    public Guid AssetId { get; set; }
    public Guid TenantId { get; set; }
    public string SafetyState { get; set; } = "Normal";
    public DateTimeOffset StateChangedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? QrPublicToken { get; set; }
    public string[] HomeWifiBssids { get; set; } = Array.Empty<string>();
    public short MissingWifiScanStreak { get; set; }
    public bool IsLostModeActive { get; set; }
    public bool AudioListenConsentGiven { get; set; }
    public DateTimeOffset? AudioConsentTimestampUtc { get; set; }

    public Asset? Asset { get; set; }
}

public class WatchContact : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public string ContactName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public short SlotIndex { get; set; }
    public bool IsSosNumber { get; set; }
    public DateTimeOffset? SyncedAtUtc { get; set; }

    public Asset? Asset { get; set; }
}

public class AssetDeviceSwap : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public string? OldImei { get; set; }
    public string? NewImei { get; set; }
    public Guid SwappedByUserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset SwappedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public Asset? Asset { get; set; }
    public User? SwappedByUser { get; set; }
}
