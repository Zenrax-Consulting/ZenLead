namespace ZenLead.Application.Abstractions;

public interface IUnitOfWork
{
    /// <summary>Runs <paramref name="action"/> in one transaction: commits if it returns, rolls back if it throws.</summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken ct = default);
}
