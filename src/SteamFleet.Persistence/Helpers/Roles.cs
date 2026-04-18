using SteamFleet.Contracts.Enums;

namespace SteamFleet.Persistence.Helpers;

public static class Roles
{
    public const string SuperAdmin = nameof(SuperAdmin);
    public const string Admin = nameof(Admin);
    public const string Operator = nameof(Operator);
    public const string Auditor = nameof(Auditor);

    public static readonly string[] All = [SuperAdmin, Admin, Operator, Auditor];
}
