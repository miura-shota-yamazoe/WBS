using WbsApp.Models.Enums;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Models.Entities;

public sealed class TaskItem
{
    public Guid TaskId { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid? ParentTaskId { get; set; }
    public int SortOrder { get; set; }
    public required string Name { get; set; }
    public string? AssigneeName { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int Progress { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.NotStarted;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public string? Memo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Project Project { get; set; } = null!;
    public TaskItem? ParentTask { get; set; }
    public ICollection<TaskItem> ChildTasks { get; set; } = new List<TaskItem>();
}
