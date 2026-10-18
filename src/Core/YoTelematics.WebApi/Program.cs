using YoTelematics.Application.Common;
using YoTelematics.Infrastructure;
using YoTelematics.WebApi.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSignalR();

var appConn = builder.Configuration.GetConnectionString("AppConnection")
    ?? "Host=localhost;Port=5432;Database=yotelematics_db;Username=yoapp_user;Password=YoAppUserPass2026!";

builder.Services.AddScoped<ITenantContextAccessor, HeaderTenantContextAccessor>();
builder.Services.AddYoTelematicsInfrastructure(appConn);

var app = builder.Build();

app.UseAuthorization();
app.MapControllers();
app.MapHub<LiveTrackingHub>("/hubs/live-tracking");

app.Run();

public sealed class HeaderTenantContextAccessor : ITenantContextAccessor
{
    public Guid? CurrentTenantId { get; set; }
    public string? CurrentTenantPath { get; set; }
    public Guid? CurrentBranchId { get; set; }
}
