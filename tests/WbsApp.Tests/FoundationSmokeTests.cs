using System.Net;
using Microsoft.Data.Sqlite;
using Xunit;

namespace WbsApp.Tests;

public sealed class FoundationSmokeTests : IClassFixture<IsolatedAppFactory>
{
    private readonly IsolatedAppFactory _factory;

    public FoundationSmokeTests(IsolatedAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Home_RendersJapaneseHtmlThroughMvc()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("lang=\"ja\"", html);
        Assert.Contains("<h1>WBSアプリ</h1>", html);
    }

    [Fact]
    public async Task UnknownRoute_ReturnsNotFound()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/missing-controller/missing-action");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SqliteNativeDependency_OpensAnInMemoryConnection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";

        var result = await command.ExecuteScalarAsync();

        var versionText = Assert.IsType<string>(result);
        Assert.True(Version.TryParse(versionText, out var version));
        Assert.NotNull(version);
        Assert.Equal(3, version.Major);
    }
}
