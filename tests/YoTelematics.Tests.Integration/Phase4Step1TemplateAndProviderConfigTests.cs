using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using YoTelematics.Application.Common;
using YoTelematics.Application.Notifications;
using YoTelematics.Infrastructure;
using YoTelematics.Infrastructure.Notifications;

namespace YoTelematics.Tests.Integration;

public sealed class Phase4Step1TemplateAndProviderConfigTests
{
    private const string AdminConn =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoadmin;Password=YoStrongPassword2026!";

    private const string WorkerConn =
        "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoworker_service;Password=YoWorkerPass2026!";

    [Fact]
    public void AT_4_1_1_LocaleTemplateResolver_ResolvesBanglaAndEnglishTemplates_WithDynamicParameters_AndPreCachedAudio()
    {
        var resolver = new LocaleTemplateResolver();
        var parameters = new Dictionary<string, string>
        {
            ["asset_name"] = "Yamaha R15 Dhaka-Metro",
            ["distance_m"] = "125.4"
        };

        var bnTheft = resolver.Resolve("alert.bike.theft", "bn-BD", parameters);
        bnTheft.Locale.Should().Be("bn-BD");
        bnTheft.Title.Should().Contain("Yamaha R15 Dhaka-Metro");
        bnTheft.PreCachedAudioUri.Should().Be("sound:yotelematics/bn-BD/alert_bike_theft");
        bnTheft.TtsScript.Should().Contain("Yamaha R15 Dhaka-Metro");

        var enDisp = resolver.Resolve("alert.bike.displacement", "en-US", parameters);
        enDisp.Locale.Should().Be("en-US");
        enDisp.Body.Should().Contain("125.4");
        enDisp.PreCachedAudioUri.Should().Be("sound:yotelematics/en-US/alert_bike_displacement");

        var kidSos = resolver.Resolve("alert.kid.sos", null, new Dictionary<string, string> { ["asset_name"] = "Ayaan" });
        kidSos.Locale.Should().Be("bn-BD", "default locale must fall back to bn-BD");
        kidSos.PreCachedAudioUri.Should().Be("sound:yotelematics/bn-BD/alert_kid_sos");
    }

    [Fact]
    public async Task AT_4_1_2_TenantProviderConfigStore_EncryptsConfigBytesInDatabase_AndDecryptsViaDataProtectionKeyRing()
    {
        var tenantId = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(WorkerConn))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, hierarchy_path, name, slug)
                VALUES ('{tenantId}', 'root.cfg_{tenantId:N}', 'Config Tenant', 'cfg-{tenantId:N}');
            ", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddYoTelematicsInfrastructure(AdminConn);
        await using var sp = services.BuildServiceProvider();

        var dpProvider = sp.GetRequiredService<IDataProtectionProvider>();
        var configStore = new DataProtectionTenantProviderConfigStore(dpProvider, WorkerConn);

        var sipConfig = new TenantSipAriConfig(
            AriEndpointUrl: "http://asterisk.yotelematics.local:8088/ari",
            AriUsername: "yo_ari_user",
            AriPassword: "SuperSecretAriPassword2026!",
            CallerIdNumber: "+8809612345678",
            TrunkName: "bd-sip-trunk");

        await configStore.UpsertConfigAsync(tenantId, "SIP", sipConfig);

        // Verify raw bytes in PostgreSQL are encrypted (do NOT contain plaintext password)
        await using (var conn = new NpgsqlConnection(WorkerConn))
        {
            await conn.OpenAsync();
            await using var rawCmd = new NpgsqlCommand(
                "SELECT encrypted_config_bytes FROM tenant_provider_configs WHERE tenant_id = @t AND provider_type = 'SIP';",
                conn);
            rawCmd.Parameters.AddWithValue("t", tenantId);
            byte[] rawBytes = (byte[])(await rawCmd.ExecuteScalarAsync())!;
            string rawAsString = Encoding.UTF8.GetString(rawBytes);
            rawAsString.Should().NotContain("SuperSecretAriPassword2026!");
        }

        // Verify decryption returns exact configuration
        var decrypted = await configStore.GetActiveConfigAsync<TenantSipAriConfig>(tenantId, "SIP");
        decrypted.Should().NotBeNull();
        decrypted!.AriPassword.Should().Be("SuperSecretAriPassword2026!");
        decrypted.CallerIdNumber.Should().Be("+8809612345678");
    }
}
