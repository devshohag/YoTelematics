using System.Text.Json;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using YoTelematics.Application.Telemetry;

namespace YoTelematics.Infrastructure.Ingestion;

/// <summary>
/// High-throughput bulk writer that initializes 'tmp_batch' ONCE per dedicated connection
/// with 'ON COMMIT DELETE ROWS' (preventing pg_catalog bloat) and binary-COPYs batches
/// into 'telemetry_person' (for KID/PET) or 'telemetry_history' (for all vehicle categories).
/// </summary>
public sealed class CatalogSafeTelemetryBulkWriter : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private NpgsqlConnection? _dedicatedConnection;
    private bool _tempTableInitialized;

    public CatalogSafeTelemetryBulkWriter(string workerConnectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(workerConnectionString);
        builder.UseNetTopologySuite();
        _dataSource = builder.Build();
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_dedicatedConnection is null)
        {
            _dedicatedConnection = await _dataSource.OpenConnectionAsync(cancellationToken);
            _tempTableInitialized = false;
        }
        else if (_dedicatedConnection.State != System.Data.ConnectionState.Open)
        {
            await _dedicatedConnection.OpenAsync(cancellationToken);
            _tempTableInitialized = false;
        }

        if (!_tempTableInitialized)
        {
            await using var cmd = _dedicatedConnection.CreateCommand();
            cmd.CommandText = @"
                CREATE TEMP TABLE IF NOT EXISTS tmp_batch (LIKE telemetry_history)
                ON COMMIT DELETE ROWS;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _tempTableInitialized = true;
        }
    }

    public async Task<uint> GetTempTableOidAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var cmd = _dedicatedConnection!.CreateCommand();
        cmd.CommandText = @"
            SELECT c.oid
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = 'tmp_batch'
              AND n.oid = pg_my_temp_schema();";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToUInt32(result);
    }

    public async Task<int> FlushBatchAsync(
        IReadOnlyList<ShardedTelemetryEnvelope> batch,
        CancellationToken cancellationToken = default)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        await EnsureInitializedAsync(cancellationToken);

        var personBatch = new List<ShardedTelemetryEnvelope>();
        var vehicleBatch = new List<ShardedTelemetryEnvelope>();

        for (int i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            if (!item.Telemetry.ShouldPersistToHypertable)
            {
                continue;
            }

            if (IsPersonOrPetCategory(item.Category))
            {
                personBatch.Add(item);
            }
            else
            {
                vehicleBatch.Add(item);
            }
        }

        int totalInserted = 0;

        if (vehicleBatch.Count > 0)
        {
            totalInserted += await CopyGroupToTargetHypertableAsync(
                vehicleBatch,
                targetTableName: "telemetry_history",
                cancellationToken);
        }

        if (personBatch.Count > 0)
        {
            totalInserted += await CopyGroupToTargetHypertableAsync(
                personBatch,
                targetTableName: "telemetry_person",
                cancellationToken);
        }

        return totalInserted;
    }

    private async Task<int> CopyGroupToTargetHypertableAsync(
        IReadOnlyList<ShardedTelemetryEnvelope> group,
        string targetTableName,
        CancellationToken cancellationToken)
    {
        await using var tx = await _dedicatedConnection!.BeginTransactionAsync(cancellationToken);

        const string copySql = @"
            COPY tmp_batch (
                asset_id,
                device_timestamp_utc,
                packet_type,
                tenant_id,
                server_received_utc,
                imei,
                location,
                location_source,
                accuracy_m,
                speed_kmh,
                course,
                satellites,
                is_gps_valid,
                ignition,
                main_power_connected,
                battery_percent,
                external_voltage,
                gsm_signal,
                alarm_code,
                is_historical_replay,
                is_clock_skewed,
                attributes_jsonb
            ) FROM STDIN (FORMAT BINARY)";

        await using (var importer = await _dedicatedConnection.BeginBinaryImportAsync(copySql, cancellationToken))
        {
            for (int i = 0; i < group.Count; i++)
            {
                var env = group[i];
                var t = env.Telemetry;

                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(env.AssetId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(t.DeviceTimestampUtc.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await importer.WriteAsync((short)t.PacketType, NpgsqlDbType.Smallint, cancellationToken);
                await importer.WriteAsync(env.TenantId, NpgsqlDbType.Uuid, cancellationToken);
                await importer.WriteAsync(t.ServerReceivedUtc.UtcDateTime, NpgsqlDbType.TimestampTz, cancellationToken);
                await importer.WriteAsync(t.Imei, NpgsqlDbType.Varchar, cancellationToken);

                if (t.Latitude.HasValue && t.Longitude.HasValue)
                {
                    var point = new Point(t.Longitude.Value, t.Latitude.Value) { SRID = 4326 };
                    await importer.WriteAsync(point, NpgsqlDbType.Geography, cancellationToken);
                }
                else
                {
                    await importer.WriteNullAsync(cancellationToken);
                }

                await importer.WriteAsync(t.LocationSource, NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(t.AccuracyMeters, NpgsqlDbType.Smallint, cancellationToken);
                await importer.WriteAsync(t.SpeedKmh, NpgsqlDbType.Real, cancellationToken);
                await importer.WriteAsync(t.Course, NpgsqlDbType.Smallint, cancellationToken);
                await importer.WriteAsync(t.Satellites, NpgsqlDbType.Smallint, cancellationToken);
                await importer.WriteAsync(t.IsGpsValid, NpgsqlDbType.Boolean, cancellationToken);

                if (t.Ignition.HasValue)
                    await importer.WriteAsync(t.Ignition.Value, NpgsqlDbType.Boolean, cancellationToken);
                else
                    await importer.WriteNullAsync(cancellationToken);

                if (t.MainPowerConnected.HasValue)
                    await importer.WriteAsync(t.MainPowerConnected.Value, NpgsqlDbType.Boolean, cancellationToken);
                else
                    await importer.WriteNullAsync(cancellationToken);

                if (t.BatteryPercent.HasValue)
                    await importer.WriteAsync(t.BatteryPercent.Value, NpgsqlDbType.Smallint, cancellationToken);
                else
                    await importer.WriteNullAsync(cancellationToken);

                if (t.ExternalVoltage.HasValue)
                    await importer.WriteAsync(t.ExternalVoltage.Value, NpgsqlDbType.Real, cancellationToken);
                else
                    await importer.WriteNullAsync(cancellationToken);

                if (t.GsmSignal.HasValue)
                    await importer.WriteAsync(t.GsmSignal.Value, NpgsqlDbType.Smallint, cancellationToken);
                else
                    await importer.WriteNullAsync(cancellationToken);

                if (t.AlarmCode is not null)
                    await importer.WriteAsync(t.AlarmCode, NpgsqlDbType.Varchar, cancellationToken);
                else
                    await importer.WriteNullAsync(cancellationToken);

                await importer.WriteAsync(t.IsHistoricalReplay, NpgsqlDbType.Boolean, cancellationToken);
                await importer.WriteAsync(t.IsClockSkewed, NpgsqlDbType.Boolean, cancellationToken);

                if (t.Attributes is not null && t.Attributes.Count > 0)
                {
                    string json = JsonSerializer.Serialize(t.Attributes);
                    await importer.WriteAsync(json, NpgsqlDbType.Jsonb, cancellationToken);
                }
                else
                {
                    await importer.WriteNullAsync(cancellationToken);
                }
            }

            await importer.CompleteAsync(cancellationToken);
        }

        await using var mergeCmd = _dedicatedConnection.CreateCommand();
        mergeCmd.Transaction = tx;
        mergeCmd.CommandText = $@"
            INSERT INTO {targetTableName}
            SELECT * FROM tmp_batch
            ON CONFLICT (asset_id, device_timestamp_utc, packet_type) DO NOTHING;";

        int affected = await mergeCmd.ExecuteNonQueryAsync(cancellationToken);

        // Commit clears tmp_batch automatically via ON COMMIT DELETE ROWS while keeping its catalog OID
        await tx.CommitAsync(cancellationToken);
        return affected;
    }

    public static bool IsPersonOrPetCategory(string category) =>
        string.Equals(category, "KID", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(category, "PET", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_dedicatedConnection is not null)
        {
            await _dedicatedConnection.DisposeAsync();
        }
        await _dataSource.DisposeAsync();
    }
}
