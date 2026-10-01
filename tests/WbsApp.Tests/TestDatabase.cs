using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace WbsApp.Tests;

public sealed class TestDatabase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WbsApp.Tests", Guid.NewGuid().ToString("N"));
    public string FilePath => Path.Combine(_directory, "wbsapp.db");

    public SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(_directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            File.Delete(file);
        }

        Directory.Delete(_directory);
    }
}

public sealed class IsolatedAppFactory : WebApplicationFactory<Program>
{
    private readonly TestDatabase _database = new();
    public string DatabasePath => _database.FilePath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Path", DatabasePath);
        builder.UseSetting("Logging:Directory", Path.GetDirectoryName(DatabasePath));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _database.Dispose();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
