using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WbsApp.Infrastructure.Logging;

public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private DateOnly? _lastCleanup;
    public string DirectoryPath { get; }

    public DailyFileLoggerProvider(IConfiguration configuration, TimeProvider clock)
    {
        _clock = clock;
        var path = configuration["Logging:Directory"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WbsApp", "Logs");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("Logging:Directoryにはログ保存先の絶対パスを指定してください。");
        }
        DirectoryPath = Path.GetFullPath(path);
        Directory.CreateDirectory(DirectoryPath);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (_gate)
        {
            try
            {
                var now = _clock.GetLocalNow();
                var today = DateOnly.FromDateTime(now.DateTime);
                if (_lastCleanup != today)
                {
                    foreach (var file in Directory.EnumerateFiles(DirectoryPath, "wbs-*.log"))
                    {
                        var name = Path.GetFileName(file);
                        if (Regex.IsMatch(name, @"^wbs-\d{4}-\d{2}-\d{2}\.log$") &&
                            DateOnly.TryParseExact(name[4..14], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var day) && day < today.AddDays(-29))
                        {
                            try { File.Delete(file); }
                            catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
                            {
                                Console.Error.WriteLine("古いログを削除できません。今回のログ記録は継続します。");
                            }
                        }
                    }
                    _lastCleanup = today;
                }

                // Exception messages may contain SQL or user input. Store types and stack locations only.
                var causes = new List<object>();
                for (var cause = exception; cause is not null; cause = cause.InnerException)
                {
                    causes.Add(new { Type = cause.GetType().FullName, cause.StackTrace });
                }
                var entry = JsonSerializer.Serialize(new
                {
                    Timestamp = now.ToUniversalTime(), Level = level.ToString(), Category = category,
                    EventId = eventId.Id, Message = message, Exceptions = causes
                });
                File.AppendAllText(Path.Combine(DirectoryPath, $"wbs-{today:yyyy-MM-dd}.log"), entry + Environment.NewLine);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A logging failure must not replace the original application exception.
                Console.Error.WriteLine("ファイルログを書き込めません。ログ保存先の空き容量とアクセス権を確認してください。");
            }
        }
    }

    private sealed class FileLogger(DailyFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // Framework request/SQL logs can contain form values, query strings or SQL; keep them out of the file.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel < LogLevel.None &&
            (category.StartsWith("WbsApp.", StringComparison.Ordinal) || category == "Microsoft.Hosting.Lifetime");

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
            }
        }
    }
}
