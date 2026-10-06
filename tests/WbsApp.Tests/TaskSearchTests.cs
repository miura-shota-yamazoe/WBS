using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WbsApp.Data;
using WbsApp.Models.ViewModels;
using WbsApp.Services;
using Xunit;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Tests;

public sealed class TaskSearchTests
{
    private static readonly DateOnly Today = new(2026, 10, 6);
    private static IReadOnlyList<TaskListRow> Rows()
    {
        var project = Guid.NewGuid(); var parent = Guid.NewGuid(); var middle = Guid.NewGuid();
        TaskTreeSource Node(Guid id, Guid? parentId, int sort, string name, int progress, TaskStatus status, int deadline) =>
            new(project, id, parentId, sort, name, "担当者", Today.AddDays(-3), Today.AddDays(deadline), progress, status, WbsApp.Models.Enums.TaskPriority.High);
        return TaskTreeBuilder.Build([
            Node(parent, null, 1, "親", 90, TaskStatus.InProgress, -1),
            Node(middle, parent, 1, "中間", 80, TaskStatus.InProgress, 0),
            Node(Guid.NewGuid(), middle, 1, "対象A", 20, TaskStatus.InReview, -1),
            Node(Guid.NewGuid(), parent, 2, "対象B", 60, TaskStatus.InReview, 0),
            Node(Guid.NewGuid(), null, 2, "対象C", 100, TaskStatus.Completed, -1)
        ], Today);
    }

    [Fact]
    public void AndSearch_KeepsAllAncestorsNumbersAndOriginalOrder()
    {
        var search = new TaskSearch { Name = " 対象 ", Status = TaskStatus.InReview, DelayedOnly = true };
        var result = TaskWbsSearch.Filter(Rows(), search);
        Assert.Equal("対象", search.Name); Assert.Equal(1, result.MatchCount);
        Assert.Equal(new[] { "1", "1.1", "1.1.1" }, result.Rows.Select(row => row.WbsNumber));
        Assert.Equal(new[] { false, false, true }, result.Rows.Select(row => row.IsMatch));
        Assert.False(result.Rows[0].IsLeaf); Assert.Equal(2, result.Rows[2].Depth);
    }

    [Fact]
    public void MatchingParent_DoesNotPullInNonMatchingDescendants()
    {
        var result = TaskWbsSearch.Filter(Rows(), new TaskSearch { Name = "親" });
        Assert.Equal(1, result.MatchCount); Assert.True(Assert.Single(result.Rows).IsMatch);
    }

    [Fact]
    public void MatchingAncestors_AreCountedAsMatchesAndSharedAncestorsAppearOnce()
    {
        var result = TaskWbsSearch.Filter(Rows(), new TaskSearch { Status = TaskStatus.InProgress });
        Assert.Equal(2, result.MatchCount); Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.True(row.IsMatch));
        var review = TaskWbsSearch.Filter(Rows(), new TaskSearch { Status = TaskStatus.InReview });
        Assert.Equal(2, review.MatchCount); Assert.Equal(4, review.Rows.Count);
        Assert.Equal(4, review.Rows.Select(row => row.TaskId).Distinct().Count());
    }

    [Theory]
    [InlineData("%")][InlineData("_")][InlineData("'")]
    public void NameSearch_TreatsWildcardAndQuoteLiterally(string name)
    {
        var rows = Rows();
        var changed = rows.Select((row, index) => index == 2 ? row with { Name = "前" + name + "後" } : row).ToArray();
        Assert.Equal(1, TaskWbsSearch.Filter(changed, new TaskSearch { Name = name }).MatchCount);
    }

    [Fact]
    public void EmptyWhitespaceAndNoMatch_HaveDifferentFilteringStates()
    {
        var rows = Rows(); var search = new TaskSearch { Name = "  " };
        var full = TaskWbsSearch.Filter(rows, search);
        Assert.False(search.IsFiltered); Assert.Equal(rows.Count, full.MatchCount);
        Assert.Equal(rows, full.Rows);
        Assert.Empty(TaskWbsSearch.Filter(rows, new TaskSearch { Name = "不存在" }).Rows);
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            TaskWbsSearch.Filter(rows, new TaskSearch { Status = (TaskStatus)99 }));
    }

    [Fact]
    public void DeepMatch_1100Levels_CollectsAncestorsIteratively()
    {
        var source = new List<TaskTreeSource>(); Guid? parent = null; var project = Guid.NewGuid();
        for (var index = 0; index < 1100; index++)
        {
            var id = Guid.NewGuid();
            source.Add(new(project, id, parent, 1, index == 1099 ? "対象" : "祖先", null,
                Today, Today, 0, TaskStatus.NotStarted, WbsApp.Models.Enums.TaskPriority.Medium));
            parent = id;
        }
        var result = TaskWbsSearch.Filter(TaskTreeBuilder.Build(source, Today), new TaskSearch { Name = "対象" });
        Assert.Equal(1, result.MatchCount); Assert.Equal(1100, result.Rows.Count);
        Assert.Single(result.Rows, row => row.IsMatch);
    }

    private static WebApplicationFactory<Program> Host(IsolatedAppFactory app) => app.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(new FixedClock());
        }));
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private static async Task<Guid> Seed(WebApplicationFactory<Program> host, bool withTasks = true)
    {
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = TaskServiceTests.Project(); db.Projects.Add(project); await db.SaveChangesAsync();
        if (!withTasks) return project.ProjectId;
        var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var input = TaskServiceTests.Input(); input.Name = "親"; input.Progress = 90;
        var parent = (await service.CreateAsync(project.ProjectId, input))!.Value;
        input = TaskServiceTests.Input(parent); input.Name = "対象<script>"; input.AssigneeName = "<担当>";
        input.Progress = 20; input.Status = TaskStatus.InReview;
        await service.CreateAsync(project.ProjectId, input);
        input = TaskServiceTests.Input(); input.Name = "別作業"; input.Progress = 100;
        await service.CreateAsync(project.ProjectId, input);
        return project.ProjectId;
    }

    [Fact]
    public async Task HttpSearch_ShowsFullProgressMatchedCountAncestorsAndTableColumns()
    {
        await using var app = new IsolatedAppFactory(); await using var host = Host(app);
        var project = await Seed(host); using var client = host.CreateClient();
        var path = $"/Projects/{project}/Tasks";
        var raw = await client.GetStringAsync(path + "?Name=%20対象%20&Status=2&DelayedOnly=true");
        var html = WebUtility.HtmlDecode(raw);
        Assert.Contains("一致 1 件", html); Assert.Contains("階層表示", html);
        Assert.Contains("プロジェクト進捗：60.0%", html);
        Assert.Equal(2, Regex.Matches(html, "data-task-id=").Count);
        Assert.Contains("data-wbs-number=\"1.1\"", html);
        Assert.Contains("並び替えは検索解除後", html);
        Assert.Contains("value=\"対象\"", html);
        Assert.Contains("<table class=\"wbs-table\">", html);
        Assert.Contains("担当者</th>", html); Assert.Contains("優先度</th>", html);
        Assert.Contains("&lt;script&gt;", raw); Assert.Contains("&lt;", raw); Assert.DoesNotContain("<script>", raw);
        Assert.Contains("<担当>", html); Assert.Contains("高</td>", html);
        Assert.Contains($"href=\"{path}\">解除", html);
        Assert.DoesNotContain("name=\"AssigneeName\"", html);
        Assert.DoesNotContain("name=\"Priority\"", html);
        var cleared = WebUtility.HtmlDecode(await client.GetStringAsync(path));
        Assert.Equal(3, Regex.Matches(cleared, "data-task-id=").Count);
        Assert.DoesNotContain("並び替えは検索解除後", cleared);
    }

    [Theory]
    [InlineData("Status=99")][InlineData("Status=bad")][InlineData("DelayedOnly=bad")]
    public async Task InvalidQuery_Returns400AndKeepsName(string query)
    {
        await using var app = new IsolatedAppFactory(); await using var host = Host(app);
        var project = await Seed(host); using var client = host.CreateClient();
        using var response = await client.GetAsync($"/Projects/{project}/Tasks?Name=対象&" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("validation-summary-errors", html); Assert.Contains("value=\"対象\"", html);
        Assert.DoesNotContain("data-task-id=", html);
    }

    [Fact]
    public async Task SearchEmptyAndNoTasks_ShowSeparateGuidanceAndMoveState()
    {
        await using var app = new IsolatedAppFactory(); await using var host = Host(app);
        var empty = await Seed(host, false); var project = await Seed(host); using var client = host.CreateClient();
        var emptyHtml = WebUtility.HtmlDecode(await client.GetStringAsync($"/Projects/{empty}/Tasks"));
        Assert.Contains("タスク未登録", emptyHtml);
        var noMatch = WebUtility.HtmlDecode(await client.GetStringAsync($"/Projects/{project}/Tasks?Name=不存在"));
        Assert.Contains("条件に一致するタスクがありません", noMatch); Assert.Contains("一致 0 件", noMatch);
        Assert.Contains("プロジェクト進捗：60.0%", noMatch);
        Assert.DoesNotContain("タスク未登録", noMatch);
        await using var scope = host.Services.CreateAsyncScope(); var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        Assert.False((await service.SearchAsync(project, new TaskSearch { DelayedOnly = true }))!.CanMove);
        Assert.False((await service.SearchAsync(project, new TaskSearch { Status = TaskStatus.Completed }))!.CanMove);
        Assert.True((await service.SearchAsync(project, new TaskSearch { Name = "  " }))!.CanMove);
        Assert.Null(await service.SearchAsync(Guid.NewGuid(), new TaskSearch()));
    }
}
