using Hangfire;
using Hangfire.PostgreSql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using SteamFleet.Integrations.Steam.Extensions;
using SteamFleet.Persistence.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSteamFleetPersistence(builder.Configuration);
builder.Services.AddSteamGateway(builder.Configuration);

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

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = Math.Max(1, builder.Configuration.GetValue<int?>("WORKER_COUNT") ?? Environment.ProcessorCount);
    options.Queues = ["default"];
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("SteamFleet.Worker"))
    .WithTracing(tracing =>
    {
        tracing.AddConsoleExporter();
    });

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger);

var host = builder.Build();
await host.Services.EnsureSteamFleetDatabaseAsync();
await host.RunAsync();
