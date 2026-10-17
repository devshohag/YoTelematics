using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YoTelematics.Application.Common;

namespace YoTelematics.Infrastructure.Persistence;

/// <summary>
/// Sets PostgreSQL session context 'app.current_tenant_path' on every opened connection.
/// Uses set_config('app.current_tenant_path', @p, false) so empty context safely triggers
/// the NULLIF(..., '')::ltree Fail-Closed RLS policy (returning 0 rows).
/// </summary>
public sealed class TenantRlsConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContextAccessor _tenantContext;

    public TenantRlsConnectionInterceptor(ITenantContextAccessor tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyTenantPath(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyTenantPathAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void ApplyTenantPath(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_path', @p, false);";
        var param = cmd.CreateParameter();
        param.ParameterName = "p";
        param.Value = _tenantContext.CurrentTenantPath ?? string.Empty;
        cmd.Parameters.Add(param);
        cmd.ExecuteNonQuery();
    }

    private async Task ApplyTenantPathAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_tenant_path', @p, false);";
        var param = cmd.CreateParameter();
        param.ParameterName = "p";
        param.Value = _tenantContext.CurrentTenantPath ?? string.Empty;
        cmd.Parameters.Add(param);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
