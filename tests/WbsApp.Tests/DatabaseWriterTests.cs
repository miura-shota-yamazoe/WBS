using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WbsApp.Data;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;
using WbsApp.Models.ViewModels;
using WbsApp.Services;
using Xunit;

namespace WbsApp.Tests;

public sealed class DatabaseWriterTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskItem TaskItem(Guid project, int order = 1) => new()
    {
        ProjectId = project, Name = "追加", SortOrder = order,
        StartDate = new DateOnly(2026, 10, 2), EndDate = new DateOnly(2026, 10, 3),
        CreatedAt = DateTime.UnixEpoch, UpdatedAt = DateTime.UnixEpoch
    };

    [Fact]
    public async Task ParallelTaskCreation_UsesUniqueContiguousSiblingOrdersAcrossScopes()
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        Guid parent;
        await using (var scope = app.Services.CreateAsyncScope())
            parent = (await scope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(project, TaskServiceTests.Input()))!.Value;
        var start = Signal();
        var writes = Enumerable.Range(0, 24).Select(async index =>
        {
            await using var scope = app.Services.CreateAsyncScope(); await start.Task;
            var input = TaskServiceTests.Input(index % 2 == 0 ? parent : null); input.Name = $"並行{index}";
            return await scope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(project, input);
        }).ToArray();
        start.SetResult(); await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(30));
        await using var finalScope = app.Services.CreateAsyncScope(); var db = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tasks = await db.Tasks.AsNoTracking().ToListAsync(); Assert.Equal(25, tasks.Count);
        Assert.Equal(Enumerable.Range(1, 12), tasks.Where(task => task.ParentTaskId == parent).OrderBy(task => task.SortOrder).Select(task => task.SortOrder));
        Assert.Equal(Enumerable.Range(1, 13), tasks.Where(task => task.ParentTaskId is null).OrderBy(task => task.SortOrder).Select(task => task.SortOrder));
        var updated = (await db.Projects.AsNoTracking().SingleAsync()).UpdatedAt;
        Assert.True(updated >= tasks.Max(task => task.UpdatedAt));
    }

    [Theory]
    [InlineData("exception")][InlineData("database")][InlineData("cancel")]
    public async Task FailureAfterIntermediateSave_RollsBackAndAllowsNextWrite(string failure)
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = (await db.Projects.AsNoTracking().SingleAsync()).UpdatedAt;
        var writer = scope.ServiceProvider.GetRequiredService<DatabaseWriter>(); using var cancellation = new CancellationTokenSource();
        var failing = writer.ExecuteAsync(async token =>
        {
            var savedProject = await db.Projects.SingleAsync(token); savedProject.UpdatedAt = savedProject.UpdatedAt.AddDays(1);
            db.Tasks.Add(TaskItem(project)); await db.SaveChangesAsync(token);
            if (failure == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (failure == "exception") throw new InvalidOperationException("途中失敗");
            db.Tasks.Add(TaskItem(project)); // Duplicate root SortOrder fails the final save.
            return true;
        }, cancellation.Token);
        if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failing);
        else if (failure == "exception") await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        else await Assert.ThrowsAsync<DbUpdateException>(() => failing);
        Assert.Null(db.Database.CurrentTransaction); Assert.Empty(db.ChangeTracker.Entries());
        Assert.Empty(await db.Tasks.AsNoTracking().ToArrayAsync());
        Assert.Equal(before, (await db.Projects.AsNoTracking().SingleAsync()).UpdatedAt);
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(project, TaskServiceTests.Input()));
        Assert.Single(await db.Tasks.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task CanceledWaiter_DoesNotReleaseTheActiveWriterGate()
    {
        await using var app = new IsolatedAppFactory(); await TaskServiceTests.SeedProjectAsync(app);
        await using var firstScope = app.Services.CreateAsyncScope();
        await using var secondScope = app.Services.CreateAsyncScope();
        await using var thirdScope = app.Services.CreateAsyncScope();
        var entered = Signal(); var release = Signal();
        var first = firstScope.ServiceProvider.GetRequiredService<DatabaseWriter>().ExecuteAsync(async token =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token); return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var second = secondScope.ServiceProvider.GetRequiredService<DatabaseWriter>().ExecuteAsync(_ => Task.FromResult(true), cancellation.Token);
            Assert.False(second.IsCompleted); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            var third = thirdScope.ServiceProvider.GetRequiredService<DatabaseWriter>().ExecuteAsync(_ => Task.FromResult(true));
            Assert.False(third.IsCompleted);
            release.SetResult(); await first; Assert.True(await third.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task TaskCreate_WaitsAndValidatesLatestProjectPeriodDespiteEarlierTrackedRead()
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var writeScope = app.Services.CreateAsyncScope(); await using var waitingScope = app.Services.CreateAsyncScope();
        var waitingDb = waitingScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await waitingDb.Projects.SingleAsync(); // Old period must not survive waiting.
        var writeDb = writeScope.ServiceProvider.GetRequiredService<AppDbContext>(); var entered = Signal(); var release = Signal();
        var first = writeScope.ServiceProvider.GetRequiredService<DatabaseWriter>().ExecuteAsync(async token =>
        {
            (await writeDb.Projects.SingleAsync(token)).EndDate = new DateOnly(2026, 10, 2);
            await writeDb.SaveChangesAsync(token); entered.SetResult(); await release.Task.WaitAsync(token); return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var second = waitingScope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(project, TaskServiceTests.Input());
            Assert.False(second.IsCompleted); release.SetResult(); await first;
            await Assert.ThrowsAsync<ValidationException>(() => second);
            Assert.Empty(await waitingDb.Tasks.AsNoTracking().ToArrayAsync());
            Assert.Equal(new DateOnly(2026, 10, 2), (await waitingDb.Projects.AsNoTracking().SingleAsync()).EndDate);
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task ProjectUpdate_WaitsThenSeesNewIncompleteTask()
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var firstScope = app.Services.CreateAsyncScope(); await using var secondScope = app.Services.CreateAsyncScope();
        var db = firstScope.ServiceProvider.GetRequiredService<AppDbContext>(); var entered = Signal(); var release = Signal();
        var first = firstScope.ServiceProvider.GetRequiredService<DatabaseWriter>().ExecuteAsync(async token =>
        {
            db.Tasks.Add(TaskItem(project)); await db.SaveChangesAsync(token);
            entered.SetResult(); await release.Task.WaitAsync(token); return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var second = secondScope.ServiceProvider.GetRequiredService<ProjectService>().UpdateAsync(project, new ProjectForm
            {
                Name = "変更", StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 31), Status = ProjectStatus.Completed
            });
            Assert.False(second.IsCompleted); release.SetResult(); await first;
            await Assert.ThrowsAsync<ValidationException>(() => second);
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task NestedTransaction_IsRejectedWithoutDeadlockAndNextWriteWorks()
    {
        await using var app = new IsolatedAppFactory(); await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var writer = scope.ServiceProvider.GetRequiredService<DatabaseWriter>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ExecuteAsync(_ => writer.ExecuteAsync(_ => Task.FromResult(true))));
        Assert.True(await writer.ExecuteAsync(_ => Task.FromResult(true)));
    }

    [Fact]
    public async Task PendingChangesOutsideWriter_AreNotSilentlyDiscardedOrCommitted()
    {
        await using var app = new IsolatedAppFactory(); await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Projects.SingleAsync()).Name = "未保存";
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<DatabaseWriter>().ExecuteAsync(_ => Task.FromResult(true)));
        Assert.True(db.ChangeTracker.HasChanges());
        Assert.NotEqual("未保存", (await db.Projects.AsNoTracking().SingleAsync()).Name);
    }
}
