using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace SteamFleet.Web.Models.Forms;

public sealed class CreateJobFormModel : IValidatableObject
{
    [Display(Name = "Тип задачи")]
    public JobType Type { get; set; } = JobType.SessionValidate;

    [Display(Name = "Пробный запуск (dry-run)")]
    public bool DryRun { get; set; }

    [Range(1, 50, ErrorMessage = "Parallelism должен быть от 1 до 50.")]
    [Display(Name = "Параллелизм")]
    public int Parallelism { get; set; } = 5;

    [Range(0, 10, ErrorMessage = "Retry должен быть от 0 до 10.")]
    [Display(Name = "Повторы (retry)")]
    public int RetryCount { get; set; } = 2;

    [Display(Name = "После смены пароля завершить все сессии")]
    public bool DeauthorizeAfterChange { get; set; }

    [Range(12, 64, ErrorMessage = "Длина пароля должна быть от 12 до 64.")]
    [Display(Name = "Длина генерируемого пароля")]
    public int GeneratePasswordLength { get; set; } = 20;

    [Display(Name = "Фиксированный новый пароль")]
    public string? FixedNewPassword { get; set; }

    [Display(Name = "Новое имя профиля")]
    public string? DisplayName { get; set; }

    [Display(Name = "Описание профиля")]
    public string? Summary { get; set; }

    [Display(Name = "Реальное имя")]
    public string? RealName { get; set; }

    [Display(Name = "Страна")]
    public string? Country { get; set; }

    [Display(Name = "Регион/штат")]
    public string? State { get; set; }

    [Display(Name = "Город")]
    public string? City { get; set; }

    [Display(Name = "Кастомный URL")]
    public string? CustomUrl { get; set; }

    [Display(Name = "Сделать профиль приватным")]
    public bool? ProfilePrivate { get; set; }

    [Display(Name = "Сделать список друзей приватным")]
    public bool? FriendsPrivate { get; set; }

    [Display(Name = "Сделать инвентарь приватным")]
    public bool? InventoryPrivate { get; set; }

    [Display(Name = "Папка назначения")]
    public string? FolderName { get; set; }

    [Display(Name = "Теги")]
    public string? TagsRaw { get; set; }

    [Display(Name = "Заметка")]
    public string? NoteText { get; set; }

    [Display(Name = "Аватар в Base64")]
    public string? AvatarBase64 { get; set; }

    [Display(Name = "Ссылка на изображение аватара")]
    public string? AvatarUrl { get; set; }

    [Display(Name = "Файл аватара")]
    public IFormFile? AvatarFile { get; set; }

    [Display(Name = "Главный аккаунт")]
    public Guid? FamilyMainAccountId { get; set; }

    public List<FriendPairFormItem> FriendsPairs { get; set; } =
    [
        new FriendPairFormItem()
    ];

    [Display(Name = "Выбранные аккаунты")]
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var requiresAccounts = Type is not JobType.FriendsAddByInvite and not JobType.FriendsConnectFamilyMain;
        if (requiresAccounts && SelectedAccountIds.Count == 0)
        {
            yield return new ValidationResult(
                "Выберите хотя бы один аккаунт для выполнения задачи.",
                [nameof(SelectedAccountIds)]);
        }

        switch (Type)
        {
            case JobType.ProfileUpdate:
                var hasProfileChange =
                    !string.IsNullOrWhiteSpace(DisplayName) ||
                    !string.IsNullOrWhiteSpace(Summary) ||
                    !string.IsNullOrWhiteSpace(RealName) ||
                    !string.IsNullOrWhiteSpace(Country) ||
                    !string.IsNullOrWhiteSpace(State) ||
                    !string.IsNullOrWhiteSpace(City) ||
                    !string.IsNullOrWhiteSpace(CustomUrl);
                if (!hasProfileChange)
                {
                    yield return new ValidationResult(
                        "Для обновления профиля заполните хотя бы одно поле (имя, био, real name, локация или custom URL).",
                        [nameof(DisplayName), nameof(Summary), nameof(RealName), nameof(Country), nameof(State), nameof(City), nameof(CustomUrl)]);
                }

                break;
            case JobType.PrivacyUpdate:
                if (ProfilePrivate is null && FriendsPrivate is null && InventoryPrivate is null)
                {
                    yield return new ValidationResult(
                        "Для приватности нужно выбрать хотя бы один переключатель (профиль/друзья/инвентарь).",
                        [nameof(ProfilePrivate), nameof(FriendsPrivate), nameof(InventoryPrivate)]);
                }

                break;
            case JobType.AvatarUpdate:
                var hasAvatarInput =
                    AvatarFile is not null ||
                    !string.IsNullOrWhiteSpace(AvatarUrl) ||
                    !string.IsNullOrWhiteSpace(AvatarBase64);
                if (!hasAvatarInput)
                {
                    yield return new ValidationResult(
                        "Для смены аватара загрузите файл или укажите ссылку на изображение.",
                        [nameof(AvatarFile), nameof(AvatarUrl)]);
                }

                break;
            case JobType.TagsAssign:
                if (string.IsNullOrWhiteSpace(TagsRaw))
                {
                    yield return new ValidationResult(
                        "Укажите хотя бы один тег.",
                        [nameof(TagsRaw)]);
                }

                break;
            case JobType.GroupMove:
                if (string.IsNullOrWhiteSpace(FolderName))
                {
                    yield return new ValidationResult(
                        "Укажите папку назначения.",
                        [nameof(FolderName)]);
                }

                break;
            case JobType.AddNote:
                if (string.IsNullOrWhiteSpace(NoteText))
                {
                    yield return new ValidationResult(
                        "Введите текст заметки.",
                        [nameof(NoteText)]);
                }

                break;
            case JobType.FriendsAddByInvite:
                if (!FriendsPairs.Any(x => x.SourceAccountId is not null && x.TargetAccountId is not null && x.SourceAccountId != x.TargetAccountId))
                {
                    yield return new ValidationResult(
                        "Добавьте хотя бы одну корректную пару source -> target.",
                        [nameof(FriendsPairs)]);
                }

                break;
            case JobType.FriendsConnectFamilyMain:
                if (FamilyMainAccountId is null)
                {
                    yield return new ValidationResult(
                        "Выберите главный аккаунт семейной группы.",
                        [nameof(FamilyMainAccountId)]);
                }

                break;
        }
    }
}
