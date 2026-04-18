var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "SteamFleet.Api" }));
app.MapControllers();

app.Run();
