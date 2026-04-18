using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using SteamFleet.Integrations.Steam.Extensions;
using SteamFleet.Persistence;
using SteamFleet.Persistence.Extensions;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Identity;
using SteamFleet.Persistence.Services;
using SteamFleet.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfig) =>
{
    loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console();
});

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
})
.AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSteamFleetPersistence(builder.Configuration);
builder.Services.AddSteamGateway(builder.Configuration);
builder.Services
    .AddIdentity<AppUser, AppRole>(options =>
    {
        options.SignIn.RequireConfirmedEmail = false;
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<SteamFleetDbContext>()
    .AddDefaultTokenProviders();
builder.Services.AddScoped<IAdminBootstrapService, AdminBootstrapService>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "steamfleet.auth";
    options.Cookie.HttpOnly = true;
    options.SlidingExpiration = true;
    options.LoginPath = "/auth/login";
    options.AccessDeniedPath = "/auth/denied";
});

var hangfireConnection = builder.Configuration.GetConnectionString("Postgres")
                       ?? builder.Configuration["POSTGRES_CONNECTION"]
                       ?? throw new InvalidOperationException("Postgres connection is missing.");

builder.Services.AddHangfire(configuration =>
{
    configuration
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(hangfireConnection));
});

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString("Redis")
                            ?? builder.Configuration["REDIS_CONNECTION"]
                            ?? "redis:6379";
    options.InstanceName = "steamfleet:";
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CanManageAccounts", policy => policy.RequireRole(Roles.SuperAdmin, Roles.Admin));
    options.AddPolicy("CanViewAudit", policy => policy.RequireRole(Roles.SuperAdmin, Roles.Admin, Roles.Auditor));
    options.AddPolicy("CanOperate", policy => policy.RequireRole(Roles.SuperAdmin, Roles.Admin, Roles.Operator));
    options.AddPolicy("AdminOrSuperAdmin", policy => policy.RequireRole(Roles.SuperAdmin, Roles.Admin));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("sensitive", context =>
    {
        var key = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("SteamFleet.Web"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddConsoleExporter();
    });

var app = builder.Build();

await app.Services.EnsureSteamFleetDatabaseAsync();

app.UseSerilogRequestLogging();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase),
    branch =>
    {
        branch.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                await context.ChallengeAsync();
                return;
            }

            var isAllowed = context.User.IsInRole(Roles.SuperAdmin) || context.User.IsInRole(Roles.Admin);
            if (!isAllowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden");
                return;
            }

            await next();
        });
    });

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization =
    [
        new RoleBasedHangfireAuthorizationFilter(Roles.SuperAdmin, Roles.Admin)
    ]
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Accounts}/{action=Index}/{id?}");
app.MapGet("/", () => Results.Redirect("/auth/login", permanent: false)).AllowAnonymous();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "SteamFleet.Web" })).AllowAnonymous();

app.Run();
