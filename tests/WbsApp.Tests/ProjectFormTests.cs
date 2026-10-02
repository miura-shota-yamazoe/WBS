using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WbsApp.Data;
using Xunit;

namespace WbsApp.Tests;

public sealed class ProjectFormTests
{
    private static Dictionary<string, string> ValidInput() => new()
    {
        ["Name"] = "計画", ["Description"] = "説明\n二行目", ["StartDate"] = "2026-10-01",
        ["EndDate"] = "2026-10-31", ["Status"] = "0"
    };
    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        return WebUtility.HtmlDecode(token);
    }
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await TokenAsync(client, path);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }
    private static async Task<WbsApp.Models.Entities.Project[]> ProjectsAsync(IsolatedAppFactory app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Projects.AsNoTracking().ToArrayAsync();
    }

    [Fact]
    public async Task CreateForm_HasEmptyDatesDefaultStatusTokenAndLocalScript()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient();
        var html = await client.GetStringAsync("/Projects/Create");
        Assert.Contains("プロジェクト登録", WebUtility.HtmlDecode(html));
        Assert.Contains("method=\"post\"", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains("value=\"0\" selected=\"selected\"", html);
        Assert.DoesNotContain("value=\"2026-", html);
        Assert.Contains("/js/project-form.js?v=", html);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/js/project-form.js")).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Create_PersistsNormalizedDataAndIgnoresEntityFields(int status)
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var fields = ValidInput(); fields["Name"] = "  計画  "; fields["Status"] = status.ToString();
        fields["ProjectId"] = Guid.Empty.ToString(); fields["CreatedAt"] = "1900-01-01";
        using var response = await PostAsync(client, "/Projects/Create", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var project = Assert.Single(await ProjectsAsync(app));
        Assert.NotEqual(Guid.Empty, project.ProjectId);
        Assert.Equal("計画", project.Name);
        Assert.Equal("説明\n二行目", project.Description);
        Assert.Equal(new DateOnly(2026, 10, 1), project.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 31), project.EndDate);
        Assert.Equal(status, (int)project.Status);
        Assert.Equal(DateTimeKind.Utc, project.CreatedAt.Kind);
        Assert.Equal(project.CreatedAt, project.UpdatedAt);
        Assert.True(project.CreatedAt.Year > 1900);
        Assert.Equal("/Projects", response.Headers.Location!.ToString());
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(response.Headers.Location));
        Assert.Contains("プロジェクトを登録しました。", html);
        Assert.Contains("プロジェクト一覧", html);
    }

    [Theory]
    [InlineData("Name", "", "名前を入力してください。")]
    [InlineData("Name", "   ", "名前を入力してください。")]
    [InlineData("StartDate", "", "開始予定日を入力してください。")]
    [InlineData("EndDate", "", "終了予定日を入力してください。")]
    [InlineData("StartDate", "2026-02-30", "開始予定日の入力形式を確認してください。")]
    [InlineData("EndDate", "2026-09-30", "終了予定日は開始予定日以降にしてください。")]
    [InlineData("Status", "", "状態を選択してください。")]
    [InlineData("Status", "99", "入力値が正しくありません。入力内容を確認してください。")]
    [InlineData("Status", "bad", "状態の入力形式を確認してください。")]
    public async Task InvalidCreate_DoesNotWriteAndPreservesOtherInputs(string field, string value, string message)
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient();
        var fields = ValidInput(); fields[field] = value;
        using var response = await PostAsync(client, "/Projects/Create", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(message, html);
        Assert.Contains("説明", html);
        Assert.Contains("二行目", html);
        Assert.Empty(await ProjectsAsync(app));
    }

    [Theory]
    [InlineData("Name", 101, "名前は100文字以内で入力してください。")]
    [InlineData("Description", 1001, "説明は1000文字以内で入力してください。")]
    public async Task OverLimit_IsRejectedAndFullValueIsRetained(string field, int length, string message)
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient();
        var fields = ValidInput(); var value = new string('あ', length); fields[field] = value;
        using var response = await PostAsync(client, "/Projects/Create", fields);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(message, html);
        Assert.Contains(value, html);
        Assert.Empty(await ProjectsAsync(app));
    }

    [Fact]
    public async Task TrimmedNameAndDescriptionAtLimits_AndSameDateAreAccepted()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var fields = ValidInput(); fields["Name"] = "  " + new string('名', 100) + "  ";
        fields["Description"] = new string('説', 1000); fields["EndDate"] = fields["StartDate"];
        using var response = await PostAsync(client, "/Projects/Create", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var project = Assert.Single(await ProjectsAsync(app));
        Assert.Equal(100, project.Name.Length); Assert.Equal(1000, project.Description!.Length);
        Assert.Equal(project.StartDate, project.EndDate);
    }

    [Fact]
    public async Task MissingStatus_IsRejectedRatherThanUsingGetDefault()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient();
        var fields = ValidInput(); fields.Remove("Status");
        using var response = await PostAsync(client, "/Projects/Create", fields);
        Assert.Contains("状態を選択してください。", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        Assert.Empty(await ProjectsAsync(app));
    }

    [Fact]
    public async Task Edit_UpdatesFieldsKeepsIdAndCreationTimeAndRejectsInvalidSave()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var create = await PostAsync(client, "/Projects/Create", ValidInput());
        var before = Assert.Single(await ProjectsAsync(app));
        var path = $"/Projects/{before.ProjectId}/Edit";
        var invalid = ValidInput(); invalid["Name"] = "変更する名前"; invalid["EndDate"] = "2026-09-30";
        using var rejected = await PostAsync(client, path, invalid);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains("変更する名前", WebUtility.HtmlDecode(await rejected.Content.ReadAsStringAsync()));
        Assert.Equal(before.Name, Assert.Single(await ProjectsAsync(app)).Name);
        var fields = ValidInput(); fields["Name"] = "  更新  "; fields["Description"] = "";
        fields["Status"] = "1"; fields["StartDate"] = "2026-11-01"; fields["EndDate"] = "2026-11-02";
        using var updated = await PostAsync(client, path, fields);
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        var after = Assert.Single(await ProjectsAsync(app));
        Assert.Equal(before.ProjectId, after.ProjectId); Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.True(after.UpdatedAt > before.UpdatedAt); Assert.Equal("更新", after.Name);
        Assert.Null(after.Description); Assert.Equal(1, (int)after.Status);
        Assert.Equal(new DateOnly(2026, 11, 1), after.StartDate);
    }

    [Fact]
    public async Task MissingEditAndMalformedId_Return404AndDoNotWrite()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient();
        var token = await TokenAsync(client, "/Projects/Create");
        foreach (var id in new[] { Guid.NewGuid().ToString(), "not-a-guid" })
        {
            var path = $"/Projects/{id}/Edit";
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
            var fields = ValidInput(); fields["__RequestVerificationToken"] = token;
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(path, new FormUrlEncodedContent(fields))).StatusCode);
        }
        Assert.Empty(await ProjectsAsync(app));
    }

    [Fact]
    public async Task ProjectPostWithoutToken_DoesNotWrite()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient();
        using var response = await client.PostAsync("/Projects/Create", new FormUrlEncodedContent(ValidInput()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ProjectsAsync(app));
    }

    [Fact]
    public async Task SaveFailure_PreservesInputShowsErrorIdAndDoesNotWrite()
    {
        await using var root = new IsolatedAppFactory();
        await using var app = root.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new FailedSaveInterceptor()))));
        using var client = app.CreateClient();
        var fields = ValidInput(); fields["Name"] = "保存失敗でも保持";
        using var response = await PostAsync(client, "/Projects/Create", fields);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("保存失敗でも保持", html);
        Assert.Contains("保存できませんでした。入力内容を保持しています。", html);
        var id = Regex.Match(html, "エラーID：([a-f0-9]{32})").Groups[1].Value;
        Assert.Equal(32, id.Length);
        Assert.DoesNotContain("private-save-exception", html);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Projects.ToArrayAsync());
        var logs = Directory.EnumerateFiles(Path.GetDirectoryName(root.DatabasePath)!, "wbs-*.log").Select(File.ReadAllText);
        Assert.Contains(id, string.Join("\n", logs));
    }

    [Fact]
    public async Task EditUsesUrlIdEvenWhenPostedIdTargetsAnotherProject()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var firstCreate = await PostAsync(client, "/Projects/Create", ValidInput());
        var first = Assert.Single(await ProjectsAsync(app));
        var other = ValidInput(); other["Name"] = "別のプロジェクト";
        using var otherCreate = await PostAsync(client, "/Projects/Create", other);
        var second = (await ProjectsAsync(app)).Single(value => value.ProjectId != first.ProjectId);
        var fields = ValidInput(); fields["Name"] = "URLの対象を更新"; fields["id"] = second.ProjectId.ToString();
        using var saved = await PostAsync(client, $"/Projects/{first.ProjectId}/Edit", fields);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var projects = await ProjectsAsync(app);
        Assert.Equal("URLの対象を更新", projects.Single(value => value.ProjectId == first.ProjectId).Name);
        Assert.Equal("別のプロジェクト", projects.Single(value => value.ProjectId == second.ProjectId).Name);
    }

    private sealed class FailedSaveInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new DbUpdateException("private-save-exception");
    }

    [Fact]
    public async Task StoredHtml_IsEscapedInEditForm()
    {
        await using var app = new IsolatedAppFactory();
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var fields = ValidInput(); fields["Name"] = "<script>名前</script>"; fields["Description"] = "</textarea><script>alert(1)</script>";
        using var saved = await PostAsync(client, "/Projects/Create", fields);
        var project = Assert.Single(await ProjectsAsync(app));
        var html = await client.GetStringAsync($"/Projects/{project.ProjectId}/Edit");
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>名前", html);
        Assert.DoesNotContain("</textarea><script>alert", html);
    }
}
