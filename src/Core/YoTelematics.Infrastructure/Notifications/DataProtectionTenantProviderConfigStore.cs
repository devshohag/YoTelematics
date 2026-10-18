using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using YoTelematics.Application.Notifications;

namespace YoTelematics.Infrastructure.Notifications;

public sealed class DataProtectionTenantProviderConfigStore : ITenantProviderConfigStore
{
    private const string ProtectorPurpose = "YoTelematics.TenantProviderConfig.v1";
    private readonly IDataProtector _protector;
    private readonly string _workerConnectionString;

    public DataProtectionTenantProviderConfigStore(
        IDataProtectionProvider dataProtectionProvider,
        string workerConnectionString)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _workerConnectionString = workerConnectionString;
    }

    public async Task UpsertConfigAsync<TConfig>(
        Guid tenantId,
        string providerType,
        TConfig config,
        CancellationToken cancellationToken = default)
    {
        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(config);
        byte[] encryptedBytes = _protector.Protect(plainBytes);

        await using var conn = new NpgsqlConnection(_workerConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO tenant_provider_configs (id, tenant_id, provider_type, encrypted_config_bytes, is_active)
            VALUES (@id, @tenantId, @providerType, @encryptedBytes, true)
            ON CONFLICT (tenant_id, provider_type)
            DO UPDATE SET
                encrypted_config_bytes = EXCLUDED.encrypted_config_bytes,
                is_active = true;", conn);

        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("providerType", providerType.ToUpperInvariant());
        cmd.Parameters.AddWithValue("encryptedBytes", encryptedBytes);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TConfig?> GetActiveConfigAsync<TConfig>(
        Guid tenantId,
        string providerType,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_workerConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(@"
            SELECT encrypted_config_bytes
            FROM tenant_provider_configs
            WHERE tenant_id = @tenantId
              AND provider_type = @providerType
              AND is_active = true
            LIMIT 1;", conn);

        cmd.Parameters.AddWithValue("tenantId", tenantId);
        cmd.Parameters.AddWithValue("providerType", providerType.ToUpperInvariant());

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is not byte[] encryptedBytes || encryptedBytes.Length == 0)
        {
            return default;
        }

        byte[] plainBytes = _protector.Unprotect(encryptedBytes);
        return JsonSerializer.Deserialize<TConfig>(plainBytes);
    }
}
