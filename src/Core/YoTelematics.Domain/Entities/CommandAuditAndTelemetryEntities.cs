using NetTopologySuite.Geometries;
using YoTelematics.Domain.Common;

namespace YoTelematics.Domain.Entities;

public class DeviceCommand : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public string Imei { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string DeliveryChannel { get; set; } = "TCP";
    public string Status { get; set; } = "Pending";
    public Guid? SupersededByCommandId { get; set; }
    public Guid? IssuedByUserId { get; set; }
    public string AuthMethod { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAtUtc { get; set; }
    public DateTimeOffset? AckAtUtc { get; set; }
    public string? RawResponse { get; set; }

    public Asset? Asset { get; set; }
    public DeviceCommand? SupersededByCommand { get; set; }
    public User? IssuedByUser { get; set; }
}

public class SecurityAuditLog : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string? SourceIpOrCli { get; set; }
    public string DetailsJsonb { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public abstract class TelemetryRecordBase
{
    public Guid AssetId { get; set; }
    public DateTimeOffset DeviceTimestampUtc { get; set; }
    public short PacketType { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset ServerReceivedUtc { get; set; }
    public string Imei { get; set; } = string.Empty;
    public Point? Location { get; set; }
    public string LocationSource { get; set; } = "GPS";
    public short AccuracyM { get; set; } = 10;
    public float SpeedKmh { get; set; }
    public short Course { get; set; }
    public short Satellites { get; set; }
    public bool IsGpsValid { get; set; }
    public bool? Ignition { get; set; }
    public bool? MainPowerConnected { get; set; }
    public short? BatteryPercent { get; set; }
    public float? ExternalVoltage { get; set; }
    public short? GsmSignal { get; set; }
    public string? AlarmCode { get; set; }
    public bool IsHistoricalReplay { get; set; }
    public bool IsClockSkewed { get; set; }
    public string? AttributesJsonb { get; set; }
}

public class TelemetryHistory : TelemetryRecordBase
{
}

public class TelemetryPerson : TelemetryRecordBase
{
}
