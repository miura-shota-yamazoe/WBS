using System.ComponentModel.DataAnnotations;
using WbsApp.Models.Enums;

namespace WbsApp.Models.ViewModels;

public sealed class ProjectForm : IValidatableObject
{
    private string? _name;
    [Display(Name = "名前")]
    [Required(ErrorMessage = "名前を入力してください。")]
    [StringLength(100, ErrorMessage = "名前は100文字以内で入力してください。")]
    public string? Name { get => _name; set => _name = value?.Trim(); }

    [Display(Name = "説明")]
    [StringLength(1000, ErrorMessage = "説明は1000文字以内で入力してください。")]
    public string? Description { get; set; }

    [Display(Name = "開始予定日")]
    [Required(ErrorMessage = "開始予定日を入力してください。")]
    public DateOnly? StartDate { get; set; }

    [Display(Name = "終了予定日")]
    [Required(ErrorMessage = "終了予定日を入力してください。")]
    public DateOnly? EndDate { get; set; }

    [Display(Name = "状態")]
    [Required(ErrorMessage = "状態を選択してください。")]
    [EnumDataType(typeof(ProjectStatus), ErrorMessage = "状態は進行中または完了を選択してください。")]
    public ProjectStatus? Status { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (StartDate.HasValue && EndDate.HasValue && EndDate < StartDate)
            yield return new ValidationResult("終了予定日は開始予定日以降にしてください。", [nameof(EndDate)]);
    }
}
