using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using YoTelematics.Application.Common;
using YoTelematics.Domain.Entities;
using YoTelematics.Infrastructure;
using YoTelematics.Infrastructure.Persistence;

namespace YoTelematics.Tests.Integration;

public class Phase1DatabaseAcceptanceTests
{
    private const string AppUserConnString =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoapp_user;Password=YoAppUserPass2026!;Pooling=true";

    private const string WorkerConnString =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!;Pooling=true";

    private const string AdminConnString =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoadmin;Password=YoStrongPassword2026!;Pooling=true";

    [Fact]
    public async Task AT_1_1_Hierarchical_Rls_Isolation_And_FailClosed_NullIf_Guard_Should_Work()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var rootId = Guid.NewGuid();
        var dhakaId = Guid.NewGuid();
        var ctgId = Guid.NewGuid();
        var dhakaAssetId = Guid.NewGuid();
        var ctgAssetId = Guid.NewGuid();

        var rootPath = $"root_{suffix}";
        var dhakaPath = $"root_{suffix}.dhaka";
        var ctgPath = $"root_{suffix}.ctg";

        // Seed tenants and assets using yoworker_service (BYPASSRLS)
        await using (var workerConn = new NpgsqlConnection(WorkerConnString))
        {
            await workerConn.OpenAsync();
            await using var cmd = workerConn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO tenants (id, parent_tenant_id, hierarchy_path, name, slug)
                VALUES
                    ('{rootId}', NULL, '{rootPath}'::ltree, 'Root Distributor', 'root-{suffix}'),
                    ('{dhakaId}', '{rootId}', '{dhakaPath}'::ltree, 'Dhaka Dealer', 'dhaka-{suffix}'),
                    ('{ctgId}', '{rootId}', '{ctgPath}'::ltree, 'Chittagong Dealer', 'ctg-{suffix}');

                INSERT INTO assets (id, tenant_id, category, name, subscription_expires_at_utc)
                VALUES
                    ('{dhakaAssetId}', '{dhakaId}', 'VEHICLE', 'Dhaka Asset', NOW() + INTERVAL '30 days'),
                    ('{ctgAssetId}', '{ctgId}', 'VEHICLE', 'CTG Asset', NOW() + INTERVAL '30 days');
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection();
        services.AddYoTelematicsPersistence(AppUserConnString);
        await using var provider = services.BuildServiceProvider();

        var tenantAccessor = provider.GetRequiredService<ITenantContextAccessor>();

        // 1. Missing/empty tenant context MUST fail-closed and return 0 rows (NULLIF guard)
        tenantAccessor.CurrentTenantPath = string.Empty;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YoTelematicsDbContext>();
            var leakedCount = await db.Assets.CountAsync();
            leakedCount.Should().Be(0, "empty app.current_tenant_path must fail-closed via NULLIF(..., '')::ltree");
        }

        // 2. Child tenant (Dhaka) sees ONLY its own asset, never sibling (CTG)
        tenantAccessor.CurrentTenantPath = dhakaPath;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YoTelematicsDbContext>();
            var visibleIds = await db.Assets.Select(a => a.Id).ToListAsync();
            visibleIds.Should().ContainSingle().Which.Should().Be(dhakaAssetId);
        }

        // 3. Parent tenant (Root) sees BOTH child tenant assets via ltree (<@) hierarchy
        tenantAccessor.CurrentTenantPath = rootPath;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YoTelematicsDbContext>();
            var visibleIds = await db.Assets.Select(a => a.Id).ToListAsync();
            visibleIds.Should().BeEquivalentTo(new[] { dhakaAssetId, ctgAssetId });
        }
    }

    [Fact]
    public async Task AT_1_2_Ledger_Balance_Trigger_Commission_Overlap_And_AuditLog_Immutability_Should_Enforce()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenantId = Guid.NewGuid();
        var dealerId = Guid.NewGuid();
        var cashAccountId = Guid.NewGuid();
        var revenueAccountId = Guid.NewGuid();

        await using var workerConn = new NpgsqlConnection(WorkerConnString);
        await workerConn.OpenAsync();

        await using (var seedCmd = workerConn.CreateCommand())
        {
            seedCmd.CommandText = $@"
                INSERT INTO tenants (id, hierarchy_path, name, slug)
                VALUES ('{tenantId}', 'ledger_{suffix}'::ltree, 'Ledger Tenant', 'ledger-{suffix}');

                INSERT INTO users (id, tenant_id, phone, role)
                VALUES ('{dealerId}', '{tenantId}', '+88017{suffix[..8]}', 'Dealer');

                INSERT INTO ledger_accounts (id, tenant_id, account_type)
                VALUES
                    ('{cashAccountId}', '{tenantId}', 'PlatformCash'),
                    ('{revenueAccountId}', '{tenantId}', 'SubscriptionRevenue');
            ";
            await seedCmd.ExecuteNonQueryAsync();
        }

        // 1. Unbalanced ledger transaction MUST throw on COMMIT (trg_enforce_balanced_ledger)
        await using (var unbalancedTx = await workerConn.BeginTransactionAsync())
        {
            var trxId = Guid.NewGuid();
            await using var cmd = workerConn.CreateCommand();
            cmd.Transaction = unbalancedTx;
            cmd.CommandText = $@"
                INSERT INTO ledger_transactions (id, tenant_id, idempotency_key, description)
                VALUES ('{trxId}', '{tenantId}', 'unbalanced-{suffix}', 'Unbalanced test');

                INSERT INTO ledger_entries (id, tenant_id, transaction_id, account_id, debit, credit)
                VALUES ('{Guid.NewGuid()}', '{tenantId}', '{trxId}', '{cashAccountId}', 500.00, 0.00);
            ";
            await cmd.ExecuteNonQueryAsync();

            var commitAct = async () => await unbalancedTx.CommitAsync();
            await commitAct.Should().ThrowAsync<PostgresException>()
                .WithMessage("*Unbalanced ledger transaction*");
        }

        // 2. Balanced double-entry transaction MUST commit cleanly
        await using (var balancedTx = await workerConn.BeginTransactionAsync())
        {
            var trxId = Guid.NewGuid();
            await using var cmd = workerConn.CreateCommand();
            cmd.Transaction = balancedTx;
            cmd.CommandText = $@"
                INSERT INTO ledger_transactions (id, tenant_id, idempotency_key, description)
                VALUES ('{trxId}', '{tenantId}', 'balanced-{suffix}', 'Balanced test');

                INSERT INTO ledger_entries (id, tenant_id, transaction_id, account_id, debit, credit)
                VALUES
                    ('{Guid.NewGuid()}', '{tenantId}', '{trxId}', '{cashAccountId}', 500.00, 0.00),
                    ('{Guid.NewGuid()}', '{tenantId}', '{trxId}', '{revenueAccountId}', 0.00, 500.00);
            ";
            await cmd.ExecuteNonQueryAsync();
            await balancedTx.CommitAsync();
        }

        // 3. Overlapping commission_rates date ranges for same dealer & category MUST be rejected by btree_gist
        await using (var rateCmd1 = workerConn.CreateCommand())
        {
            rateCmd1.CommandText = $@"
                INSERT INTO commission_rates (id, tenant_id, dealer_user_id, asset_category, commission_percent, valid_from_utc, valid_to_utc)
                VALUES ('{Guid.NewGuid()}', '{tenantId}', '{dealerId}', 'VEHICLE', 10.00, '2026-01-01T00:00:00Z', '2026-12-31T23:59:59Z');
            ";
            await rateCmd1.ExecuteNonQueryAsync();
        }

        Func<Task> overlapAct = async () =>
        {
            await using var rateCmd2 = workerConn.CreateCommand();
            rateCmd2.CommandText = $@"
                INSERT INTO commission_rates (id, tenant_id, dealer_user_id, asset_category, commission_percent, valid_from_utc, valid_to_utc)
                VALUES ('{Guid.NewGuid()}', '{tenantId}', '{dealerId}', 'VEHICLE', 12.00, '2026-06-01T00:00:00Z', '2027-06-01T00:00:00Z');
            ";
            await rateCmd2.ExecuteNonQueryAsync();
        };
        (await overlapAct.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23P01");

        // 4. security_audit_logs MUST be immutable (REVOKE UPDATE, DELETE, TRUNCATE for yoapp_user & yoworker_service)
        var auditId = Guid.NewGuid();
        await using (var insertAuditCmd = workerConn.CreateCommand())
        {
            insertAuditCmd.CommandText = $@"
                INSERT INTO security_audit_logs (id, tenant_id, action_type, details_jsonb)
                VALUES ('{auditId}', '{tenantId}', 'TEST_AUDIT_EVENT', '{{}}'::jsonb);
            ";
            await insertAuditCmd.ExecuteNonQueryAsync();
        }

        Func<Task> deleteAuditAct = async () =>
        {
            await using var delCmd = workerConn.CreateCommand();
            delCmd.CommandText = $"DELETE FROM security_audit_logs WHERE id = '{auditId}';";
            await delCmd.ExecuteNonQueryAsync();
        };
        (await deleteAuditAct.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task AT_1_3_Catalog_Guard_All_26_Oltp_Tables_Have_Forced_Rls_And_Hypertables_Have_Composite_Pk()
    {
        await using var adminConn = new NpgsqlConnection(AdminConnString);
        await adminConn.OpenAsync();

        // 1. Check that zero OLTP tables with tenant_id (excluding hypertables) lack ENABLE + FORCE RLS
        await using (var rlsCheckCmd = adminConn.CreateCommand())
        {
            rlsCheckCmd.CommandText = @"
                SELECT c.relname
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                  AND c.relkind = 'r'
                  AND c.relname NOT IN ('telemetry_history', 'telemetry_person', 'data_protection_keys', 'device_models', 'geofence_wifi_fingerprints', 'spatial_ref_sys')
                  AND (c.relrowsecurity = false OR c.relforcerowsecurity = false);
            ";

            var unprotectedTables = new List<string>();
            await using var reader = await rlsCheckCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                unprotectedTables.Add(reader.GetString(0));
            }

            unprotectedTables.Should().BeEmpty("all 26 tenant OLTP tables must have ENABLE and FORCE ROW LEVEL SECURITY");
        }

        // 2. Count exact number of Forced-RLS OLTP tables (must be 26)
        await using (var countCmd = adminConn.CreateCommand())
        {
            countCmd.CommandText = @"
                SELECT COUNT(*)
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                  AND c.relkind = 'r'
                  AND c.relrowsecurity = true
                  AND c.relforcerowsecurity = true;
            ";
            var forcedCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
            forcedCount.Should().Be(26);
        }

        // 3. Verify composite PK column order (asset_id, device_timestamp_utc, packet_type) on both hypertables
        foreach (var hypertable in new[] { "telemetry_history", "telemetry_person" })
        {
            await using var pkCmd = adminConn.CreateCommand();
            pkCmd.CommandText = $@"
                SELECT a.attname
                FROM pg_index i
                JOIN pg_class c ON c.oid = i.indrelid
                JOIN unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
                JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
                WHERE c.relname = '{hypertable}' AND i.indisprimary
                ORDER BY k.ord;
            ";

            var pkColumns = new List<string>();
            await using var reader = await pkCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                pkColumns.Add(reader.GetString(0));
            }

            pkColumns.Should().Equal("asset_id", "device_timestamp_utc", "packet_type");
        }

        // 4. Verify connection string guard rejects 'No Reset On Close=true'
        var services = new ServiceCollection();
        Action act = () => services.AddYoTelematicsPersistence(AppUserConnString + ";No Reset On Close=true");
        act.Should().Throw<InvalidOperationException>().WithMessage("*No Reset On Close=true*");
    }
}

