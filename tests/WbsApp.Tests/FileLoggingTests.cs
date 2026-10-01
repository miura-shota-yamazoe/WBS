using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WbsApp.Infrastructure.Logging;
using Xunit;

namespace WbsApp.Tests;

public sealed class FileLoggingTests
{
    private static IConfiguration Configuration(string path) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Logging:Directory"] = path }).Build();

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void InvalidDirectory_IsRejected(string path) =>
        Assert.Throws<InvalidOperationException>(() => new DailyFileLoggerProvider(Configuration(path), TimeProvider.System));

    [Fact]
    public void DailyRollover_Keeps30CalendarDaysAndDoesNotDeleteUnrelatedFiles()
    {
        using var database = new TestDatabase();
        var directory = Path.GetDirectoryName(database.FilePath)!;
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "wbs-2026-09-01.log", "wbs-2026-09-02.log", "wbs-2026-11-01.log", "manual.log", "wbs-unrecognized.log" })
            File.WriteAllText(Path.Combine(directory, name), "保持条件");
        var clock = new TestClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new DailyFileLoggerProvider(Configuration(directory), clock);
        var logger = provider.CreateLogger("WbsApp.Tests");
        Parallel.For(0, 100, index => logger.LogInformation("記録 {Index}", index));
        var lines = File.ReadAllLines(Path.Combine(directory, "wbs-2026-10-01.log"));
        Assert.Equal(100, lines.Length);
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal("Information", json.RootElement.GetProperty("Level").GetString());
            Assert.Equal(clock.GetUtcNow(), json.RootElement.GetProperty("Timestamp").GetDateTimeOffset());
        }
        Assert.False(File.Exists(Path.Combine(directory, "wbs-2026-09-01.log")));
        Assert.True(File.Exists(Path.Combine(directory, "wbs-2026-09-02.log")));
        clock.Instant = clock.Instant.AddDays(1);
        logger.LogWarning("翌日の記録");
        Assert.True(File.Exists(Path.Combine(directory, "wbs-2026-10-02.log")));
        Assert.False(File.Exists(Path.Combine(directory, "wbs-2026-09-02.log")));
        foreach (var name in new[] { "wbs-2026-11-01.log", "manual.log", "wbs-unrecognized.log" })
            Assert.True(File.Exists(Path.Combine(directory, name)));
    }

    [Fact]
    public void FrameworkSqlAndExceptionMessages_AreNotWrittenToFile()
    {
        using var database = new TestDatabase();
        var directory = Path.GetDirectoryName(database.FilePath)!;
        using var provider = new DailyFileLoggerProvider(Configuration(directory), TimeProvider.System);
        provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogError("SELECT private-input");
        provider.CreateLogger("Microsoft.AspNetCore.Request").LogError("private-query");
        provider.CreateLogger("WbsApp.Tests").LogError(new InvalidOperationException("private-exception"), "安全なエラーID: ABC123");
        var log = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(directory, "wbs-*.log")));
        Assert.Contains("ABC123", log);
        Assert.Contains("InvalidOperationException", log);
        Assert.DoesNotContain("private-", log);
        Assert.DoesNotContain("SELECT", log);
    }

    [Fact]
    public void UnwritableLog_DoesNotReplaceApplicationException()
    {
        using var database = new TestDatabase();
        var directory = Path.GetDirectoryName(database.FilePath)!;
        var clock = new TestClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new DailyFileLoggerProvider(Configuration(directory), clock);
        var blockedFile = Path.Combine(directory, "wbs-2026-10-01.log");
        Directory.CreateDirectory(blockedFile);
        try
        {
            var exception = Record.Exception(() => provider.CreateLogger("WbsApp.Tests").LogError("記録できないエラー"));
            Assert.Null(exception);
        }
        finally { Directory.Delete(blockedFile); }
    }

    [Fact]
    public void LockedOldLog_DoesNotPreventCurrentLogFromBeingWritten()
    {
        using var database = new TestDatabase();
        var directory = Path.GetDirectoryName(database.FilePath)!;
        Directory.CreateDirectory(directory);
        var oldFile = Path.Combine(directory, "wbs-2026-09-01.log");
        File.WriteAllText(oldFile, "削除できないログ");
        using var held = new FileStream(oldFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var clock = new TestClock(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        using var provider = new DailyFileLoggerProvider(Configuration(directory), clock);
        provider.CreateLogger("WbsApp.Tests").LogInformation("現在の記録");
        Assert.True(File.Exists(oldFile));
        Assert.Single(File.ReadAllLines(Path.Combine(directory, "wbs-2026-10-01.log")));
    }

    private sealed class TestClock(DateTimeOffset instant) : TimeProvider
    {
        public DateTimeOffset Instant { get; set; } = instant;
        public override DateTimeOffset GetUtcNow() => Instant;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
