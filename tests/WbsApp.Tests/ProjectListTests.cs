using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WbsApp.Data;
using WbsApp.Models.Entities;
using WbsApp.Models.Enums;
using WbsApp.Models.ViewModels;
using WbsApp.Services;
using Xunit;

namespace WbsApp.Tests;

public sealed class ProjectListTests
{
    private static Project Project(int number, ProjectStatus status = ProjectStatus.InProgress, string? name = null) => new()
    {
        ProjectId = Guid.Parse($"00000000-0000-0000-0000-{number:000000000000}"), Name = name ?? $"計画{number:000}",
        Status = status, StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 31),
        CreatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    };
    private static async Task SeedAsync(IsolatedAppFactory app, params Project[] projects)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Projects.AddRange(projects);
        await db.SaveChangesAsync();
    }
    private static async Task<ProjectList> SearchAsync(IsolatedAppFactory app, ProjectSearch search)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ProjectService>().SearchAsync(search);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(100, 100, 1)]
    [InlineData(101, 100, 2)]
    public async Task PageBoundary_HasExactly100RowsPerPage(int count, int expectedRows, int expectedPages)
    {
        await using var app = new IsolatedAppFactory();
        await SeedAsync(app, Enumerable.Range(1, count).Select(value => Project(value)).ToArray());
        var result = await SearchAsync(app, new ProjectSearch());
        Assert.Equal(count, result.TotalCount);
        Assert.Equal(expectedRows, result.Items.Count);
        Assert.Equal(expectedPages, result.PageCount);
        if (count == 101)
        {
            var second = await SearchAsync(app, new ProjectSearch { Page = 2 });
            Assert.Equal(Project(101).ProjectId, Assert.Single(second.Items).ProjectId);
            Assert.DoesNotContain(second.Items[0].ProjectId, result.Items.Select(value => value.ProjectId));
        }
    }

    [Fact]
    public async Task Sort_UsesUpdatedAtDescendingAndIdAscendingForTies()
    {
        await using var app = new IsolatedAppFactory();
        var older = Project(1); older.UpdatedAt = older.UpdatedAt.AddDays(-1);
        var newer = Project(9); newer.UpdatedAt = newer.UpdatedAt.AddDays(1);
        await SeedAsync(app, Project(3), older, newer, Project(2));
        var result = await SearchAsync(app, new ProjectSearch());
        Assert.Equal(new[] { 9, 2, 3, 1 }.Select(value => Project(value).ProjectId), result.Items.Select(value => value.ProjectId));
    }

    [Fact]
    public async Task NameAndStatus_AreAndFiltersAndWhitespaceIsNormalized()
    {
        await using var app = new IsolatedAppFactory();
        await SeedAsync(app, Project(1, name: "開発 計画"), Project(2, ProjectStatus.Completed, "開発 完了"), Project(3, name: "別の計画"));
        var result = await SearchAsync(app, new ProjectSearch { Name = "  開発  ", Status = ProjectStatus.Completed });
        Assert.Equal("開発", result.Search.Name);
        Assert.Equal(Project(2).ProjectId, Assert.Single(result.Items).ProjectId);
        Assert.Equal(2, (await SearchAsync(app, new ProjectSearch { Name = "開発" })).TotalCount);
        Assert.Equal(2, (await SearchAsync(app, new ProjectSearch { Status = ProjectStatus.InProgress })).TotalCount);
        Assert.Equal(3, (await SearchAsync(app, new ProjectSearch { Name = "   " })).TotalCount);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("'")]
    public async Task SearchTreatsSqlWildcardAndQuoteAsLiteralCharacters(string literal)
    {
        await using var app = new IsolatedAppFactory();
        await SeedAsync(app, Project(1, name: "前" + literal + "後"), Project(2, name: "普通の名前"));
        Assert.Equal(Project(1).ProjectId, Assert.Single((await SearchAsync(app, new ProjectSearch { Name = literal })).Items).ProjectId);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(999, 2)]
    [InlineData(int.MaxValue, 2)]
    public async Task OutOfRangePage_IsClampedToValidRange(int page, int expected)
    {
        await using var app = new IsolatedAppFactory();
        await SeedAsync(app, Enumerable.Range(1, 101).Select(value => Project(value)).ToArray());
        var result = await SearchAsync(app, new ProjectSearch { Page = page });
        Assert.Equal(expected, result.Search.Page);
        Assert.NotEmpty(result.Items);
    }

    [Fact]
    public async Task InvalidServiceStatus_IsRejected()
    {
        await using var app = new IsolatedAppFactory();
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => SearchAsync(app, new ProjectSearch { Status = (ProjectStatus)99 }));
    }

    [Fact]
    public async Task EmptyDataAndEmptySearch_ShowDifferentGuidance()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient();
        Assert.Contains("プロジェクトを登録してください", WebUtility.HtmlDecode(await client.GetStringAsync("/Projects")));
        await SeedAsync(app, Project(1));
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Projects?Name=該当なし"));
        Assert.Contains("条件に一致するプロジェクトがありません", html);
        Assert.Contains("検索条件を解除", html);
        Assert.DoesNotContain("プロジェクトを登録してください", html);
    }

    [Fact]
    public async Task PaginationLinks_PreserveFiltersAndSearchFormStartsAtPageOne()
    {
        await using var app = new IsolatedAppFactory();
        await SeedAsync(app, Enumerable.Range(1, 101).Select(value => Project(value, ProjectStatus.Completed, "検索 計画" + value)).ToArray());
        using var client = app.CreateClient();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Projects?Name=検索&Status=1"));
        Assert.Equal(100, Regex.Matches(html, "data-project-id=").Count);
        var nextTag = Regex.Match(html, "<a[^>]*rel=\"next\"[^>]*>").Value;
        var next = Regex.Match(nextTag, "href=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(next);
        var decoded = Uri.UnescapeDataString(next);
        Assert.Contains("Name=検索", decoded); Assert.Contains("Status=1", decoded); Assert.Contains("Page=2", decoded);
        var pageTwo = WebUtility.HtmlDecode(await client.GetStringAsync(next));
        Assert.Single(Regex.Matches(pageTwo, "data-project-id="));
        Assert.Contains("2 / 2 ページ", pageTwo); Assert.Contains("rel=\"prev\"", pageTwo);
        var form = Regex.Match(pageTwo, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("name=\"Page\"", form);
        Assert.Contains("method=\"get\"", form);
        Assert.Contains("value=\"検索\"", form);
    }

    [Theory]
    [InlineData("Status=99")]
    [InlineData("Status=bad")]
    [InlineData("Page=bad")]
    [InlineData("Page=2147483648")]
    public async Task InvalidQuery_Returns400WithValidationMessage(string query)
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/Projects?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("validation-summary-errors", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ListEscapesNamesShowsEditAndCancelReturnsToList()
    {
        await using var app = new IsolatedAppFactory();
        var project = Project(1, name: "<script>名前</script>");
        await SeedAsync(app, project);
        using var client = app.CreateClient();
        var html = await client.GetStringAsync("/Projects");
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>名前", html);
        Assert.Contains($"/Projects/{project.ProjectId}/Edit", html);
        Assert.Contains("datetime=", html);
        var edit = WebUtility.HtmlDecode(await client.GetStringAsync($"/Projects/{project.ProjectId}/Edit"));
        Assert.Contains("href=\"/Projects\">キャンセル", edit);
    }
}
