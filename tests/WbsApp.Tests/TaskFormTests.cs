using System.Data.Common;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WbsApp.Data;
using WbsApp.Services;
using Xunit;

namespace WbsApp.Tests;

public sealed class TaskFormTests
{
    [Fact]
    public async Task CompletionAndReopening_WorkWithoutJavaScriptAndRetainErrors()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(app);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var path = $"/Projects/{projectId}/Tasks/Create";
        var fields = Fields(); fields["Progress"] = "100";
        using var created = await PostAsync(client, path, fields);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Guid id;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var task = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync();
            id = task.TaskId; Assert.Equal(3, (int)task.Status);
        }
        var edit = $"/Projects/{projectId}/Tasks/{id}/Edit";
        fields["WasCompleted"] = "false";
        using var invalid = await PostAsync(client, edit, fields);
        var html = WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("完了から再開するには", html);
        Assert.Contains("data-was-completed=\"true\"", html);
        Assert.Contains("value=\"1\" selected=\"selected\">作業中", html);
        Assert.Contains("value=\"100\"", html);
        fields["Progress"] = "50";
        using var reopened = await PostAsync(client, edit, fields);
        Assert.Equal(HttpStatusCode.Redirect, reopened.StatusCode);
        fields["Status"] = "3"; fields["Progress"] = "25";
        using var completed = await PostAsync(client, edit, fields);
        Assert.Equal(HttpStatusCode.Redirect, completed.StatusCode);
        await using var finalScope = app.Services.CreateAsyncScope();
        var saved = await finalScope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync();
        Assert.Equal(100, saved.Progress); Assert.Equal(3, (int)saved.Status);
    }

    private static Dictionary<string, string> Fields() => new()
    {
        ["Name"] = " 作業 ", ["AssigneeName"] = " 担当者 ", ["StartDate"] = "2026-10-02",
        ["EndDate"] = "2026-10-03", ["Progress"] = "25", ["Status"] = "1", ["Priority"] = "0", ["Memo"] = "備考\n二行目"
    };
    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    }
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, Dictionary<string, string> fields, string? tokenPath = null)
    {
        fields["__RequestVerificationToken"] = await TokenAsync(client, tokenPath ?? path);
        Assert.NotEmpty(fields["__RequestVerificationToken"]);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task DefaultsAndProjectLink_CreateEditAndChildFlow()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(app);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var basePath = $"/Projects/{projectId}/Tasks";
        Assert.Contains(basePath, await client.GetStringAsync("/Projects"));
        var initial = WebUtility.HtmlDecode(await client.GetStringAsync(basePath + "/Create"));
        Assert.Contains("タスク登録", initial); Assert.Contains("name=\"Progress\"", initial); Assert.Contains("value=\"0\"", initial);
        Assert.Contains("value=\"0\" selected=\"selected\">未着手", initial);
        Assert.Contains("value=\"1\" selected=\"selected\">中", initial);
        using var saved = await PostAsync(client, basePath + "/Create", Fields());
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode); Assert.Equal(basePath, saved.Headers.Location!.ToString());
        Guid root;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var task = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync(); root = task.TaskId;
            Assert.Equal("担当者", task.AssigneeName); Assert.Equal(0, (int)task.Priority);
        }
        var list = WebUtility.HtmlDecode(await client.GetStringAsync(basePath));
        Assert.Contains("タスクを登録しました。", list); Assert.Contains("子タスク追加", list);
        var childPath = basePath + "/Create?parentId=" + root;
        Assert.Contains(root.ToString(), await client.GetStringAsync(childPath));
        var childFields = Fields(); childFields["ParentTaskId"] = root.ToString(); childFields["Name"] = "子作業";
        using var childSaved = await PostAsync(client, basePath + "/Create", childFields, childPath);
        Assert.Equal(HttpStatusCode.Redirect, childSaved.StatusCode);
        Guid child;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var task = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync(value => value.ParentTaskId == root);
            child = task.TaskId; Assert.Equal(1, task.SortOrder);
        }
        var editPath = basePath + $"/{child}/Edit";
        var edit = WebUtility.HtmlDecode(await client.GetStringAsync(editPath));
        Assert.Contains("disabled=\"disabled\"", edit); Assert.Contains("type=\"hidden\"", edit);
        childFields["Name"] = "更新した子"; childFields["AssigneeName"] = "   "; childFields["Progress"] = "100"; childFields["Status"] = "3";
        using var edited = await PostAsync(client, editPath, childFields);
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        Assert.Contains("タスクを保存しました。", WebUtility.HtmlDecode(await client.GetStringAsync(basePath)));
        await using var finalScope = app.Services.CreateAsyncScope();
        var updated = await finalScope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync(value => value.TaskId == child);
        Assert.Equal("更新した子", updated.Name); Assert.Null(updated.AssigneeName); Assert.Equal(100, updated.Progress); Assert.Equal(root, updated.ParentTaskId);
    }

    [Theory]
    [InlineData("Name", "   ", "名前を入力してください。")]
    [InlineData("StartDate", "", "開始予定日を入力してください。")]
    [InlineData("StartDate", "bad", "開始予定日の入力形式を確認してください。")]
    [InlineData("EndDate", "2026-10-01", "終了予定日は開始予定日以降にしてください。")]
    [InlineData("Progress", "", "進捗率を入力してください。")]
    [InlineData("Progress", "1.5", "進捗率の入力形式を確認してください。")]
    [InlineData("Progress", "101", "進捗率は0～100の整数で入力してください。")]
    [InlineData("Status", "", "ステータスを選択してください。")]
    [InlineData("Status", "99", "入力値が正しくありません。入力内容を確認してください。")]
    [InlineData("Priority", "", "優先度を選択してください。")]
    [InlineData("Priority", "99", "入力値が正しくありません。入力内容を確認してください。")]
    [InlineData("ParentTaskId", "bad", "親タスクの入力形式を確認してください。")]
    public async Task InvalidPost_PreservesInputsAndDoesNotWrite(string field, string value, string message)
    {
        await using var app = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(app);
        using var client = app.CreateClient(); var fields = Fields(); fields[field] = value;
        using var response = await PostAsync(client, $"/Projects/{projectId}/Tasks/Create", fields);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(message, html); Assert.Contains("備考", html); Assert.Contains("二行目", html); Assert.Contains("担当者", html);
        await using var scope = app.Services.CreateAsyncScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.ToArrayAsync());
    }

    [Fact]
    public async Task ParentValidation_RebuildsOptionsAndPreservesInput()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(app);
        using var client = app.CreateClient(); var fields = Fields(); fields["ParentTaskId"] = Guid.NewGuid().ToString();
        using var response = await PostAsync(client, $"/Projects/{projectId}/Tasks/Create", fields);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("同じプロジェクトの既存タスクを親に選択してください。", html); Assert.Contains("担当者", html);
        Assert.Contains("ルート（親なし）", html);
    }

    [Fact]
    public async Task WrongProjectMissingTaskAndCsrf_AreRejected()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(app);
        Guid taskId;
        await using (var scope = app.Services.CreateAsyncScope()) taskId = (await scope.ServiceProvider.GetRequiredService<TaskService>().CreateAsync(projectId, TaskServiceTests.Input()))!.Value;
        using var client = app.CreateClient();
        var createPath = $"/Projects/{projectId}/Tasks/Create";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(createPath, new FormUrlEncodedContent(Fields()))).StatusCode);
        foreach (var path in new[] { $"/Projects/{Guid.NewGuid()}/Tasks/{taskId}/Edit", $"/Projects/{projectId}/Tasks/{Guid.NewGuid()}/Edit" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
            using var response = await PostAsync(client, path, Fields(), createPath);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("担当者", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(createPath + "?parentId=" + Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task EntityFieldsAreIgnoredAndStoredHtmlIsEscaped()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(app);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }); var fields = Fields();
        fields["ProjectId"] = Guid.NewGuid().ToString(); fields["TaskId"] = Guid.Empty.ToString(); fields["SortOrder"] = "99"; fields["CreatedAt"] = "1900-01-01";
        fields["Name"] = "<script>名前</script>"; fields["Memo"] = "</textarea><script>alert(1)</script>";
        using var response = await PostAsync(client, $"/Projects/{projectId}/Tasks/Create", fields);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope(); var task = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync();
        Assert.NotEqual(Guid.Empty, task.TaskId); Assert.Equal(projectId, task.ProjectId); Assert.Equal(1, task.SortOrder); Assert.True(task.CreatedAt.Year > 1900);
        var html = await client.GetStringAsync($"/Projects/{projectId}/Tasks/{task.TaskId}/Edit");
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script>名前", html); Assert.DoesNotContain("</textarea><script>alert", html);
    }

    [Fact]
    public async Task InsertFailure_RollsBackProjectUpdateAndPreservesFormWithErrorId()
    {
        await using var root = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(root);
        DateTime before;
        await using (var scope = root.Services.CreateAsyncScope()) before = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Projects.AsNoTracking().SingleAsync()).UpdatedAt;
        await using var app = root.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new FailTaskInsert()))));
        using var client = app.CreateClient();
        using var response = await PostAsync(client, $"/Projects/{projectId}/Tasks/Create", Fields());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()); Assert.Contains("担当者", html); Assert.DoesNotContain("private-failure", html);
        var id = Regex.Match(html, "エラーID：([a-f0-9]{32})").Groups[1].Value; Assert.Equal(32, id.Length);
        Assert.Contains(id, string.Join("\n", Directory.EnumerateFiles(Path.GetDirectoryName(root.DatabasePath)!, "wbs-*.log").Select(File.ReadAllText)));
        await using var finalScope = app.Services.CreateAsyncScope(); var db = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Tasks.AsNoTracking().ToArrayAsync()); Assert.Equal(before, (await db.Projects.AsNoTracking().SingleAsync()).UpdatedAt);
    }

    [Fact]
    public async Task EditUsesUrlTaskIdAndRejectsInvalidChangesWithoutLosingInput()
    {
        await using var app = new IsolatedAppFactory(); var projectId = await TaskServiceTests.SeedProjectAsync(app);
        Guid first; Guid second;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TaskService>();
            first = (await service.CreateAsync(projectId, TaskServiceTests.Input()))!.Value;
            second = (await service.CreateAsync(projectId, TaskServiceTests.Input()))!.Value;
        }
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var path = $"/Projects/{projectId}/Tasks/{first}/Edit"; var fields = Fields();
        fields["Name"] = "変更する名前"; fields["Progress"] = "101";
        using var invalid = await PostAsync(client, path, fields);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("変更する名前", WebUtility.HtmlDecode(await invalid.Content.ReadAsStringAsync()));
        await using (var scope = app.Services.CreateAsyncScope())
            Assert.Equal("作業", (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().SingleAsync(value => value.TaskId == first)).Name);
        fields["Progress"] = "25"; fields["TaskId"] = second.ToString(); fields["ProjectId"] = Guid.NewGuid().ToString();
        using var saved = await PostAsync(client, path, fields);
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await using var finalScope = app.Services.CreateAsyncScope();
        var tasks = await finalScope.ServiceProvider.GetRequiredService<AppDbContext>().Tasks.AsNoTracking().ToDictionaryAsync(value => value.TaskId);
        Assert.Equal("変更する名前", tasks[first].Name); Assert.Equal("作業", tasks[second].Name);
    }

    private sealed class FailTaskInsert : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"tasks\"", StringComparison.Ordinal)) throw new InvalidOperationException("private-failure");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
