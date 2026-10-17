using YoTelematics.Domain.Common;

namespace YoTelematics.Domain.Entities;

public class DeviceModel
{
    public string Id { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string CutCmdTemplate { get; set; } = string.Empty;
    public string RestoreCmdTemplate { get; set; } = string.Empty;
    public string StatusCmdTemplate { get; set; } = string.Empty;
    public string StatusResponseRegex { get; set; } = @"ACC[:=]\s*(OFF|0).*SPEED[:=]\s*0";
    public string TimerActiveCmdTemplate { get; set; } = string.Empty;
    public string TimerNormalCmdTemplate { get; set; } = string.Empty;
    public string HeartbeatArmedCmdTemplate { get; set; } = string.Empty;
    public string HeartbeatNormalCmdTemplate { get; set; } = string.Empty;
    public bool SupportsAcInput { get; set; }
    public bool SupportsAccelerometerEvents { get; set; }
}

public class InventoryDevice : ITenantScopedEntity
{
    public string Imei { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string BarcodeSerial { get; set; } = string.Empty;
    public string? SimMsisdn { get; set; }
    public string? SimIccid { get; set; }
    public string ApnName { get; set; } = "gpinternet";
    public byte[] DevicePasswordEncrypted { get; set; } = Array.Empty<byte>();
    public Guid? CurrentHolderDealerId { get; set; }
    public string ProvisioningState { get; set; } = "InStock";
    public short CurrentHeartbeatIntervalSec { get; set; } = 180;
    public DateTimeOffset? LastConnectedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DeviceModel? Model { get; set; }
    public User? CurrentHolderDealer { get; set; }
}

public class InventoryMovement : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Imei { get; set; } = string.Empty;
    public Guid? FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public DateTimeOffset MovedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public InventoryDevice? Device { get; set; }
    public User? FromUser { get; set; }
    public User? ToUser { get; set; }
}
