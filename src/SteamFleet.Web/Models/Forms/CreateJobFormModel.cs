using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;

namespace SteamFleet.Web.Models.Forms;

public sealed class CreateJobFormModel
{
    public JobType Type { get; set; } = JobType.SessionValidate;
    public bool DryRun { get; set; }
    public int Parallelism { get; set; } = 5;
    public int RetryCount { get; set; } = 2;
    public bool DeauthorizeAfterChange { get; set; }
    public int GeneratePasswordLength { get; set; } = 20;
    public string? FixedNewPassword { get; set; }
    public string? DisplayName { get; set; }
    public string? Summary { get; set; }
    public string? RealName { get; set; }
    public string? Country { get; set; }
    public string? State { get; set; }
    public string? City { get; set; }
    public string? CustomUrl { get; set; }
    public bool? ProfilePrivate { get; set; }
    public bool? FriendsPrivate { get; set; }
    public bool? InventoryPrivate { get; set; }
    public string? FolderName { get; set; }
    public string? TagsRaw { get; set; }
    public string? NoteText { get; set; }
    public string? AvatarBase64 { get; set; }
    public Guid? FamilyMainAccountId { get; set; }
    public List<FriendPairFormItem> FriendsPairs { get; set; } =
    [
        new FriendPairFormItem()
    ];
    public List<Guid> SelectedAccountIds { get; set; } = [];
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AccountOption> AvailableAccounts { get; set; } = [];
    public List<AccountOption> MainAccountOptions { get; set; } = [];

    public sealed record AccountOption(Guid Id, string LoginName, string? DisplayName, string Status);

    public sealed class FriendPairFormItem
    {
        public Guid? SourceAccountId { get; set; }
        public Guid? TargetAccountId { get; set; }
    }
}
