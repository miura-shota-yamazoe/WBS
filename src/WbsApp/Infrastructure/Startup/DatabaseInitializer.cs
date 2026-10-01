using Microsoft.EntityFrameworkCore;
using WbsApp.Data;

namespace WbsApp.Infrastructure.Startup;

public sealed class DatabaseInitializer(
    AppDbContext dbContext,
    DatabaseLocation location,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(location.FilePath)!);
            // Read history before Migrate so an older executable cannot alter a newer DB.
            var known = dbContext.Database.GetMigrations().ToArray();
            var applied = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            if (applied.Except(known, StringComparer.Ordinal).Any())
            {
                throw new InvalidOperationException("このアプリで扱えないMigration履歴があります。対応するアプリ版で起動してください。");
            }

            var pending = known.Except(applied, StringComparer.Ordinal).ToArray();
            if (pending.Length > 0)
            {
                // Existing databases must wait for the pre-migration backup integration (T17c).
                // An empty SQLite file, including one opened while reading history, is safe to initialize.
                await dbContext.Database.OpenConnectionAsync(cancellationToken);
                try
                {
                    await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                    var tableCount = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
                    if (tableCount > 0)
                    {
                        throw new InvalidOperationException("既存DBの更新には更新前バックアップが必要です。T17cの実装まで更新を停止します。");
                    }
                }
                finally
                {
                    await dbContext.Database.CloseConnectionAsync();
                }

                await dbContext.Database.MigrateAsync(cancellationToken);
            }

            logger.LogInformation("DB初期化を完了しました。適用Migration数: {MigrationCount}", known.Length);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "DB初期化に失敗したため、Webサーバーの起動を中止します。");
            throw;
        }
    }
}
