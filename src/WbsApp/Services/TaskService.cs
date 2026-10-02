using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using WbsApp.Data;
using WbsApp.Infrastructure.Clock;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;
using WbsApp.Models.ViewModels;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Services;

public sealed class TaskService(AppDbContext db, AppClock clock)
{
    public async Task<TaskList?> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var name = await db.Projects.Where(value => value.ProjectId == projectId).Select(value => value.Name).SingleOrDefaultAsync(cancellationToken);
        if (name is null) return null;
        var tasks = await db.Tasks.AsNoTracking().Where(value => value.ProjectId == projectId)
            .OrderBy(value => value.CreatedAt).ThenBy(value => value.TaskId)
            .Select(value => new { value.TaskId, value.Name, value.Progress, value.Status, value.EndDate }).ToListAsync(cancellationToken);
        var today = clock.Today;
        return new TaskList(projectId, name, tasks.Select(value => new TaskListRow(value.TaskId, value.Name,
            value.Progress, value.Status, value.EndDate, TaskProgressRules.IsDelayed(value.EndDate, value.Status, today))).ToArray());
    }

    public async Task<TaskForm?> FormAsync(Guid projectId, Guid? taskId = null, TaskForm? input = null,
        Guid? parentId = null, CancellationToken cancellationToken = default)
    {
        var list = await ListAsync(projectId, cancellationToken);
        if (list is null) return null;
        if (taskId.HasValue)
        {
            var task = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(value => value.ProjectId == projectId && value.TaskId == taskId, cancellationToken);
            if (task is null) return null;
            input ??= new TaskForm
            {
                ParentTaskId = task.ParentTaskId, Name = task.Name, AssigneeName = task.AssigneeName,
                StartDate = task.StartDate, EndDate = task.EndDate, Progress = task.Progress,
                Status = task.Status, Priority = task.Priority, Memo = task.Memo
            };
            input.WasCompleted = task.Status == TaskStatus.Completed;
        }
        else if (input is null)
        {
            if (parentId.HasValue && !list.Tasks.Any(value => value.TaskId == parentId)) return null;
            input = new TaskForm { ParentTaskId = parentId, Progress = 0, Status = TaskStatus.NotStarted, Priority = TaskPriority.Medium };
        }
        input.ProjectId = projectId;
        input.ProjectName = list.ProjectName;
        input.TaskId = taskId;
        input.ParentOptions = list.Tasks.Where(value => !taskId.HasValue || value.TaskId == input.ParentTaskId)
            .Select(value => new TaskLink(value.TaskId, value.Name)).ToArray();
        return input;
    }

    public async Task<Guid?> CreateAsync(Guid projectId, TaskForm input, CancellationToken cancellationToken = default)
    {
        Validator.ValidateObject(input, new ValidationContext(input), true);
        var project = await db.Projects.SingleOrDefaultAsync(value => value.ProjectId == projectId, cancellationToken);
        if (project is null) return null;
        if (input.ParentTaskId.HasValue && !await db.Tasks.AnyAsync(value => value.ProjectId == projectId && value.TaskId == input.ParentTaskId, cancellationToken))
            throw Invalid(nameof(TaskForm.ParentTaskId), "同じプロジェクトの既存タスクを親に選択してください。");
        CheckPeriod(project, input);
        var last = await db.Tasks.Where(value => value.ProjectId == projectId && value.ParentTaskId == input.ParentTaskId)
            .Select(value => (int?)value.SortOrder).MaxAsync(cancellationToken) ?? 0;
        if (last == int.MaxValue) throw Invalid("", "これ以上タスクを追加できません。表示順を確認してください。");
        var normalized = TaskProgressRules.Normalize(input, null);
        var now = clock.UtcNow;
        var task = new TaskItem { ProjectId = projectId, ParentTaskId = input.ParentTaskId,
            Name = input.Name!, SortOrder = last + 1, CreatedAt = now, UpdatedAt = now };
        Apply(task, input, normalized);
        project.UpdatedAt = NextTimestamp(project.UpdatedAt, now);
        db.Tasks.Add(task);
        // One SaveChanges makes task and project writes atomic; process-wide serialization is T15a.
        await db.SaveChangesAsync(cancellationToken);
        return task.TaskId;
    }

    public async Task<bool> UpdateAsync(Guid projectId, Guid taskId, TaskForm input, CancellationToken cancellationToken = default)
    {
        Validator.ValidateObject(input, new ValidationContext(input), true);
        var task = await db.Tasks.Include(value => value.Project).SingleOrDefaultAsync(value => value.ProjectId == projectId && value.TaskId == taskId, cancellationToken);
        if (task is null) return false;
        if (input.ParentTaskId != task.ParentTaskId)
            throw Invalid(nameof(TaskForm.ParentTaskId), "編集時の親タスクは変更できません。");
        if (input.StartDate != task.StartDate || input.EndDate != task.EndDate) CheckPeriod(task.Project, input);
        var normalized = TaskProgressRules.Normalize(input, task.Status);
        var now = clock.UtcNow;
        Apply(task, input, normalized);
        task.UpdatedAt = NextTimestamp(task.UpdatedAt, now);
        task.Project.UpdatedAt = NextTimestamp(task.Project.UpdatedAt, now);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return false; }
        return true;
    }

    private static ValidationException Invalid(string field, string message) =>
        new(new ValidationResult(message, string.IsNullOrEmpty(field) ? [] : [field]), null, null);
    private static void CheckPeriod(Project project, TaskForm input)
    {
        if (input.StartDate < project.StartDate || input.EndDate > project.EndDate)
            throw Invalid("", "プロジェクト期間外の日付は確認が必要です。日付を確認してください。");
    }
    private static DateTime NextTimestamp(DateTime previous, DateTime now) => now > previous ? now : previous.AddTicks(1);
    private static void Apply(TaskItem task, TaskForm input, (int Progress, TaskStatus Status) normalized)
    {
        task.Name = input.Name!; task.AssigneeName = input.AssigneeName;
        task.StartDate = input.StartDate!.Value; task.EndDate = input.EndDate!.Value;
        task.Progress = normalized.Progress; task.Status = normalized.Status;
        task.Priority = input.Priority!.Value; task.Memo = string.IsNullOrEmpty(input.Memo) ? null : input.Memo;
    }
}
