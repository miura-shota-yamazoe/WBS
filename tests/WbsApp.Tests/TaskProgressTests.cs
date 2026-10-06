using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WbsApp.Data;
using WbsApp.Infrastructure.Clock;
using WbsApp.Services;
using Xunit;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Tests;

public sealed class TaskProgressTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ExistingIncompleteTask_CanCompleteByProgressAlone(int initialStatus)
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var input = TaskServiceTests.Input(); input.Status = (TaskStatus)initialStatus;
        var id = (await service.CreateAsync(project, input))!.Value;
        input.Progress = 100;
        Assert.True(await service.UpdateAsync(project, id, input));
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync();
        Assert.Equal(100, saved.Progress); Assert.Equal(TaskStatus.Completed, saved.Status);
    }

    [Fact]
    public async Task TaskPage_ShowsDelayOnlyForOverdueIncompleteTasks()
    {
        await using var app = new IsolatedAppFactory();
        await using var host = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedClock(new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero)));
        }));
        Guid projectId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var project = TaskServiceTests.Project();
            db.Projects.Add(project); await db.SaveChangesAsync(); projectId = project.ProjectId;
            var service = scope.ServiceProvider.GetRequiredService<TaskService>();
            var input = TaskServiceTests.Input(); input.Name = "期限前日"; await service.CreateAsync(projectId, input);
            input.Name = "期限当日"; input.EndDate = new DateOnly(2026, 10, 4); await service.CreateAsync(projectId, input);
            input.Name = "完了済み"; input.EndDate = new DateOnly(2026, 10, 3); input.Status = TaskStatus.Completed;
            await service.CreateAsync(projectId, input);
        }
        using var client = host.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/Projects/{projectId}/Tasks"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, ">遅延</strong>"));
        Assert.Contains("100%", html); Assert.Contains("2026-10-04", html);
        Assert.Contains("期限前日", html); Assert.Contains("期限当日", html); Assert.Contains("完了済み", html);
    }

    [Theory]
    [InlineData(0, 100, 3, 100)]
    [InlineData(1, 100, 3, 100)]
    [InlineData(2, 100, 3, 100)]
    [InlineData(3, 0, 3, 100)]
    [InlineData(3, 25, 3, 100)]
    [InlineData(0, 25, 0, 25)]
    [InlineData(1, 99, 1, 99)]
    public async Task Create_NormalizesCompletionAfterValidation(int status, int progress, int savedStatus, int savedProgress)
    {
        await using var app = new IsolatedAppFactory();
        var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var input = TaskServiceTests.Input(); input.Status = (TaskStatus)status; input.Progress = progress;
        await scope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(project, input);
        var task = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync();
        Assert.Equal(savedStatus, (int)task.Status); Assert.Equal(savedProgress, task.Progress);
    }

    [Theory]
    [InlineData(-1)] [InlineData(101)]
    public async Task Completed_DoesNotHideInvalidProgress(int progress)
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var input = TaskServiceTests.Input(); input.Status = TaskStatus.Completed; input.Progress = progress;
        await Assert.ThrowsAsync<ValidationException>(() => scope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(project, input));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Reopening_RequiresBothFieldsAndDoesNotWriteOnError(int status)
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var input = TaskServiceTests.Input(); input.Status = TaskStatus.Completed;
        var id = (await service.CreateAsync(project, input))!.Value;
        var before = await db.Tasks.AsNoTracking().SingleAsync();
        var projectBefore = (await db.Projects.AsNoTracking().SingleAsync()).UpdatedAt;
        input.Status = (TaskStatus)status; input.Progress = 100; input.WasCompleted = false;
        var error = await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(project, id, input));
        Assert.Contains("Progress", error.ValidationResult!.MemberNames);
        Assert.Equal(before.UpdatedAt, (await db.Tasks.AsNoTracking().SingleAsync()).UpdatedAt);
        Assert.Equal(projectBefore, (await db.Projects.AsNoTracking().SingleAsync()).UpdatedAt);
        input.Progress = 99;
        Assert.True(await service.UpdateAsync(project, id, input));
        var saved = await db.Tasks.AsNoTracking().SingleAsync();
        Assert.Equal((TaskStatus)status, saved.Status); Assert.Equal(99, saved.Progress);
        Assert.True(saved.UpdatedAt > before.UpdatedAt);
    }

    [Fact]
    public async Task LoweringProgressAlone_KeepsCompletedAndParentIsIndependent()
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var input = TaskServiceTests.Input(); input.Status = TaskStatus.Completed;
        var parent = (await service.CreateAsync(project, input))!.Value;
        await service.CreateAsync(project, TaskServiceTests.Input(parent));
        input.Progress = 20;
        Assert.True(await service.UpdateAsync(project, parent, input));
        var saved = await db.Tasks.AsNoTracking().SingleAsync(value => value.TaskId == parent);
        Assert.Equal(100, saved.Progress); Assert.Equal(TaskStatus.Completed, saved.Status);
        Assert.Equal(25, (await db.Tasks.AsNoTracking().SingleAsync(value => value.ParentTaskId == parent)).Progress);
        Assert.Equal(WbsApp.Models.Enums.ProjectStatus.InProgress, (await db.Projects.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(-1, 0, true)] [InlineData(-1, 1, true)] [InlineData(-1, 2, true)] [InlineData(-1, 3, false)]
    [InlineData(0, 0, false)] [InlineData(1, 0, false)]
    public void Delay_UsesLocalTodayAndStrictlyEarlierDeadline(int dayOffset, int status, bool delayed)
    {
        var clock = new AppClock(new FixedClock(new DateTimeOffset(2026, 10, 1, 15, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 2), clock.Today);
        Assert.Equal(new DateTime(2026, 10, 1, 15, 30, 0, DateTimeKind.Utc), clock.UtcNow);
        Assert.Equal(delayed, TaskProgressRules.IsDelayed(clock.Today.AddDays(dayOffset), (TaskStatus)status, clock.Today));
    }

    [Fact]
    public void LocalMidnight_ChangesDelayWithoutChangingDeadline()
    {
        var deadline = new DateOnly(2026, 10, 1);
        var before = new AppClock(new FixedClock(new DateTimeOffset(2026, 10, 1, 14, 59, 59, TimeSpan.Zero)));
        var after = new AppClock(new FixedClock(new DateTimeOffset(2026, 10, 1, 15, 0, 0, TimeSpan.Zero)));
        Assert.False(TaskProgressRules.IsDelayed(deadline, TaskStatus.InProgress, before.Today));
        Assert.True(TaskProgressRules.IsDelayed(deadline, TaskStatus.InProgress, after.Today));
    }

    [Fact]
    public async Task List_DetectsParentAndChildDelayUsingInjectedClock()
    {
        await using var app = new IsolatedAppFactory(); var project = await TaskServiceTests.SeedProjectAsync(app);
        await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = new TaskService(db, new AppClock(new FixedClock(new DateTimeOffset(2026, 10, 4, 15, 0, 0, TimeSpan.Zero))),
            scope.ServiceProvider.GetRequiredService<DatabaseWriter>());
        var parent = (await service.CreateAsync(project, TaskServiceTests.Input()))!.Value;
        var child = (await service.CreateAsync(project, TaskServiceTests.Input(parent)))!.Value;
        var completed = TaskServiceTests.Input(); completed.Status = TaskStatus.Completed;
        var done = (await service.CreateAsync(project, completed))!.Value;
        var list = (await service.ListAsync(project))!;
        Assert.True(list.Tasks.Single(value => value.TaskId == parent).IsDelayed);
        Assert.True(list.Tasks.Single(value => value.TaskId == child).IsDelayed);
        Assert.False(list.Tasks.Single(value => value.TaskId == done).IsDelayed);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), value => value.State != EntityState.Unchanged);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("TestJapan", TimeSpan.FromHours(9), "TestJapan", "TestJapan");
    }
}
