using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace WbsApp.Tests;

public sealed class CommonUiTests : IClassFixture<CommonUiFactory>
{
    private readonly CommonUiFactory _factory;
    public CommonUiTests(CommonUiFactory factory) => _factory = factory;

    [Fact]
    public async Task LayoutAndStyles_AreServedLocally()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("本文へ移動", html);
        Assert.Contains("ブラウザーを閉じてもアプリは動作を継続します。", html);
        Assert.Contains("/css/site.css?v=", html);
        Assert.DoesNotContain("cdn", html, StringComparison.OrdinalIgnoreCase);
        using var css = await client.GetAsync("/css/site.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-token")]
    public async Task PostWithoutValidToken_IsRejected(string? token)
    {
        using var client = _factory.CreateClient();
        var fields = new Dictionary<string, string> { ["Name"] = "保存しない" };
        if (token is not null) fields["__RequestVerificationToken"] = token;
        using var response = await client.PostAsync("/_test/input", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("送信内容を確認できませんでした", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("保存しない", html);
    }

    [Fact]
    public async Task ValidTokenWithInvalidInput_PreservesAndEscapesValueAndShowsFieldError()
    {
        using var client = _factory.CreateClient();
        var token = await client.GetStringAsync("/_test/token");
        using var response = await client.PostAsync("/_test/input", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "<script>alert(1)</script>", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("名前は10文字以内で入力してください。", WebUtility.HtmlDecode(html));
        Assert.Contains("aria-invalid=\"true\"", html);
        Assert.Contains("aria-describedby=\"Name-error\"", html);
        Assert.Contains("必須", html);
    }

    [Fact]
    public async Task RequiredInput_IsValidatedOnServer()
    {
        using var client = _factory.CreateClient();
        var token = await client.GetStringAsync("/_test/token");
        using var response = await client.PostAsync("/_test/input", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "", ["__RequestVerificationToken"] = token
        }));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("名前を入力してください。", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task ModelError_AppearsInSharedSummary()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/_test/summary");
        Assert.Contains("保存できませんでした。入力を確認してください。", WebUtility.HtmlDecode(html));
        Assert.Contains("role=\"alert\"", html);
    }

    [Fact]
    public async Task Notification_IsEscapedAndConsumedOnce()
    {
        using var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/_test/notify");
        Assert.Contains("<script>通知</script>", WebUtility.HtmlDecode(html));
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>通知", html);
        Assert.Contains("role=\"status\"", html);
        Assert.DoesNotContain("通知", await client.GetStringAsync("/"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionOnGetOrPost_ShowsOnlyErrorIdAndMatchesFileLog(bool post)
    {
        using var client = _factory.CreateClient();
        HttpResponseMessage response;
        if (post)
        {
            var token = await client.GetStringAsync("/_test/token");
            response = await client.PostAsync("/_test/failure", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Memo"] = "private-form-content", ["__RequestVerificationToken"] = token
            }));
        }
        else response = await client.GetAsync("/_test/failure?memo=private-query-content");
        using (response)
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("処理を完了できませんでした", WebUtility.HtmlDecode(html));
            Assert.Contains("プロジェクト一覧へ戻る", WebUtility.HtmlDecode(html));
            var id = Regex.Match(html, "id=\"error-id\">([a-f0-9]{32})</code>").Groups[1].Value;
            Assert.Equal(32, id.Length);
            Assert.DoesNotContain("private-exception-content", html);
            Assert.DoesNotContain("SELECT", html);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
            var log = string.Join("\n", Directory.EnumerateFiles(_factory.LogDirectory, "wbs-*.log").Select(File.ReadAllText));
            Assert.Contains(id, log);
            Assert.Contains("InvalidOperationException", log);
            Assert.DoesNotContain("private-exception-content", log);
            Assert.DoesNotContain("private-form-content", log);
            Assert.DoesNotContain("private-query-content", log);
        }
    }

    [Fact]
    public async Task UnknownRoute_ShowsHelpful404WithoutChangingStatus()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/unknown");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("ページが見つかりません", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }
}

public sealed class CommonUiFactory : WebApplicationFactory<Program>
{
    private readonly TestDatabase _database = new();
    public string LogDirectory => Path.GetDirectoryName(_database.FilePath)!;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", _database.FilePath);
        builder.UseSetting("Logging:Directory", LogDirectory);
        builder.ConfigureServices(services => services.AddControllersWithViews().AddApplicationPart(typeof(UiProbeController).Assembly));
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _database.Dispose();
    }
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}

// Registered only by the test factory; these routes do not exist in the distributed app.
[Route("_test")]
public sealed class UiProbeController(IAntiforgery antiforgery, IModelMetadataProvider metadata) : Controller
{
    [HttpGet("token")]
    public IActionResult Token() => Content(antiforgery.GetAndStoreTokens(HttpContext).RequestToken!);
    [HttpPost("input")]
    public IActionResult Input(ProbeInput input)
    {
        var viewData = new ViewDataDictionary<string>(metadata, ModelState) { Model = input.Name! };
        viewData.ModelExplorer = metadata.GetModelExplorerForType(typeof(ProbeInput), input).GetExplorerForProperty(nameof(ProbeInput.Name));
        viewData.TemplateInfo.HtmlFieldPrefix = nameof(ProbeInput.Name);
        return new ViewResult { ViewName = "/Views/Shared/EditorTemplates/String.cshtml", ViewData = viewData, TempData = TempData };
    }
    [HttpGet("summary")]
    public IActionResult Summary()
    {
        ModelState.AddModelError("", "保存できませんでした。入力を確認してください。");
        return View("/Views/Shared/_FormErrors.cshtml");
    }
    [HttpGet("notify")]
    public IActionResult Notify()
    {
        TempData["SuccessMessage"] = "<script>通知</script>";
        return RedirectToAction("Index", "Home");
    }
    [HttpGet("failure"), HttpPost("failure")]
    public IActionResult Failure() => throw new InvalidOperationException("SELECT private-exception-content");
}

public sealed class ProbeInput
{
    [Display(Name = "名前")]
    [Required(ErrorMessage = "名前を入力してください。")]
    [StringLength(10, ErrorMessage = "名前は10文字以内で入力してください。")]
    public string? Name { get; set; }
}
