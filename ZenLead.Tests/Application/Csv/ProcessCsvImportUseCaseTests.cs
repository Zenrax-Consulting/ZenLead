using System.Text;
using ZenLead.Application.Csv;
using ZenLead.Application.UseCases.Leads.Import;
using ZenLead.Application.Leads;
using ZenLead.Domain.Entities;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Csv;

public class ProcessCsvImportUseCaseTests
{
    private readonly CsvImportHarness _h = new();

    private static void AssertReconciles(CsvImportBatch b)
        => Assert.Equal(b.RowCount, b.ImportedCount + b.SkippedDuplicateCount + b.SkippedSuppressedCount + b.ErrorCount);

    [Fact]
    public async Task HappyPath_ImportsEveryRow_AndCompletes()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(30));

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(CsvImportStatus.Completed, batch.Status);
        Assert.Equal(30, batch.ImportedCount);
        Assert.Equal(30, batch.ProcessedRowCount);
        Assert.NotNull(batch.FinishedAt);
        Assert.Equal(30, _h.Store.Leads.Count);
        Assert.All(_h.Store.Leads, l => Assert.Equal(LeadSource.Csv, l.Source));
        Assert.Equal("First3 Last3", _h.Store.Leads.Single(l => l.Email == "u3@acme3.com").Name);
    }

    [Fact]
    public async Task CountsAddUp_AcrossImportedDuplicateSuppressedAndInvalidRows()
    {
        _h.Store.Leads.Add(new Lead { Id = Guid.NewGuid(), WorkspaceId = _h.Workspace, Name = "x", Email = "existing@acme.com", Status = LeadStatus.New, CreatedAt = DateTime.UtcNow });
        _h.Store.Leads.Add(new Lead { Id = Guid.NewGuid(), WorkspaceId = _h.Workspace, Name = "x", Email = "gone@acme.com", Status = LeadStatus.Unsubscribed, CreatedAt = DateTime.UtcNow });
        var csv = string.Join("\n",
            "Email,First,Last,Title,Company",
            "new1@acme.com,A,A,CTO,Acme",
            "NEW1@acme.com,A,A,CTO,Acme",          // duplicate within the file (case-insensitive)
            "existing@acme.com,B,B,CTO,Acme",      // already in the workspace
            "gone@acme.com,C,C,CTO,Acme",          // unsubscribed
            "not-an-email,D,D,CTO,Acme",           // invalid
            ",E,E,CTO,Acme",                       // blank email
            "bad@acme.com,F\"F,F,CTO,Acme",        // malformed quoting
            "new2@acme.com,G,G,CTO,Acme") + "\n";
        var batch = await _h.UploadAndStartAsync(csv);

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(8, batch.RowCount);
        Assert.Equal(2, batch.ImportedCount);
        Assert.Equal(2, batch.SkippedDuplicateCount);
        Assert.Equal(1, batch.SkippedSuppressedCount);
        Assert.Equal(3, batch.ErrorCount);
        AssertReconciles(batch);
        var issues = CsvIssueLog.Load(batch.ErrorLogJson, 1000).Issues;
        Assert.Equal(6, issues.Count);
        Assert.Contains(issues, i => i.Row == 4 && i.Outcome == IngestionOutcome.Suppressed);
        Assert.Contains(issues, i => i.Row == 7 && i.Outcome == IngestionOutcome.Invalid);
    }

    [Fact]
    public async Task Batches_CommitSeparately_EveryFiveHundredRows()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(1200));
        _h.Batches.UpdateCheckpoints.Clear();

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal([500, 1000, 1200, 1200], _h.Batches.UpdateCheckpoints);   // three flushes, then the completion save
        Assert.Equal(1200, batch.ImportedCount);
    }

    [Fact]
    public async Task ReRunningACompletedBatch_IsANoOp()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(10));
        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);
        _h.Batches.UpdateCheckpoints.Clear();

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Empty(_h.Batches.UpdateCheckpoints);
        Assert.Equal(10, batch.ImportedCount);
        Assert.Equal(10, _h.Store.Leads.Count);
    }

    [Fact]
    public async Task ImportingTheSameFileAsANewBatch_ImportsNothing_AndReportsDuplicates()
    {
        var csv = CsvImportHarness.Csv(25);
        var first = await _h.UploadAndStartAsync(csv);
        await _h.Process.ExecuteAsync(first.Id, _h.Workspace, CancellationToken.None);

        var second = await _h.UploadAndStartAsync(csv);
        await _h.Process.ExecuteAsync(second.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(25, second.SkippedDuplicateCount);
        Assert.Equal(25, _h.Store.Leads.Count);
        AssertReconciles(second);
    }

    [Fact]
    public async Task CrashAfterTheFirstBatch_ThenRerun_ResumesWithoutInflatingCounts()
    {
        var crashing = new CrashingIngestionStore(_h.Store, failOnInsertCall: 2);
        _h.IngestionStore = crashing;
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(1200));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None));

        Assert.Equal(500, batch.ProcessedRowCount);
        Assert.Equal(500, batch.ImportedCount);
        Assert.NotEqual(CsvImportStatus.Completed, batch.Status);

        crashing.Armed = false;
        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(CsvImportStatus.Completed, batch.Status);
        Assert.Equal(1200, batch.ImportedCount);
        Assert.Equal(0, batch.SkippedDuplicateCount);
        Assert.Equal(1200, _h.Store.Leads.Count);
        AssertReconciles(batch);
    }

    [Fact]
    public async Task ARerunAfterACrashBetweenInsertAndCheckpoint_NeverCreatesDuplicates()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(10));
        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);
        // pretend the checkpoint never made it to the database
        batch.Status = CsvImportStatus.Parsing; batch.ProcessedRowCount = 0; batch.ImportedCount = 0;

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(10, _h.Store.Leads.Count);
        Assert.Equal(10, batch.SkippedDuplicateCount);
    }

    [Fact]
    public async Task MissingBlob_FailsTheBatchWithAMessage()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(5));
        _h.Blobs.Blobs.Clear();

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(CsvImportStatus.Failed, batch.Status);
        Assert.Contains("no longer available", batch.FailureReason);
        Assert.NotNull(batch.FinishedAt);
    }

    [Fact]
    public async Task MissingMapping_FailsTheBatch()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(5));
        batch.ColumnMappingJson = null;

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(CsvImportStatus.Failed, batch.Status);
        Assert.Equal("Column mapping missing.", batch.FailureReason);
    }

    [Fact]
    public async Task ErrorLog_IsCappedAtOneThousand_WhileErrorCountStaysTrue()
    {
        var csv = "Email,First,Last,Title,Company\n" + string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"bad{i},F,L,CTO,Acme")) + "\n";
        var batch = await _h.UploadAndStartAsync(csv);

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(1500, batch.ErrorCount);
        Assert.Equal(1000, CsvIssueLog.Load(batch.ErrorLogJson, 1000).Issues.Count);
        AssertReconciles(batch);
    }

    [Fact]
    public async Task ABatchIdFromAnotherWorkspace_FindsNothing()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(5));

        await _h.Process.ExecuteAsync(batch.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(_h.Store.Leads);
        Assert.Equal(CsvImportStatus.Parsing, batch.Status);
    }

    [Fact]
    public async Task Latin1File_IsImportedWithTheAccentsIntact()
    {
        var bytes = Encoding.Latin1.GetBytes("Email;First;Last;Title;Company\nrene@acme.com;René;Dupont;CTO;Acme\n");
        var batch = await _h.UploadAndStartAsync("", bytes: bytes);

        await _h.Process.ExecuteAsync(batch.Id, _h.Workspace, CancellationToken.None);

        Assert.Equal(";", batch.Delimiter);
        Assert.Equal("René Dupont", _h.Store.Leads.Single().Name);
    }
}
