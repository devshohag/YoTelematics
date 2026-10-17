using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using YoTelematics.Application.Common;
using YoTelematics.Infrastructure.Persistence;

namespace YoTelematics.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddYoTelematicsInfrastructure(
        this IServiceCollection services,
        string appConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(appConnectionString);
        if (builder.NoResetOnClose)
        {
            throw new InvalidOperationException(
                "SECURITY VIOLATION: 'No Reset On Close=true' is strictly forbidden with RLS.");
        }

        services.AddScoped<TenantRlsConnectionInterceptor>();

        services.AddDbContext<YoTelematicsDbContext>((sp, options) =>
        {
            var rlsInterceptor = sp.GetRequiredService<TenantRlsConnectionInterceptor>();
            options.UseNpgsql(appConnectionString, npgsql =>
            {
                npgsql.UseNetTopologySuite();
            })
            .AddInterceptors(rlsInterceptor);
        });

        // Blocker B7 Fix: Persist DataProtection Key Ring to PostgreSQL data_protection_keys table
        services.AddDataProtection()
            .SetApplicationName("YoTelematics")
            .PersistKeysToDbContext<YoTelematicsDbContext>();

        return services;
    }
}
