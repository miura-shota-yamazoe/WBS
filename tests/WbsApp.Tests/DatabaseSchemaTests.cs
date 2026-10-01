using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WbsApp.Data;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;
using Xunit;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Tests;

public sealed class DatabaseSchemaTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=True");
    private AppDbContext _context = null!;
    private static readonly DateTime UtcTimestamp = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Migration_CreatesOnlyInitialBusinessTablesAndIsRepeatable()
    {
        var applied = (await _context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Single(applied);
        Assert.EndsWith("_InitialCreate", applied[0]);
        Assert.False(_context.Database.HasPendingModelChanges());
        var tables = await ReadColumnAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        Assert.Contains("projects", tables);
        Assert.Contains("tasks", tables);
        Assert.DoesNotContain("assignees", tables);
        Assert.All(tables, name => Assert.Contains(name, new[] { "projects", "tasks", "__EFMigrationsHistory", "__EFMigrationsLock" }));
        Assert.Empty(await _context.Projects.ToListAsync());
        Assert.Empty(await _context.Tasks.ToListAsync());
        var project = await AddProjectAsync();
        await _context.Database.MigrateAsync();
        Assert.Equal(project.ProjectId, (await _context.Projects.SingleAsync()).ProjectId);
        Assert.Equal(1L, await ScalarAsync("PRAGMA foreign_keys"));
    }

    [Theory]
    [InlineData(ProjectStatus.InProgress, "IN_PROGRESS")]
    [InlineData(ProjectStatus.Completed, "COMPLETED")]
    public async Task Project_RoundTripsUuidDatesUtcAndFixedStatus(ProjectStatus status, string dbValue)
    {
        var project = await AddProjectAsync(status);
        _context.ChangeTracker.Clear();
        var loaded = await _context.Projects.SingleAsync();
        Assert.NotEqual(Guid.Empty, loaded.ProjectId);
        Assert.Equal(project.ProjectId, loaded.ProjectId);
        Assert.Equal(new DateOnly(2026, 10, 1), loaded.StartDate);
        Assert.Equal(UtcTimestamp, loaded.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAt.Kind);
        Assert.Equal(status, loaded.Status);
        Assert.Equal(dbValue, await ScalarAsync("SELECT status FROM projects"));
        Assert.Equal("text", await ScalarAsync("SELECT typeof(project_id) FROM projects"));
    }

    [Theory]
    [InlineData(TaskStatus.NotStarted, "NOT_STARTED", TaskPriority.High, "HIGH", 0)]
    [InlineData(TaskStatus.InProgress, "IN_PROGRESS", TaskPriority.Medium, "MEDIUM", 25)]
    [InlineData(TaskStatus.InReview, "IN_REVIEW", TaskPriority.Low, "LOW", 75)]
    [InlineData(TaskStatus.Completed, "COMPLETED", TaskPriority.High, "HIGH", 100)]
    public async Task Task_RoundTripsFixedEnumsAndOptionalFields(TaskStatus status, string dbStatus,
        TaskPriority priority, string dbPriority, int progress)
    {
        var project = await AddProjectAsync();
        var task = new TaskItem
        {
            ProjectId = project.ProjectId, Name = "タスク", SortOrder = 1,
            StartDate = project.StartDate, EndDate = project.EndDate,
            Status = status, Priority = priority, Progress = progress,
            CreatedAt = UtcTimestamp, UpdatedAt = UtcTimestamp
        };
        _context.Tasks.Add(task);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var loaded = await _context.Tasks.SingleAsync();
        Assert.Equal(task.TaskId, loaded.TaskId);
        Assert.Equal(status, loaded.Status);
        Assert.Equal(priority, loaded.Priority);
        Assert.Equal(progress, loaded.Progress);
        Assert.Null(loaded.ParentTaskId);
        Assert.Null(loaded.AssigneeName);
        Assert.Null(loaded.Memo);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedAt.Kind);
        Assert.Equal(dbStatus, await ScalarAsync("SELECT status FROM tasks"));
        Assert.Equal(dbPriority, await ScalarAsync("SELECT priority FROM tasks"));
    }

    [Theory]
    [InlineData("end_date", "2026-09-30", "ck_projects_date_range")]
    [InlineData("status", "UNKNOWN", "ck_projects_status")]
    public async Task ProjectChecks_RejectInvalidSqlWrites(string column, object value, string constraint)
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(() => InsertProjectAsync(column, value));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains(constraint, exception.Message);
    }

    [Theory]
    [InlineData("end_date", "2026-09-30", "ck_tasks_date_range")]
    [InlineData("progress", -1, "ck_tasks_progress")]
    [InlineData("progress", 101, "ck_tasks_progress")]
    [InlineData("status", "UNKNOWN", "ck_tasks_status")]
    [InlineData("priority", "UNKNOWN", "ck_tasks_priority")]
    [InlineData("sort_order", 0, "ck_tasks_sort_order")]
    [InlineData("sort_order", -1, "ck_tasks_sort_order")]
    [InlineData("progress", 100, "ck_tasks_completion")]
    [InlineData("status", "COMPLETED", "ck_tasks_completion")]
    public async Task TaskChecks_RejectInvalidSqlWrites(string column, object value, string constraint)
    {
        var project = await AddProjectAsync();
        var exception = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(project.ProjectId, column, value));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains(constraint, exception.Message);
    }

    [Fact]
    public async Task SelfParent_IsRejectedByCheckConstraint()
    {
        var project = await AddProjectAsync();
        var id = Guid.NewGuid();
        var exception = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(project.ProjectId,
            "parent_task_id", id, taskId: id));
        Assert.Contains("ck_tasks_not_self_parent", exception.Message);
    }

    [Fact]
    public async Task RootOrder_IsUniquePerProjectEvenWithNullParent()
    {
        var firstProject = await AddProjectAsync();
        var secondProject = await AddProjectAsync();
        await InsertTaskAsync(firstProject.ProjectId);
        var exception = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(firstProject.ProjectId));
        Assert.Equal(2067, exception.SqliteExtendedErrorCode);
        await InsertTaskAsync(secondProject.ProjectId);
        Assert.Equal(2, await _context.Tasks.CountAsync());
    }

    [Fact]
    public async Task ChildOrder_IsUniquePerParentAndIndependentOfRootOrder()
    {
        var project = await AddProjectAsync();
        var firstParent = await InsertTaskAsync(project.ProjectId);
        var secondParent = await InsertTaskAsync(project.ProjectId, "sort_order", 2);
        await InsertTaskAsync(project.ProjectId, "parent_task_id", firstParent);
        var exception = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(project.ProjectId,
            "parent_task_id", firstParent));
        Assert.Equal(2067, exception.SqliteExtendedErrorCode);
        await InsertTaskAsync(project.ProjectId, "parent_task_id", secondParent);
        Assert.Equal(4, await _context.Tasks.CountAsync());
    }

    [Fact]
    public async Task MissingProject_IsRejectedByForeignKey()
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(Guid.NewGuid()));
        Assert.Equal(787, exception.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task MissingParent_IsRejectedByForeignKey()
    {
        var project = await AddProjectAsync();
        var exception = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(project.ProjectId,
            "parent_task_id", Guid.NewGuid()));
        Assert.Equal(787, exception.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task ParentDelete_IsRestrictedUntilChildIsDeleted()
    {
        var project = await AddProjectAsync();
        var parent = await InsertTaskAsync(project.ProjectId);
        var child = await InsertTaskAsync(project.ProjectId, "parent_task_id", parent);
        var exception = await Assert.ThrowsAsync<SqliteException>(() => DeleteTaskAsync(parent));
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(2, await _context.Tasks.CountAsync());
        await DeleteTaskAsync(child);
        await DeleteTaskAsync(parent);
        Assert.Empty(await _context.Tasks.ToListAsync());
    }

    [Fact]
    public async Task ProjectDelete_CascadesRootTasks()
    {
        var project = await AddProjectAsync();
        await InsertTaskAsync(project.ProjectId);
        _context.Projects.Remove(project);
        await _context.SaveChangesAsync();
        Assert.Empty(await _context.Tasks.ToListAsync());
    }

    [Fact]
    public async Task RawInsert_UsesDatabaseDefaults()
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects (project_id, name, start_date, end_date, created_at, updated_at)
            VALUES ($id, '既定値', '2026-10-01', '2026-10-31', '2026-10-01 00:00:00', '2026-10-01 00:00:00');
            INSERT INTO tasks (task_id, project_id, name, sort_order, start_date, end_date, created_at, updated_at)
            VALUES ($task, $id, '既定値タスク', 1, '2026-10-01', '2026-10-31', '2026-10-01 00:00:00', '2026-10-01 00:00:00');
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid());
        command.Parameters.AddWithValue("$task", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
        Assert.Equal("IN_PROGRESS", await ScalarAsync("SELECT status FROM projects"));
        Assert.Equal("NOT_STARTED", await ScalarAsync("SELECT status FROM tasks"));
        Assert.Equal("MEDIUM", await ScalarAsync("SELECT priority FROM tasks"));
        Assert.Equal(0L, await ScalarAsync("SELECT progress FROM tasks"));
    }

    [Fact]
    public async Task Name_IsNotNullableInEitherTable()
    {
        var projectException = await Assert.ThrowsAsync<SqliteException>(() => InsertProjectAsync("name", null));
        Assert.Equal(1299, projectException.SqliteExtendedErrorCode);
        var project = await AddProjectAsync();
        var taskException = await Assert.ThrowsAsync<SqliteException>(() => InsertTaskAsync(project.ProjectId, "name", null));
        Assert.Equal(1299, taskException.SqliteExtendedErrorCode);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task NonUtcTimestamp_IsRejectedBeforePersistence(DateTimeKind kind)
    {
        _context.Projects.Add(new Project
        {
            Name = "UTC検証", StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 31),
            CreatedAt = DateTime.SpecifyKind(UtcTimestamp, kind), UpdatedAt = UtcTimestamp
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
        Assert.Empty(await _context.Projects.AsNoTracking().ToListAsync());
    }

    private async Task<Project> AddProjectAsync(ProjectStatus status = ProjectStatus.InProgress)
    {
        var project = new Project
        {
            Name = "プロジェクト", StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 31),
            Status = status, CreatedAt = UtcTimestamp, UpdatedAt = UtcTimestamp
        };
        _context.Projects.Add(project);
        await _context.SaveChangesAsync();
        return project;
    }

    private async Task InsertProjectAsync(string column, object? value)
    {
        var values = new Dictionary<string, object?>
        {
            ["project_id"] = Guid.NewGuid(), ["name"] = "検証", ["start_date"] = "2026-10-01",
            ["end_date"] = "2026-10-31", ["status"] = "IN_PROGRESS",
            ["created_at"] = UtcTimestamp, ["updated_at"] = UtcTimestamp
        };
        values[column] = value;
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects (project_id, name, start_date, end_date, status, created_at, updated_at)
            VALUES ($project_id, $name, $start_date, $end_date, $status, $created_at, $updated_at)
            """;
        foreach (var (name, parameterValue) in values)
            command.Parameters.AddWithValue("$" + name, parameterValue ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> InsertTaskAsync(Guid projectId, string? column = null, object? value = null, Guid? taskId = null)
    {
        var id = taskId ?? Guid.NewGuid();
        var values = new Dictionary<string, object?>
        {
            ["task_id"] = id, ["project_id"] = projectId, ["parent_task_id"] = null,
            ["name"] = "検証タスク", ["sort_order"] = 1, ["start_date"] = "2026-10-01",
            ["end_date"] = "2026-10-31", ["progress"] = 0, ["status"] = "NOT_STARTED", ["priority"] = "MEDIUM",
            ["created_at"] = UtcTimestamp, ["updated_at"] = UtcTimestamp
        };
        if (column is not null) values[column] = value;
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tasks (task_id, project_id, parent_task_id, name, sort_order, start_date, end_date,
                progress, status, priority, created_at, updated_at)
            VALUES ($task_id, $project_id, $parent_task_id, $name, $sort_order, $start_date, $end_date,
                $progress, $status, $priority, $created_at, $updated_at)
            """;
        foreach (var (name, parameterValue) in values)
            command.Parameters.AddWithValue("$" + name, parameterValue ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task DeleteTaskAsync(Guid id)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM tasks WHERE task_id = $id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task<List<string>> ReadColumnAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }
}
