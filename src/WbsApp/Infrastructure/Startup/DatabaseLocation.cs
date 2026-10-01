using Microsoft.Data.Sqlite;

namespace WbsApp.Infrastructure.Startup;

public sealed class DatabaseLocation
{
    public string FilePath { get; }
    public string ConnectionString { get; }

    public DatabaseLocation(IConfiguration configuration)
    {
        var configuredPath = configuration["Database:Path"];
        if (configuredPath is null)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("LOCALAPPDATAの保存先を取得できません。");
            }

            configuredPath = Path.Combine(localAppData, "WbsApp", "Data", "wbsapp.db");
        }

        if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
        {
            throw new InvalidOperationException("Database:PathにはDBファイルの絶対パスを指定してください。");
        }

        FilePath = Path.GetFullPath(configuredPath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }
}
