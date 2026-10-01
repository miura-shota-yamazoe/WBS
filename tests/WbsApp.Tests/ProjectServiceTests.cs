using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WbsApp.Data;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;
using WbsApp.Models.ViewModels;
using WbsApp.Services;
using Xunit;

namespace WbsApp.Tests;

public sealed class ProjectServiceTests
{
    private static ProjectForm Input() => new()
    {
        Name = "サービスから登録", StartDate = new DateOnly(2026, 10, 1),
        EndDate = new DateOnly(2026, 10, 31), Status = ProjectStatus.InProgress
    };

    [Theory]
    [InlineData("name")]
    [InlineData("date")]
    [InlineData("status")]
    public async Task InvalidServiceInput_IsRejectedWithoutWriting(string invalid)
    {
        await using var app = new IsolatedAppFactory();
        await using var scope = app.Services.CreateAsyncScope();
        var input = Input();
        if (invalid == "name") input.Name = "   ";
        if (invalid == "date") input.EndDate = input.StartDate!.Value.AddDays(-1);
        if (invalid == "status") input.Status = (ProjectStatus)99;
        await Assert.ThrowsAsync<ValidationException>(() => scope.ServiceProvider.GetRequiredService<ProjectService>().CreateAsync(input));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Projects.ToListAsync());
    }

    [Fact]
    public async Task UpdatesWithFrozenClock_AlwaysAdvanceTimestampAndPreserveCreation()
    {
        await using var root = new IsolatedAppFactory();
        var instant = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await using var app = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<TimeProvider>(new FrozenClock(instant))));
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var id = await service.CreateAsync(Input());
        var input = Input(); input.Name = "変更";
        Assert.True(await service.UpdateAsync(id, input));
        var project = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Projects.SingleAsync();
        Assert.Equal(instant.UtcDateTime, project.CreatedAt);
        Assert.Equal(instant.UtcDateTime.AddTicks(1), project.UpdatedAt);
        Assert.True(await service.UpdateAsync(id, input));
        Assert.Equal(instant.UtcDateTime.AddTicks(2), project.UpdatedAt);
        Assert.False(await service.UpdateAsync(Guid.NewGuid(), input));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("period")]
    public async Task ChangesRequiringWarningConfirmation_AreNotSilentlySaved(string change)
    {
        await using var app = new IsolatedAppFactory();
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = await service.CreateAsync(Input());
        var original = await db.Projects.SingleAsync();
        var timestamp = original.UpdatedAt;
        db.Tasks.Add(new TaskItem
        {
            ProjectId = id, Name = "未完了タスク", SortOrder = 1, StartDate = original.StartDate,
            EndDate = original.EndDate, CreatedAt = timestamp, UpdatedAt = timestamp
        });
        await db.SaveChangesAsync();
        var input = Input();
        if (change == "status") input.Status = ProjectStatus.Completed;
        else input.EndDate = new DateOnly(2026, 10, 2);
        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(id, input));
        Assert.Equal(ProjectStatus.InProgress, original.Status);
        Assert.Equal(new DateOnly(2026, 10, 31), original.EndDate);
        Assert.Equal(timestamp, original.UpdatedAt);
        input = Input(); input.Name = "タスクに影響しない名前変更";
        Assert.True(await service.UpdateAsync(id, input));
    }

    private sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
