using System.ComponentModel.DataAnnotations;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Models.ViewModels;

public sealed class TaskSearch
{
    [Display(Name = "タスク名")]
    public string? Name { get; set; }
    [Display(Name = "ステータス")]
    [EnumDataType(typeof(TaskStatus), ErrorMessage = "ステータスの検索条件を確認してください。")]
    public TaskStatus? Status { get; set; }
    [Display(Name = "遅延のみ")]
    public bool DelayedOnly { get; set; }
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Name) || Status.HasValue || DelayedOnly;
}
