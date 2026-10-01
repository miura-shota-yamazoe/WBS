using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WbsApp.Data;
using WbsApp.Infrastructure.Startup;
using WbsApp.Models.Entities;
using Xunit;

namespace WbsApp.Tests;

public sealed class DatabaseStartupTests
{
    private static WebApplicationFactory<Program> CreateFactory(string path) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseSetting("Database:Path", path));

    [Fact]
    public void DefaultLocation_UsesLocalApplicationData()
    {
        var location = new DatabaseLocation(new ConfigurationBuilder().Build());
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WbsApp", "Data", "wbsapp.db"), location.FilePath);
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(location.ConnectionString);
        Assert.True(connection.ForeignKeys);
        Assert.False(connection.Pooling);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative.db")]
    [InlineData(":memory:")]
    public void InvalidPath_IsRejected(string path)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:Path"] = path }).Build();
        Assert.Throws<InvalidOperationException>(() => new DatabaseLocation(configuration));
    }

    [Fact]
    public async Task FirstStart_CreatesDatabaseAndRestartPreservesProjectAndTask()
    {
        using var database = new TestDatabase();
        Assert.False(File.Exists(database.FilePath));
        Guid projectId;
        Guid taskId;
        var timestamp = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var app = CreateFactory(database.FilePath))
        {
            using var client = app.CreateClient();
            Assert.True((await client.GetAsync("/")).IsSuccessStatusCode);
            Assert.True(File.Exists(database.FilePath));
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Single(await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var project = new Project
            {
                Name = "再起動の確認", StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 31),
                CreatedAt = timestamp, UpdatedAt = timestamp
            };
            var task = new TaskItem
            {
                Project = project, Name = "保持するタスク", SortOrder = 1,
                StartDate = project.StartDate, EndDate = project.EndDate,
                CreatedAt = timestamp, UpdatedAt = timestamp
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            projectId = project.ProjectId;
            taskId = task.TaskId;
        }

        await using (var restarted = CreateFactory(database.FilePath))
        {
            using var client = restarted.CreateClient();
            Assert.True((await client.GetAsync("/")).IsSuccessStatusCode);
            await using var scope = restarted.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("再起動の確認", (await db.Projects.SingleAsync()).Name);
            var task = await db.Tasks.SingleAsync();
            Assert.Equal(taskId, task.TaskId);
            Assert.Equal(projectId, task.ProjectId);
            Assert.Equal(timestamp, task.CreatedAt);
            Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        }

        // No pooled handle remains after shutdown: a stopped DB can be opened exclusively.
        using var exclusive = new FileStream(database.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task EmptyExistingFile_CanBeInitialized()
    {
        using var database = new TestDatabase();
        using (database.OpenConnection()) { }
        await using var app = CreateFactory(database.FilePath);
        using var client = app.CreateClient();
        Assert.True((await client.GetAsync("/")).IsSuccessStatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task UnknownMigration_StopsStartupAndLeavesDatabaseUnchanged()
    {
        using var database = new TestDatabase();
        await using (var app = CreateFactory(database.FilePath))
        {
            using var client = app.CreateClient();
        }
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_FutureVersion', '10.0.12')";
            command.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(database.FilePath);
        await using var olderApp = CreateFactory(database.FilePath);
        var exception = Assert.Throws<InvalidOperationException>(() => olderApp.CreateClient());
        Assert.Contains("扱えないMigration履歴", exception.Message);
        Assert.Equal(before, File.ReadAllBytes(database.FilePath));
    }

    [Fact]
    public async Task ExistingSchemaWithoutHistory_StopsUntilBackupIsImplemented()
    {
        using var database = new TestDatabase();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE retained_data (value TEXT); INSERT INTO retained_data VALUES ('保持')";
            command.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(database.FilePath);
        await using var app = CreateFactory(database.FilePath);
        var exception = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("更新前バックアップ", exception.Message);
        Assert.Equal(before, File.ReadAllBytes(database.FilePath));
    }

    [Fact]
    public async Task CorruptDatabase_StopsStartupWithoutReplacingFile()
    {
        using var database = new TestDatabase();
        using (database.OpenConnection()) { }
        var content = Enumerable.Repeat((byte)0x42, 1024).ToArray();
        File.WriteAllBytes(database.FilePath, content);
        await using var app = CreateFactory(database.FilePath);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => app.CreateClient());
        Assert.Equal(content, File.ReadAllBytes(database.FilePath));
    }

    [Fact]
    public async Task MigrationFailure_StopsStartupAndRollsBackApplicationTables()
    {
        using var database = new TestDatabase();
        await using var app = CreateFactory(database.FilePath).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddDbContext<AppDbContext>(options =>
                options.AddInterceptors(new FailingMigrationInterceptor()))));

        var exception = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Equal("Migrationの途中失敗を再現", exception.Message);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN ('projects', 'tasks')";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    private sealed class FailingMigrationInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CREATE TABLE \"tasks\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Migrationの途中失敗を再現");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ParentDirectoryIsFile_StopsStartupWithoutChangingFile()
    {
        using var database = new TestDatabase();
        using (database.OpenConnection()) { }
        File.WriteAllText(database.FilePath, "保持");
        await using var app = CreateFactory(Path.Combine(database.FilePath, "nested.db"));
        Assert.Throws<IOException>(() => app.CreateClient());
        Assert.Equal("保持", File.ReadAllText(database.FilePath));
    }
}
