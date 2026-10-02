using System.ComponentModel.DataAnnotations;
using WbsApp.Models.ViewModels;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Services;

public static class TaskProgressRules
{
    // Call only after input validation; use the persisted status, never posted metadata.
    public static (int Progress, TaskStatus Status) Normalize(TaskForm input, TaskStatus? previousStatus)
    {
        var progress = input.Progress!.Value;
        var status = input.Status!.Value;
        if (previousStatus == TaskStatus.Completed && status != TaskStatus.Completed && progress == 100)
            throw new ValidationException(new ValidationResult(
                "完了から再開するには、ステータスを変更し、進捗率を0～99%にしてください。",
                [nameof(TaskForm.Progress)]), null, null);
        if (status == TaskStatus.Completed) return (100, status);
        return progress == 100 ? (100, TaskStatus.Completed) : (progress, status);
    }

    public static bool IsDelayed(DateOnly endDate, TaskStatus status, DateOnly today) =>
        endDate < today && status != TaskStatus.Completed;
}
