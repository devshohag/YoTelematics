using Microsoft.AspNetCore.SignalR;

namespace YoTelematics.WebApi.Hubs;

public sealed record LivePositionBroadcast(
    Guid AssetId,
    DateTimeOffset DeviceTimestampUtc,
    double? Latitude,
    double? Longitude,
    float SpeedKmh,
    short Course,
    bool? Ignition,
    bool? MainPowerConnected,
    string ArmedState);

public sealed class LiveTrackingHub : Hub
{
    public static string GetAssetGroupName(Guid assetId) => $"asset:{assetId:D}";

    public async Task SubscribeToAsset(Guid assetId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetAssetGroupName(assetId));
    }

    public async Task UnsubscribeFromAsset(Guid assetId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetAssetGroupName(assetId));
    }
}
