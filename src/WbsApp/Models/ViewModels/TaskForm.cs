using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using WbsApp.Models.Enums;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Models.ViewModels;

public sealed class TaskForm : IValidatableObject
{
    private string? _name;
    private string? _assigneeName;
    [Display(Name = "親タスク")]
    public Guid? ParentTaskId { get; set; }
    [Display(Name = "名前"), Required(ErrorMessage = "名前を入力してください。")]
    [StringLength(200, ErrorMessage = "名前は200文字以内で入力してください。")]
    public string? Name { get => _name; set => _name = value?.Trim(); }
    [Display(Name = "担当者")]
    [StringLength(100, ErrorMessage = "担当者は100文字以内で入力してください。")]
    public string? AssigneeName { get => _assigneeName; set => _assigneeName = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
    [Display(Name = "開始予定日"), Required(ErrorMessage = "開始予定日を入力してください。")]
    public DateOnly? StartDate { get; set; }
    [Display(Name = "終了予定日"), Required(ErrorMessage = "終了予定日を入力してください。")]
    public DateOnly? EndDate { get; set; }
    [Display(Name = "進捗率"), Required(ErrorMessage = "進捗率を入力してください。")]
    [Range(0, 100, ErrorMessage = "進捗率は0～100の整数で入力してください。")]
    public int? Progress { get; set; }
    [Display(Name = "ステータス"), Required(ErrorMessage = "ステータスを選択してください。")]
    [EnumDataType(typeof(TaskStatus), ErrorMessage = "ステータスの選択内容を確認してください。")]
    public TaskStatus? Status { get; set; }
    [Display(Name = "優先度"), Required(ErrorMessage = "優先度を選択してください。")]
    [EnumDataType(typeof(TaskPriority), ErrorMessage = "優先度の選択内容を確認してください。")]
    public TaskPriority? Priority { get; set; }
    [Display(Name = "備考"), StringLength(2000, ErrorMessage = "備考は2000文字以内で入力してください。")]
    public string? Memo { get; set; }

    [BindNever, ValidateNever] public Guid ProjectId { get; set; }
    [BindNever, ValidateNever] public Guid? TaskId { get; set; }
    [BindNever, ValidateNever] public bool WasCompleted { get; set; }
    [BindNever, ValidateNever] public string ProjectName { get; set; } = "";
    [BindNever, ValidateNever] public IReadOnlyList<TaskLink> ParentOptions { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate.HasValue && EndDate.HasValue && EndDate < StartDate)
            yield return new ValidationResult("終了予定日は開始予定日以降にしてください。", [nameof(EndDate)]);
    }
}

public sealed record TaskLink(Guid TaskId, string Name);
public sealed record TaskListRow(Guid TaskId, string Name, string? AssigneeName, DateOnly StartDate,
    DateOnly EndDate, int Progress, TaskStatus Status, TaskPriority Priority, string WbsNumber,
    int Depth, bool IsLeaf, bool IsDelayed);
public sealed record TaskList(Guid ProjectId, string ProjectName, DateOnly StartDate, DateOnly EndDate,
    ProjectStatus Status, decimal? Progress, IReadOnlyList<TaskListRow> Tasks);
