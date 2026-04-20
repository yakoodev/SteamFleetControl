namespace SteamFleet.Contracts.Settings;

public sealed class UpdateOperationalSafetySettingsRequest
{
    public bool SafeModeEnabled { get; set; } = true;
    public bool BlockManualSensitiveDuringCooldown { get; set; }
    public int DefaultJobParallelism { get; set; } = 3;
    public int DefaultJobRetryCount { get; set; } = 1;
    public int MaxSensitiveParallelism { get; set; } = 2;
    public int MaxSensitiveAccountsPerJob { get; set; } = 50;
}

