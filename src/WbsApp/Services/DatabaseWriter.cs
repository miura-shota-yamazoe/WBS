using Microsoft.EntityFrameworkCore;
using WbsApp.Data;
using WbsApp.Infrastructure;

namespace WbsApp.Services;

public sealed class DatabaseWriter(AppDbContext db, DatabaseWriteGate gate)
{
    // Compose related DB operations in this callback; do not nest writer/service writes.
    // Intermediate SaveChanges are allowed and remain inside this transaction.
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("書き込みトランザクションを入れ子にできません。");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (db.ChangeTracker.HasChanges())
                throw new InvalidOperationException("書き込み処理の外で未保存の変更があります。");
            // A previous read in this scope must not hide changes committed while waiting.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await operation(cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw; // DisposeAsync rolls back before releasing the gate, even on cancellation.
            }
        }
        finally { gate.Release(); }
    }
}
