using System.Text;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Csv;
using ZenLead.Application.Leads;
using ZenLead.Application.UseCases.Leads.Import;
using ZenLead.Domain.Entities;
using ZenLead.Infrastructure.Csv;
using ZenLead.Tests.Application.Discovery;
using ZenLead.Tests.Application.Leads;

namespace ZenLead.Tests.Application.Csv;

public class InMemoryBlobStorage : IBlobStorage
{
    public Dictionary<string, byte[]> Blobs { get; } = [];

    public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Blobs[path] = ms.ToArray();
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
        => Task.FromResult<Stream?>(Blobs.TryGetValue(path, out var b) ? new MemoryStream(b) : null);

    public Task DeleteAsync(string path, CancellationToken ct = default) { Blobs.Remove(path); return Task.CompletedTask; }
}

public class FakeCsvImportRepository : ICsvImportRepository
{
    public List<CsvImportBatch> Batches { get; } = [];
    /// <summary>ProcessedRowCount at each UpdateAsync, to show progress is committed batch by batch.</summary>
    public List<int> UpdateCheckpoints { get; } = [];

    public Task AddAsync(CsvImportBatch batch, CancellationToken ct = default) { Batches.Add(batch); return Task.CompletedTask; }
    public Task<CsvImportBatch?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Batches.FirstOrDefault(b => b.Id == id));
    public Task<CsvImportBatch?> GetForJobAsync(Guid id, Guid workspaceId, CancellationToken ct = default)
        => Task.FromResult(Batches.FirstOrDefault(b => b.Id == id && b.WorkspaceId == workspaceId));
    public Task UpdateAsync(CsvImportBatch batch, CancellationToken ct = default) { UpdateCheckpoints.Add(batch.ProcessedRowCount); return Task.CompletedTask; }
    public Task MarkFailedAsync(Guid id, Guid workspaceId, string reason, CancellationToken ct = default)
    {
        var b = Batches.FirstOrDefault(x => x.Id == id && x.WorkspaceId == workspaceId);
        if (b is not null) { b.Status = ZenLead.Domain.Enums.CsvImportStatus.Failed; b.FailureReason = reason; }
        return Task.CompletedTask;
    }
}

/// <summary>Wraps the in-memory store and throws on the Nth insert, to simulate the worker dying mid-import.</summary>
public class CrashingIngestionStore(FakeLeadIngestionStore inner, int failOnInsertCall) : ILeadIngestionStore
{
    private int _inserts;
    public bool Armed { get; set; } = true;

    public Task<IReadOnlyDictionary<string, ExistingLead>> FindExistingAsync(Guid workspaceId, IReadOnlyCollection<string> emails, CancellationToken ct)
        => inner.FindExistingAsync(workspaceId, emails, ct);

    public Task<IReadOnlyDictionary<CompanyKey, Guid>> UpsertCompaniesAsync(
        Guid workspaceId, IReadOnlyCollection<(CompanyKey Key, string? Industry, string? Country, string? Size)> companies, CancellationToken ct)
        => inner.UpsertCompaniesAsync(workspaceId, companies, ct);

    public Task<IReadOnlySet<Guid>> InsertLeadsAsync(IReadOnlyList<Lead> leads, CancellationToken ct)
    {
        if (Armed && ++_inserts == failOnInsertCall) throw new InvalidOperationException("simulated crash");
        return inner.InsertLeadsAsync(leads, ct);
    }
}

/// <summary>Wires the real use cases and reader to in-memory storage, so import tests exercise upload → start → process end to end.</summary>
public class CsvImportHarness
{
    public Guid Workspace { get; } = Guid.NewGuid();
    public Guid User { get; } = Guid.NewGuid();
    public InMemoryBlobStorage Blobs { get; } = new();
    public FakeCsvImportRepository Batches { get; } = new();
    public FakeLeadIngestionStore Store { get; } = new();
    public FakeJobScheduler Scheduler { get; } = new();
    public CsvHelperRowReader Reader { get; } = new();
    public ILeadIngestionStore IngestionStore { get; set; }

    public CsvImportHarness() => IngestionStore = Store;

    public CreateCsvImportUseCase Create => new(Blobs, Reader, Batches, TimeProvider.System);
    public StartCsvImportUseCase Start => new(Blobs, Reader, Batches, Scheduler, TimeProvider.System);
    public ProcessCsvImportUseCase Process => new(Batches, Blobs, Reader, new LeadIngestionService(IngestionStore), TimeProvider.System);

    public static ColumnMapping DefaultMapping { get; } = new(null, "First", "Last", "Email", "Title", "Company", null, null, null, null);

    public async Task<CsvImportBatch> UploadAndStartAsync(string csv, ColumnMapping? mapping = null, byte[]? bytes = null)
    {
        using var stream = new MemoryStream(bytes ?? Encoding.UTF8.GetBytes(csv));
        var upload = await Create.ExecuteAsync(Workspace, User, "leads.csv", stream, CancellationToken.None);
        var started = await Start.ExecuteAsync(Workspace, upload.BatchId, mapping ?? DefaultMapping, CancellationToken.None);
        Assert.Equal(StartImportOutcome.Started, started.Outcome);
        return Batches.Batches.Single(b => b.Id == upload.BatchId);
    }

    public static string Csv(int rows, string prefix = "u")
        => "Email,First,Last,Title,Company\n" + string.Join("\n", Enumerable.Range(0, rows).Select(i => $"{prefix}{i}@acme{i % 20}.com,First{i},Last{i},CTO,Acme {i % 20}")) + "\n";
}
