using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Abstractions;
using ZenLead.Infrastructure.Persistence;

namespace ZenLead.Tests.Support;

public class FakeCurrentWorkspace(Guid? id = null) : ICurrentWorkspace
{
    public Guid? WorkspaceId { get; set; } = id;
}

/// <summary>One shared in-memory SQLite database, many contexts — each with its own "current workspace", like separate requests.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var ctx = CreateContext(null);
        ctx.Database.EnsureCreated();
    }

    public ZenLeadDbContext CreateContext(Guid? workspaceId)
        => new(new DbContextOptionsBuilder<ZenLeadDbContext>().UseSqlite(_connection).Options, new FakeCurrentWorkspace(workspaceId));

    public void Dispose() => _connection.Dispose();
}
