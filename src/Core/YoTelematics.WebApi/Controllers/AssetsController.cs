using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using YoTelematics.Infrastructure.Persistence;

namespace YoTelematics.WebApi.Controllers;

public sealed record DisarmAssetResponse(
    Guid AssetId,
    string ArmedState,
    long NewStateVersion,
    bool GraceTimerCancelled);

public sealed record EnableServiceModeRequest(int DurationHours = 24);

public sealed record RoutePointDto(
    DateTimeOffset DeviceTimestampUtc,
    double Latitude,
    double Longitude,
    float SpeedKmh,
    short Course,
    bool? Ignition);

public sealed record RouteStopDto(
    double Latitude,
    double Longitude,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    int DurationSeconds);

public sealed record RoutePlaybackResponse(
    Guid AssetId,
    IReadOnlyList<RoutePointDto> PolylinePoints,
    IReadOnlyList<RouteStopDto> Stops);

[ApiController]
[Route("api/assets")]
public sealed class AssetsController(YoTelematicsDbContext dbContext) : ControllerBase
{
    /// <summary>
    /// Pre-Start Disarm Endpoint (AT-4.2): Cancels any active 20s GracePeriod by incrementing state_version
    /// and setting armed_state = 'Disarmed', preventing voice call escalation.
    /// </summary>
    [HttpPost("{assetId:guid}/disarm")]
    public async Task<ActionResult<DisarmAssetResponse>> DisarmAsset(
        Guid assetId,
        CancellationToken ct)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return NotFound();

        var profile = await dbContext.VehicleProfiles.FirstOrDefaultAsync(vp => vp.AssetId == assetId, ct);
        if (profile is null) return BadRequest("Asset does not have a vehicle profile.");

        bool wasInGrace = profile.ArmedState == "GracePeriod";

        asset.StateVersion += 1;
        profile.ArmedState = "Disarmed";
        profile.GraceExpiresAtUtc = null;
        profile.AutoArmDueAtUtc = null;
        profile.VibrationCountWindow = 0;

        await dbContext.SaveChangesAsync(ct);

        return Ok(new DisarmAssetResponse(
            AssetId: asset.Id,
            ArmedState: profile.ArmedState,
            NewStateVersion: asset.StateVersion,
            GraceTimerCancelled: wasInGrace));
    }

    [HttpPost("{assetId:guid}/arm")]
    public async Task<ActionResult<DisarmAssetResponse>> ArmAsset(
        Guid assetId,
        CancellationToken ct)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return NotFound();

        var profile = await dbContext.VehicleProfiles.FirstOrDefaultAsync(vp => vp.AssetId == assetId, ct);
        if (profile is null) return BadRequest("Asset does not have a vehicle profile.");

        asset.StateVersion += 1;
        profile.ArmedState = "Armed";
        profile.GraceExpiresAtUtc = null;
        profile.AutoArmDueAtUtc = null;
        profile.VibrationCountWindow = 0;

        await dbContext.SaveChangesAsync(ct);

        return Ok(new DisarmAssetResponse(
            AssetId: asset.Id,
            ArmedState: profile.ArmedState,
            NewStateVersion: asset.StateVersion,
            GraceTimerCancelled: false));
    }

    /// <summary>
    /// Blocker B5 Fix: Allows owner to enable 24h Mechanic Service Mode so battery disconnection does not trigger theft calls.
    /// </summary>
    [HttpPost("{assetId:guid}/service-mode")]
    public async Task<IActionResult> EnableServiceMode(
        Guid assetId,
        [FromBody] EnableServiceModeRequest request,
        CancellationToken ct)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return NotFound();

        int hours = Math.Clamp(request.DurationHours, 1, 72);
        asset.StateVersion += 1;
        asset.ServiceModeUntilUtc = DateTimeOffset.UtcNow.AddHours(hours);

        await dbContext.SaveChangesAsync(ct);
        return Ok(new { asset.Id, asset.ServiceModeUntilUtc });
    }

    /// <summary>
    /// High-Speed Route Playback & Stop Detection API using the composite PK (asset_id, device_timestamp_utc, packet_type).
    /// First verifies RLS permission on OLTP assets table before querying the hypertable (Blocker B2).
    /// </summary>
    [HttpGet("{assetId:guid}/playback")]
    public async Task<ActionResult<RoutePlaybackResponse>> GetRoutePlayback(
        Guid assetId,
        [FromQuery] DateTimeOffset fromUtc,
        [FromQuery] DateTimeOffset toUtc,
        CancellationToken ct)
    {
        // Step 1: Verify caller has RLS visibility on this asset
        bool canAccess = await dbContext.Assets.AnyAsync(a => a.Id == assetId, ct);
        if (!canAccess) return NotFound();

        var points = new List<RoutePointDto>();
        var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        const string sql = @"
            SELECT
                device_timestamp_utc,
                ST_Y(location::geometry) AS lat,
                ST_X(location::geometry) AS lon,
                speed_kmh,
                course,
                ignition
            FROM telemetry_history
            WHERE asset_id = @assetId
              AND device_timestamp_utc >= @fromUtc
              AND device_timestamp_utc <= @toUtc
              AND packet_type = 1
              AND is_gps_valid = TRUE
              AND location IS NOT NULL
            ORDER BY device_timestamp_utc ASC;";

        await using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("assetId", assetId);
            cmd.Parameters.AddWithValue("fromUtc", fromUtc);
            cmd.Parameters.AddWithValue("toUtc", toUtc);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                points.Add(new RoutePointDto(
                    DeviceTimestampUtc: reader.GetFieldValue<DateTimeOffset>(0),
                    Latitude: reader.GetDouble(1),
                    Longitude: reader.GetDouble(2),
                    SpeedKmh: reader.GetFloat(3),
                    Course: reader.GetInt16(4),
                    Ignition: reader.IsDBNull(5) ? null : reader.GetBoolean(5)));
            }
        }

        var stops = ComputeStops(points, minimumStopSeconds: 180);
        return Ok(new RoutePlaybackResponse(assetId, points, stops));
    }

    public static IReadOnlyList<RouteStopDto> ComputeStops(
        IReadOnlyList<RoutePointDto> points,
        int minimumStopSeconds = 180)
    {
        var stops = new List<RouteStopDto>();
        if (points.Count < 2) return stops;

        RoutePointDto? stopStart = null;
        RoutePointDto? lastStationary = null;

        foreach (var pt in points)
        {
            bool isStationary = pt.SpeedKmh < 2.0f;
            if (isStationary)
            {
                stopStart ??= pt;
                lastStationary = pt;
            }
            else if (stopStart is not null && lastStationary is not null)
            {
                double secs = (lastStationary.DeviceTimestampUtc - stopStart.DeviceTimestampUtc).TotalSeconds;
                if (secs >= minimumStopSeconds)
                {
                    stops.Add(new RouteStopDto(
                        stopStart.Latitude,
                        stopStart.Longitude,
                        stopStart.DeviceTimestampUtc,
                        lastStationary.DeviceTimestampUtc,
                        (int)secs));
                }
                stopStart = null;
                lastStationary = null;
            }
        }

        if (stopStart is not null && lastStationary is not null)
        {
            double secs = (lastStationary.DeviceTimestampUtc - stopStart.DeviceTimestampUtc).TotalSeconds;
            if (secs >= minimumStopSeconds)
            {
                stops.Add(new RouteStopDto(
                    stopStart.Latitude,
                    stopStart.Longitude,
                    stopStart.DeviceTimestampUtc,
                    lastStationary.DeviceTimestampUtc,
                    (int)secs));
            }
        }

        return stops;
    }
}
