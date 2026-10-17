using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YoTelematics.Application.Common;
using YoTelematics.Infrastructure.Persistence;

namespace YoTelematics.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddYoTelematicsPersistence(
        this IServiceCollection services,
        string appConnectionString)
    {
        if (appConnectionString.Contains("No Reset On Close=true", StringComparison.OrdinalIgnoreCase) ||
            appConnectionString.Contains("NoResetOnClose=true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Security violation (v3.4-FINAL): 'No Reset On Close=true' is strictly prohibited on RLS-protected connections.");
        }

        services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
        services.AddSingleton<TenantRlsConnectionInterceptor>();

        services.AddDbContext<YoTelematicsDbContext>((sp, options) =>
        {
            options.UseNpgsql(appConnectionString, npgsql => npgsql.UseNetTopologySuite());
            options.AddInterceptors(sp.GetRequiredService<TenantRlsConnectionInterceptor>());
        });

        return services;
    }
}
