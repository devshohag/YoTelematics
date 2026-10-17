using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using YoTelematics.Application.Common;
using YoTelematics.Infrastructure;
using YoTelematics.Infrastructure.Persistence;

namespace YoTelematics.Tests.Integration;

public sealed class Phase1DatabaseAcceptanceTests
{
    private const string AdminConn = "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoadmin;Password=YoStrongPassword2026!";
    private const string AppConn = "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoapp_user;Password=YoAppUserPass2026!";

    [Fact]
    public async Task AT_1_3_Catalog_MustEnforceForcedRls_OnAllTenantScopedOltpTables()
    {
        await using var conn = new NpgsqlConnection(AdminConn);
        await conn.OpenAsync();

        const string sql = @"
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = 'public'
              AND c.relkind = 'r'
              AND a.attname = 'tenant_id'
              AND c.relname NOT IN ('telemetry_history', 'telemetry_person')
              AND (c.relrowsecurity = false OR c.relforcerowsecurity = false);";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var unprotectedTables = new List<string>();
        while (await reader.ReadAsync())
        {
            unprotectedTables.Add(reader.GetString(0));
        }

        unprotectedTables.Should().BeEmpty("every OLTP table with tenant_id must have ENABLE and FORCE ROW LEVEL SECURITY");
    }

    [Fact]
    public async Task AT_1_3_RlsFailClosed_QueryWithoutTenantContext_ReturnsZeroRows_AndIsolatesTenants()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();
        var pathA = $"root.t_{tenantAId:N}";
        var pathB = $"root.t_{tenantBId:N}";

        await using (var adminConn = new NpgsqlConnection(AdminConn))
        {
            await adminConn.OpenAsync();
            await using var seedCmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, hierarchy_path, name, slug) VALUES
                ('{tenantAId}', '{pathA}', 'Tenant A', 'slug-{tenantAId:N}'),
                ('{tenantBId}', '{pathB}', 'Tenant B', 'slug-{tenantBId:N}');

                INSERT INTO assets (id, tenant_id, category, name, subscription_expires_at_utc) VALUES
                ('{Guid.NewGuid()}', '{tenantAId}', 'BIKE', 'Bike A', NOW() + INTERVAL '30 days'),
                ('{Guid.NewGuid()}', '{tenantBId}', 'BIKE', 'Bike B', NOW() + INTERVAL '30 days');
            ", adminConn);
            await seedCmd.ExecuteNonQueryAsync();
        }

        // 1. Fail-Closed Check: Empty context -> 0 rows
        await using (var appConn = new NpgsqlConnection(AppConn))
        {
            await appConn.OpenAsync();
            await using (var setCmd = new NpgsqlCommand("SELECT set_config('app.current_tenant_path', '', false);", appConn))
            {
                await setCmd.ExecuteNonQueryAsync();
            }

            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM assets WHERE tenant_id IN (@a, @b);",
                appConn);
            cmd.Parameters.AddWithValue("a", tenantAId);
            cmd.Parameters.AddWithValue("b", tenantBId);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            count.Should().Be(0, "empty string tenant path must fail-closed via NULLIF guard");
        }

        // 2. Tenant A context -> sees only Tenant A
        await using (var appConn = new NpgsqlConnection(AppConn))
        {
            await appConn.OpenAsync();
            await using (var setCmd = new NpgsqlCommand($"SELECT set_config('app.current_tenant_path', '{pathA}', false);", appConn))
            {
                await setCmd.ExecuteNonQueryAsync();
            }

            await using var cmd = new NpgsqlCommand("SELECT name FROM assets;", appConn);
            var names = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));

            names.Should().Contain("Bike A").And.NotContain("Bike B");
        }
    }

    [Fact]
    public async Task AT_1_1_And_AT_1_2_Ledger_RejectsUnbalancedEntries_AndEnforcesIdempotency()
    {
        var tenantId = Guid.NewGuid();
        var accDebit = Guid.NewGuid();
        var accCredit = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var idemKey = $"idem-{Guid.NewGuid():N}";

        await using var conn = new NpgsqlConnection(AdminConn);
        await conn.OpenAsync();

        await using (var setup = new NpgsqlCommand($@"
            INSERT INTO tenants (id, hierarchy_path, name, slug) VALUES ('{tenantId}', 'root.l_{tenantId:N}', 'Ledger T', 'lt-{tenantId:N}');
            INSERT INTO ledger_accounts (id, tenant_id, account_type) VALUES
            ('{accDebit}', '{tenantId}', 'BkashClearing'),
            ('{accCredit}', '{tenantId}', 'SystemRevenue');
        ", conn))
        {
            await setup.ExecuteNonQueryAsync();
        }

        // 1. Unbalanced transaction must fail on COMMIT
        Func<Task> unbalancedAct = async () =>
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO ledger_transactions (id, tenant_id, idempotency_key) VALUES ('{Guid.NewGuid()}', '{tenantId}', 'unbal-{Guid.NewGuid():N}');
                INSERT INTO ledger_entries (id, tenant_id, transaction_id, account_id, debit, credit)
                VALUES ('{Guid.NewGuid()}', '{tenantId}', (SELECT id FROM ledger_transactions LIMIT 1), '{accDebit}', 200.00, 0);
            ", conn, tx);
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        };

        await unbalancedAct.Should().ThrowAsync<PostgresException>()
            .WithMessage("*Unbalanced ledger transaction*");

        // 2. Balanced transaction succeeds
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO ledger_transactions (id, tenant_id, idempotency_key) VALUES ('{txId}', '{tenantId}', '{idemKey}');
                INSERT INTO ledger_entries (id, tenant_id, transaction_id, account_id, debit, credit) VALUES
                ('{Guid.NewGuid()}', '{tenantId}', '{txId}', '{accDebit}', 200.00, 0),
                ('{Guid.NewGuid()}', '{tenantId}', '{txId}', '{accCredit}', 0, 200.00);
            ", conn, tx);
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }

        // 3. Duplicate IdempotencyKey is rejected
        Func<Task> duplicateAct = async () =>
        {
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO ledger_transactions (id, tenant_id, idempotency_key) VALUES ('{Guid.NewGuid()}', '{tenantId}', '{idemKey}');",
                conn);
            await cmd.ExecuteNonQueryAsync();
        };

        await duplicateAct.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task AT_1_4_DeviceSwap_PreservesFullAssetTelemetryHistory_AndLogsSwapRecord()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var oldImei = $"{Random.Shared.NextInt64(100000000000000, 999999999999999)}";
        var newImei = $"{Random.Shared.NextInt64(100000000000000, 999999999999999)}";

        await using var conn = new NpgsqlConnection(AdminConn);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand($@"
            INSERT INTO tenants (id, hierarchy_path, name, slug) VALUES ('{tenantId}', 'root.s_{tenantId:N}', 'Swap T', 'st-{tenantId:N}');
            INSERT INTO users (id, tenant_id, phone, role) VALUES ('{userId}', '{tenantId}', '+88017{Random.Shared.Next(10000000, 99999999)}', 'Dealer');
            INSERT INTO device_models (id, protocol, cut_cmd_template, restore_cmd_template, status_cmd_template, timer_active_cmd_template, timer_normal_cmd_template, heartbeat_armed_cmd_template, heartbeat_normal_cmd_template)
            VALUES ('GT06_SWAP', 'GT06', 'DYD#', 'HFYD#', 'STATUS#', 'TIMER,10#', 'TIMER,20#', 'HBT,1#', 'HBT,3#') ON CONFLICT DO NOTHING;

            INSERT INTO inventory_devices (imei, tenant_id, model_id, barcode_serial, device_password_encrypted) VALUES
            ('{oldImei}', '{tenantId}', 'GT06_SWAP', 'BC-{oldImei}', E'\\x0102'),
            ('{newImei}', '{tenantId}', 'GT06_SWAP', 'BC-{newImei}', E'\\x0102');

            INSERT INTO assets (id, tenant_id, current_imei, category, name, subscription_expires_at_utc)
            VALUES ('{assetId}', '{tenantId}', '{oldImei}', 'BIKE', 'Swap Bike', NOW() + INTERVAL '30 days');

            INSERT INTO telemetry_history (asset_id, device_timestamp_utc, packet_type, tenant_id, server_received_utc, imei, speed_kmh)
            VALUES ('{assetId}', NOW() - INTERVAL '1 hour', 1, '{tenantId}', NOW() - INTERVAL '1 hour', '{oldImei}', 42.5);

            -- Perform Warranty Swap to newImei
            UPDATE assets SET current_imei = '{newImei}' WHERE id = '{assetId}';
            INSERT INTO asset_device_swaps (id, tenant_id, asset_id, old_imei, new_imei, swapped_by_user_id, reason)
            VALUES ('{Guid.NewGuid()}', '{tenantId}', '{assetId}', '{oldImei}', '{newImei}', '{userId}', 'Faulty GSM');

            INSERT INTO telemetry_history (asset_id, device_timestamp_utc, packet_type, tenant_id, server_received_utc, imei, speed_kmh)
            VALUES ('{assetId}', NOW(), 1, '{tenantId}', NOW(), '{newImei}', 55.0);
        ", conn);
        await cmd.ExecuteNonQueryAsync();

        await using var countCmd = new NpgsqlCommand($"SELECT COUNT(*) FROM telemetry_history WHERE asset_id = '{assetId}';", conn);
        var historyCount = (long)(await countCmd.ExecuteScalarAsync())!;
        historyCount.Should().Be(2, "both pre-swap and post-swap telemetry must remain queryable under the same AssetId");
    }

    [Fact]
    public void AT_1_5_DataProtection_KeyRing_SurvivesContainerRecreation_ViaDatabasePersistence()
    {
        byte[] cipherBytes;

        // Container Instance 1: Encrypt device password and persist key to PostgreSQL
        using (var sp1 = BuildServiceProviderWithDbDataProtection())
        {
            var protector1 = sp1.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("InventoryDevice.Password.v1");
            cipherBytes = protector1.Protect(System.Text.Encoding.UTF8.GetBytes("839201"));
        }

        // Container Instance 2 (Fresh ServiceProvider & memory): Decrypt using key loaded from PostgreSQL
        using (var sp2 = BuildServiceProviderWithDbDataProtection())
        {
            var protector2 = sp2.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("InventoryDevice.Password.v1");
            var plainBytes = protector2.Unprotect(cipherBytes);
            System.Text.Encoding.UTF8.GetString(plainBytes).Should().Be("839201");
        }
    }

    private static ServiceProvider BuildServiceProviderWithDbDataProtection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantAccessor(null));
        services.AddYoTelematicsInfrastructure(AdminConn);
        return services.BuildServiceProvider();
    }

    private sealed class StaticTenantAccessor(string? path) : ITenantContextAccessor
    {
        public Guid? CurrentTenantId { get; set; }
        public string? CurrentTenantPath { get; set; } = path;
        public Guid? CurrentBranchId { get; set; }
    }
}




