using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WbsApp.Models.Enums;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Data.Configurations;

internal static class DatabaseValueConverters
{
    public static readonly ValueConverter<ProjectStatus, string> ProjectStatusConverter =
        new(value => WriteProjectStatus(value), value => ReadProjectStatus(value));
    public static readonly ValueConverter<TaskStatus, string> TaskStatusConverter =
        new(value => WriteTaskStatus(value), value => ReadTaskStatus(value));
    public static readonly ValueConverter<TaskPriority, string> TaskPriorityConverter =
        new(value => WriteTaskPriority(value), value => ReadTaskPriority(value));
    public static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(value => RequireUtc(value), value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTime RequireUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : throw new ArgumentException("保存日時にはUTCを指定してください。", nameof(value));

    private static string WriteProjectStatus(ProjectStatus value) => value switch
    {
        ProjectStatus.InProgress => "IN_PROGRESS",
        ProjectStatus.Completed => "COMPLETED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "未定義の状態です。")
    };

    private static ProjectStatus ReadProjectStatus(string value) => value switch
    {
        "IN_PROGRESS" => ProjectStatus.InProgress,
        "COMPLETED" => ProjectStatus.Completed,
        _ => throw new InvalidOperationException("DBに未定義のプロジェクト状態があります。")
    };

    private static string WriteTaskStatus(TaskStatus value) => value switch
    {
        TaskStatus.NotStarted => "NOT_STARTED",
        TaskStatus.InProgress => "IN_PROGRESS",
        TaskStatus.InReview => "IN_REVIEW",
        TaskStatus.Completed => "COMPLETED",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "未定義の状態です。")
    };

    private static TaskStatus ReadTaskStatus(string value) => value switch
    {
        "NOT_STARTED" => TaskStatus.NotStarted,
        "IN_PROGRESS" => TaskStatus.InProgress,
        "IN_REVIEW" => TaskStatus.InReview,
        "COMPLETED" => TaskStatus.Completed,
        _ => throw new InvalidOperationException("DBに未定義のタスク状態があります。")
    };

    private static string WriteTaskPriority(TaskPriority value) => value switch
    {
        TaskPriority.High => "HIGH",
        TaskPriority.Medium => "MEDIUM",
        TaskPriority.Low => "LOW",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "未定義の優先度です。")
    };

    private static TaskPriority ReadTaskPriority(string value) => value switch
    {
        "HIGH" => TaskPriority.High,
        "MEDIUM" => TaskPriority.Medium,
        "LOW" => TaskPriority.Low,
        _ => throw new InvalidOperationException("DBに未定義の優先度があります。")
    };
}
