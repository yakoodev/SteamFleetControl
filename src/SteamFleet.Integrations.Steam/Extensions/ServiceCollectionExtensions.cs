using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SteamFleet.Contracts.Steam;
using SteamFleet.Integrations.Steam.Options;

namespace SteamFleet.Integrations.Steam.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSteamGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SteamGatewayOptions>(configuration.GetSection("SteamGateway"));
        services.AddSingleton<ISteamAccountGateway, SteamKitGateway>();
        return services;
    }
}
