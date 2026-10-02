namespace WbsApp.Infrastructure.Clock;

public sealed class AppClock(TimeProvider timeProvider)
{
    public DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;
    public DateOnly Today => DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
}
