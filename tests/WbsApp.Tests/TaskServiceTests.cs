using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WbsApp.Data;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;
using WbsApp.Models.ViewModels;
using WbsApp.Services;
using Xunit;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Tests;

public sealed class TaskServiceTests
{
    internal static Project Project(string name = "検証プロジェクト") => new()
    {
        Name = name, StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 31),
        CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    };
    internal static TaskForm Input(Guid? parent = null) => new()
    {
        ParentTaskId = parent, Name = " 作業 ", AssigneeName = " 担当者 ", StartDate = new DateOnly(2026, 10, 2),
        EndDate = new DateOnly(2026, 10, 3), Progress = 25, Status = TaskStatus.InProgress,
        Priority = TaskPriority.High, Memo = "備考\n二行目"
    };
    internal static async Task<Guid> SeedProjectAsync(IsolatedAppFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var project = Project();
        db.Projects.Add(project); await db.SaveChangesAsync(); return project.ProjectId;
    }

    [Fact]
    public async Task RootsAndChildren_AppendToTheirOwnSiblingEndAndUpdateProject()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = (await db.Projects.AsNoTracking().SingleAsync()).UpdatedAt;
        var root1 = (await service.CreateAsync(projectId, Input()))!.Value;
        var root2 = (await service.CreateAsync(projectId, Input()))!.Value;
        var child1 = (await service.CreateAsync(projectId, Input(root1)))!.Value;
        var child2 = (await service.CreateAsync(projectId, Input(root1)))!.Value;
        var other = (await service.CreateAsync(projectId, Input(root2)))!.Value;
        var tasks = await db.Tasks.AsNoTracking().ToDictionaryAsync(value => value.TaskId);
        Assert.Equal(1, tasks[root1].SortOrder); Assert.Equal(2, tasks[root2].SortOrder);
        Assert.Equal(1, tasks[child1].SortOrder); Assert.Equal(2, tasks[child2].SortOrder); Assert.Equal(1, tasks[other].SortOrder);
        Assert.Equal(root1, tasks[child1].ParentTaskId);
        Assert.True((await db.Projects.AsNoTracking().SingleAsync()).UpdatedAt > before);
        var parentBefore = tasks[root1].UpdatedAt;
        var input = Input(root1); input.Name = "更新"; input.AssigneeName = "   "; input.Memo = "";
        Assert.True(await service.UpdateAsync(projectId, child1, input));
        var updated = await db.Tasks.AsNoTracking().SingleAsync(value => value.TaskId == child1);
        Assert.Equal(tasks[child1].CreatedAt, updated.CreatedAt); Assert.Equal(root1, updated.ParentTaskId);
        Assert.Equal(1, updated.SortOrder); Assert.Equal("更新", updated.Name); Assert.Null(updated.AssigneeName); Assert.Null(updated.Memo);
        Assert.True(updated.UpdatedAt > tasks[child1].UpdatedAt);
        Assert.Equal(parentBefore, (await db.Tasks.AsNoTracking().SingleAsync(value => value.TaskId == root1)).UpdatedAt);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public async Task AllStatusesAndPriorities_RoundTrip(int status, int priority)
    {
        await using var app = new IsolatedAppFactory(); var projectId = await SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var input = Input(); input.Status = (TaskStatus)status; input.Priority = (TaskPriority)priority; input.Progress = status == 3 ? 100 : 0;
        var id = await service.CreateAsync(projectId, input);
        var task = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync();
        Assert.Equal(id, task.TaskId); Assert.Equal(status, (int)task.Status); Assert.Equal(priority, (int)task.Priority);
        Assert.Equal("作業", task.Name); Assert.Equal("担当者", task.AssigneeName); Assert.Equal("備考\n二行目", task.Memo);
        Assert.Equal(DateTimeKind.Utc, task.CreatedAt.Kind); Assert.Equal(task.CreatedAt, task.UpdatedAt);
    }

    [Theory]
    [InlineData("name")] [InlineData("assignee")] [InlineData("memo")] [InlineData("date")]
    [InlineData("progress-low")] [InlineData("progress-high")] [InlineData("status")] [InlineData("priority")]
    [InlineData("completed")] [InlineData("hundred")]
    public async Task InvalidInput_IsRejectedWithoutChangingDatabase(string invalid)
    {
        await using var app = new IsolatedAppFactory(); var projectId = await SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var input = Input();
        switch (invalid)
        {
            case "name": input.Name = new string('あ', 201); break;
            case "assignee": input.AssigneeName = new string('あ', 101); break;
            case "memo": input.Memo = new string('あ', 2001); break;
            case "date": input.EndDate = input.StartDate!.Value.AddDays(-1); break;
            case "progress-low": input.Progress = -1; break;
            case "progress-high": input.Progress = 101; break;
            case "status": input.Status = (TaskStatus)99; break;
            case "priority": input.Priority = (TaskPriority)99; break;
            case "completed": input.Status = TaskStatus.Completed; break;
            case "hundred": input.Progress = 100; break;
        }
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(projectId, input));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task LengthBoundariesAndSameDate_AreAcceptedAfterTrimming()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var input = Input();
        input.Name = "  " + new string('名', 200) + "  "; input.AssigneeName = "  " + new string('担', 100) + "  ";
        input.Memo = new string('備', 2000); input.EndDate = input.StartDate;
        await scope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(projectId, input);
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync();
        Assert.Equal(200, saved.Name.Length); Assert.Equal(100, saved.AssigneeName!.Length); Assert.Equal(2000, saved.Memo!.Length);
    }

    [Fact]
    public async Task MissingOrCrossProjectParent_AndParentChanges_AreRejected()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var other = Project("別プロジェクト");
        db.Projects.Add(other); await db.SaveChangesAsync();
        var foreignParent = (await service.CreateAsync(other.ProjectId, Input()))!.Value;
        foreach (var parent in new[] { Guid.NewGuid(), foreignParent })
            await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(projectId, Input(parent)));
        var id = (await service.CreateAsync(projectId, Input()))!.Value;
        var child = (await service.CreateAsync(projectId, Input(id)))!.Value;
        foreach (var parent in new Guid?[] { id, child, foreignParent })
            await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(projectId, id, Input(parent)));
        Assert.Null((await db.Tasks.AsNoTracking().SingleAsync(value => value.TaskId == id)).ParentTaskId);
        Assert.False(await service.UpdateAsync(other.ProjectId, id, Input()));
        Assert.Null(await service.CreateAsync(Guid.NewGuid(), Input()));
    }

    [Fact]
    public async Task ProjectPeriodIsGuarded_ButParentPeriodDoesNotRestrictChildren()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var parentInput = Input(); parentInput.EndDate = parentInput.StartDate;
        var parent = (await service.CreateAsync(projectId, parentInput))!.Value;
        var child = Input(parent); child.EndDate = new DateOnly(2026, 10, 20);
        Assert.NotNull(await service.CreateAsync(projectId, child));
        child.EndDate = new DateOnly(2026, 11, 1);
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(projectId, child));
    }

    [Fact]
    public async Task SortOrderOverflow_IsRejectedWithoutWrites()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        await service.CreateAsync(projectId, Input());
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var task = await db.Tasks.SingleAsync(); task.SortOrder = int.MaxValue;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ValidationException>(() => service.CreateAsync(projectId, Input()));
        Assert.Single(await db.Tasks.AsNoTracking().ToArrayAsync());
    }
}
