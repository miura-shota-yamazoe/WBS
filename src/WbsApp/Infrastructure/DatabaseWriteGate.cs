namespace WbsApp.Infrastructure;

// One instance per app host, shared by every request and write service.
public sealed class DatabaseWriteGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    internal Task WaitAsync(CancellationToken token) => _semaphore.WaitAsync(token);
    internal void Release() => _semaphore.Release();
    public void Dispose() => _semaphore.Dispose();
}
