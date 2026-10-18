namespace YoTelematics.Application.Commands;

public interface IOutboundCommandTransport
{
    Task<bool> TrySendRawCommandAsync(
        string imei,
        string protocolName,
        string asciiCommand,
        CancellationToken cancellationToken = default);
}

public sealed record IssueCommandRequest(
    Guid TenantId,
    Guid AssetId,
    string Imei,
    string CommandType,
    Guid? IssuedByUserId,
    string AuthMethod,
    string DeliveryChannel = "TCP",
    string? SourceIpOrCli = null);

public sealed record CommandProgressOutcome(
    Guid CommandId,
    string CommandType,
    string Status,
    string? DispatchedAsciiCommand,
    bool RelayStateChanged,
    string? NewRelayState,
    short? UpdatedHeartbeatIntervalSec);

public interface IDeviceCommandService
{
    Task<CommandProgressOutcome> IssueCommandAsync(
        IssueCommandRequest request,
        CancellationToken cancellationToken = default);

    Task<CommandProgressOutcome?> HandleDeviceCommandResponseAsync(
        string imei,
        string responseText,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken = default);
}
