using NetTopologySuite.Geometries;
using YoTelematics.Domain.Common;

namespace YoTelematics.Domain.Entities;

public class Geofence : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ZoneType { get; set; } = string.Empty;
    public Point? CenterPoint { get; set; }
    public short? SafeRadiusM { get; set; }
    public short? DangerRadiusM { get; set; }
    public short HysteresisBufferM { get; set; } = 30;
    public Polygon? PolygonBoundary { get; set; }

    public Branch? Branch { get; set; }
    public ICollection<GeofenceWifiFingerprint> WifiFingerprints { get; set; } = new List<GeofenceWifiFingerprint>();
}

public class GeofenceWifiFingerprint
{
    public Guid GeofenceId { get; set; }
    public string Bssid { get; set; } = string.Empty;
    public int SeenCount { get; set; } = 1;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

    public Geofence? Geofence { get; set; }
}

public class ChildSchedule : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid GeofenceId { get; set; }
    public short[] ActiveDays { get; set; } = Array.Empty<short>();
    public TimeOnly ArriveByLocal { get; set; }
    public TimeOnly LeaveFromLocal { get; set; }
    public DateOnly? SkipTodayOverrideDate { get; set; }

    public Asset? Asset { get; set; }
    public Geofence? Geofence { get; set; }
}

public class GeofenceHoliday : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid GeofenceId { get; set; }
    public DateOnly HolidayDate { get; set; }
    public string? Description { get; set; }

    public Geofence? Geofence { get; set; }
}
