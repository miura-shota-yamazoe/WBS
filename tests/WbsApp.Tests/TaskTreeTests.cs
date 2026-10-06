using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WbsApp.Data;
using WbsApp.Models.Entities;
using WbsApp.Models.ViewModels;
using WbsApp.Services;
using Xunit;
using TaskStatus = WbsApp.Models.Enums.TaskStatus;

namespace WbsApp.Tests;

public sealed class TaskTreeTests
{
    private static readonly Guid ProjectId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateOnly Today = new(2026, 10, 6);

    private static TaskTreeSource Node(int id, Guid? parent = null, int sort = 1, int progress = 0,
        TaskStatus status = TaskStatus.NotStarted) => new(ProjectId,
        Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"), parent, sort,
        $"作業{id}", null, Today, Today, progress, status, WbsApp.Models.Enums.TaskPriority.Medium);

    [Fact]
    public void UnsortedInput_BecomesPreorderWithSiblingNumbersAndLeafFlags()
    {
        var root1 = Node(1);
        var root2 = Node(2, sort: 2);
        var child1 = Node(3, root1.TaskId);
        var child2 = Node(4, root1.TaskId, sort: 2);
        var grandchild = Node(5, child2.TaskId);
        var result = TaskTreeBuilder.Build([grandchild, root2, child2, root1, child1], Today);
        Assert.Equal(new[] { "1", "1.1", "1.2", "1.2.1", "2" }, result.Select(value => value.WbsNumber));
        Assert.Equal(new[] { 0, 1, 1, 2, 0 }, result.Select(value => value.Depth));
        Assert.Equal(new[] { false, true, false, true, true }, result.Select(value => value.IsLeaf));
        Assert.Equal(new[] { root1.TaskId, child1.TaskId, child2.TaskId, grandchild.TaskId, root2.TaskId },
            result.Select(value => value.TaskId));
    }

    [Fact]
    public void DeepChain_1100Levels_UsesIterativeTraversal()
    {
        var chain = new List<TaskTreeSource>(1100);
        Guid? parent = null;
        for (var id = 1; id <= 1100; id++)
        {
            var task = Node(id, parent);
            chain.Add(task); parent = task.TaskId;
        }
        chain.Reverse();
        var result = TaskTreeBuilder.Build(chain, Today);
        Assert.Equal(1100, result.Count);
        Assert.Equal(1099, result[^1].Depth);
        Assert.Equal(string.Join('.', Enumerable.Repeat("1", 1100)), result[^1].WbsNumber);
        Assert.Single(result, value => value.IsLeaf);
    }

    [Fact]
    public void WideTree_5000Siblings_KeepsLastNumber()
    {
        var result = TaskTreeBuilder.Build(Enumerable.Range(1, 5000).Reverse()
            .Select(id => Node(id, sort: id)).ToArray(), Today);
        Assert.Equal("1", result[0].WbsNumber);
        Assert.Equal("5000", result[^1].WbsNumber);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("gap")]
    [InlineData("orphan")]
    [InlineData("cycle")]
    public void InvalidHierarchy_IsNotPresentedAsNormalWbs(string caseName)
    {
        var first = Node(1);
        TaskTreeSource[] tasks = caseName switch
        {
            "duplicate" => [first, first],
            "gap" => [first, Node(2, sort: 3)],
            "orphan" => [Node(2, Guid.NewGuid())],
            _ => [Node(1, Node(2).TaskId), Node(2, Node(1).TaskId)]
        };
        Assert.Throws<InvalidDataException>(() => TaskTreeBuilder.Build(tasks, Today));
    }

    [Fact]
    public void LeafAverage_ExcludesParentAndChangesWhenChildIsAdded()
    {
        var parent = Node(1, progress: 100, status: TaskStatus.Completed);
        var other = Node(2, sort: 2, progress: 40, status: TaskStatus.InProgress);
        var before = new[] { parent, other };
        Assert.Equal(70.0m, Average(before));
        var child = Node(3, parent.TaskId, progress: 20, status: TaskStatus.InProgress);
        Assert.Equal(30.0m, Average([parent, other, child]));
        Assert.Equal(70.0m, Average(before));
    }

    [Fact]
    public void EmptyFractionAndCompletedCases_AreFormattedByTheRules()
    {
        Assert.Null(Average([]));
        Assert.Equal(33.3m, Average([Node(1, progress: 0), Node(2, sort: 2, progress: 0), Node(3, sort: 3, progress: 100)]));
        Assert.Equal(60.0m, Average(Enumerable.Range(1, 20)
            .Select(id => Node(id, sort: id, progress: id == 1 ? 59 : 60)).ToArray()));
        Assert.Equal(99.9m, Average(Enumerable.Range(1, 20)
            .Select(id => Node(id, sort: id, progress: id == 1 ? 99 : 100)).ToArray()));
        Assert.Equal(100.0m, Average([Node(1, progress: 100), Node(2, sort: 2, progress: 100)]));
    }

    private static decimal? Average(IEnumerable<TaskTreeSource> tasks) => ProjectProgressCalculator.Calculate(
        tasks.Select(value => new TaskProgressSource(value.ProjectId, value.TaskId, value.ParentTaskId, value.Progress)));

    [Fact]
    public async Task ProjectSearch_UsesAllLeavesOfOnlyTheCurrentPage()
    {
        await using var app = new IsolatedAppFactory();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var projects = Enumerable.Range(1, 101).Select(id => new Project
        {
            ProjectId = Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"),
            Name = $"計画{id:000}", StartDate = Today, EndDate = Today.AddDays(7),
            CreatedAt = DateTime.UnixEpoch, UpdatedAt = DateTime.UnixEpoch
        }).ToArray();
        db.Projects.AddRange(projects); await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<TaskService>();
        var input = TaskServiceTests.Input(); input.StartDate = Today; input.EndDate = Today;
        input.Progress = 100;
        await service.CreateAsync(projects[100].ProjectId, input);
        input.Progress = 25; input.Status = TaskStatus.InProgress;
        await service.CreateAsync(projects[0].ProjectId, input);
        foreach (var project in await db.Projects.ToListAsync()) project.UpdatedAt = DateTime.UnixEpoch;
        await db.SaveChangesAsync();
        var pageOne = await scope.ServiceProvider.GetRequiredService<ProjectService>().SearchAsync(new ProjectSearch());
        Assert.Equal(100, pageOne.Items.Count);
        Assert.Equal(25.0m, pageOne.Items.Single(value => value.ProjectId == projects[0].ProjectId).Progress);
        Assert.DoesNotContain(pageOne.Items, value => value.ProjectId == projects[100].ProjectId);
        Assert.Null(pageOne.Items.Single(value => value.ProjectId == projects[1].ProjectId).Progress);
        var pageTwo = await scope.ServiceProvider.GetRequiredService<ProjectService>().SearchAsync(new ProjectSearch { Page = 2 });
        Assert.Equal(100.0m, Assert.Single(pageTwo.Items).Progress);
        var filtered = await scope.ServiceProvider.GetRequiredService<ProjectService>().SearchAsync(new ProjectSearch { Name = "計画001" });
        Assert.Equal(25.0m, Assert.Single(filtered.Items).Progress);
    }

    [Fact]
    public async Task HttpLists_ShowNumbersIndentationAndSameProjectProgress()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TaskService>();
            var parentInput = TaskServiceTests.Input(); parentInput.Name = "親タスク"; parentInput.Progress = 90;
            var parent = (await service.CreateAsync(projectId, parentInput))!.Value;
            var childInput = TaskServiceTests.Input(parent); childInput.Name = "子タスク"; childInput.Progress = 20;
            await service.CreateAsync(projectId, childInput);
        }
        using var client = app.CreateClient();
        var tasks = WebUtility.HtmlDecode(await client.GetStringAsync($"/Projects/{projectId}/Tasks"));
        var projects = WebUtility.HtmlDecode(await client.GetStringAsync("/Projects"));
        Assert.Contains("data-wbs-number=\"1\" data-depth=\"0\"", tasks);
        Assert.Contains("data-wbs-number=\"1.1\" data-depth=\"1\"", tasks);
        Assert.Contains("プロジェクト進捗：20.0%", tasks);
        Assert.Contains("20.0%", projects);
        Assert.Contains("親の進捗は手入力", tasks);
        Assert.Contains("90%", tasks);
        Assert.Contains("20%", tasks);
    }
}
