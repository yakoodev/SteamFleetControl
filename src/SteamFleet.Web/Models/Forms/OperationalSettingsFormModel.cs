using System.ComponentModel.DataAnnotations;

namespace SteamFleet.Web.Models.Forms;

public sealed class OperationalSettingsFormModel
{
    [Display(Name = "Включить безопасный режим")]
    public bool SafeModeEnabled { get; set; } = true;

    [Display(Name = "Блокировать ручные sensitive-операции в cooldown")]
    public bool BlockManualSensitiveDuringCooldown { get; set; }

    [Range(1, 50, ErrorMessage = "Значение должно быть в диапазоне 1-50.")]
    [Display(Name = "Параллелизм по умолчанию для задач")]
    public int DefaultJobParallelism { get; set; } = 3;

    [Range(0, 10, ErrorMessage = "Значение должно быть в диапазоне 0-10.")]
    [Display(Name = "Retry по умолчанию для задач")]
    public int DefaultJobRetryCount { get; set; } = 1;

    [Range(1, 20, ErrorMessage = "Значение должно быть в диапазоне 1-20.")]
    [Display(Name = "Максимальный параллелизм sensitive-задач")]
    public int MaxSensitiveParallelism { get; set; } = 2;

    [Range(1, 1000, ErrorMessage = "Значение должно быть в диапазоне 1-1000.")]
    [Display(Name = "Максимум аккаунтов в sensitive batch")]
    public int MaxSensitiveAccountsPerJob { get; set; } = 50;

    public DateTimeOffset? UpdatedAt { get; set; }
}

