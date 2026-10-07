using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Persistence;

public class EfUnitOfWork(ZenLeadDbContext db) : IUnitOfWork
{
    public async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await action();
        await transaction.CommitAsync(ct);
        return result; // disposing without commit (exception path) rolls back
    }
}
