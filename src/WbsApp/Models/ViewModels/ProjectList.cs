using System.ComponentModel.DataAnnotations;
using WbsApp.Models.Enums;

namespace WbsApp.Models.ViewModels;

public sealed class ProjectSearch
{
    [Display(Name = "名前")]
    public string? Name { get; set; }
    [Display(Name = "状態")]
    [EnumDataType(typeof(ProjectStatus), ErrorMessage = "状態はすべて・進行中・完了から選択してください。")]
    public ProjectStatus? Status { get; set; }
    public int Page { get; set; } = 1;
}

public sealed record ProjectListItem(Guid ProjectId, string Name, DateOnly StartDate, DateOnly EndDate,
    ProjectStatus Status, DateTime UpdatedAt, decimal? Progress = null);

public sealed class ProjectList
{
    public required ProjectSearch Search { get; init; }
    public IReadOnlyList<ProjectListItem> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int PageCount => Math.Max(1, (TotalCount - 1) / 100 + 1);
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Search.Name) || Search.Status.HasValue;
}
