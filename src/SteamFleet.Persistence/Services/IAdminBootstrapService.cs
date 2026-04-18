namespace SteamFleet.Persistence.Services;

public interface IAdminBootstrapService
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);
}
