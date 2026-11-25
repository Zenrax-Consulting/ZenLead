using System.Text;
using ZenLead.Application.Abstractions;
using ZenLead.Application.Csv;
using ZenLead.Application.UseCases.Leads.Import;
using ZenLead.Domain.Enums;

namespace ZenLead.Tests.Application.Csv;

public class CreateAndStartCsvImportTests
{
    private readonly CsvImportHarness _h = new();

    private Task<global::ZenLead.Application.Dtos.Leads.CsvUploadResult> Upload(string csv)
        => _h.Create.ExecuteAsync(_h.Workspace, _h.User, @"C:\fakepath\..\leads.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)), CancellationToken.None);

    [Fact]
    public async Task Upload_StoresTheFile_AndRecordsAnUploadedBatch()
    {
        var result = await Upload(CsvImportHarness.Csv(12));

        var batch = Assert.Single(_h.Batches.Batches);
        Assert.Equal(result.BatchId, batch.Id);
        Assert.Equal(CsvImportStatus.Uploaded, batch.Status);
        Assert.Equal(12, batch.RowCount);
        Assert.Equal(",", batch.Delimiter);
        Assert.Equal("leads.csv", batch.FileName);                              // only the file name part is kept
        Assert.Equal($"imports/{_h.Workspace}/{batch.Id}.csv", batch.BlobPath);
        Assert.True(_h.Blobs.Blobs.ContainsKey(batch.BlobPath));
        Assert.Equal(10, result.SampleRows.Count);
        Assert.Equal(["Email", "First", "Last", "Title", "Company"], result.Headers);
    }

    [Fact]
    public async Task Upload_OfARejectedFile_DeletesTheBlob_AndCreatesNoBatch()
    {
        await Assert.ThrowsAsync<CsvFormatException>(() => Upload("Email,Name\n"));

        Assert.Empty(_h.Blobs.Blobs);
        Assert.Empty(_h.Batches.Batches);
    }

    [Fact]
    public async Task Start_WithAValidMapping_QueuesTheJob_AfterTheBatchIsSaved()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(3));

        Assert.Equal(CsvImportStatus.Parsing, batch.Status);
        Assert.NotNull(batch.StartedAt);
        Assert.Contains("\"email\":\"Email\"", batch.ColumnMappingJson);
        Assert.Equal([(batch.Id, _h.Workspace)], _h.Scheduler.CsvImports);
    }

    [Fact]
    public async Task Start_NormalisesHeaderCase_ToTheFilesSpelling()
    {
        var upload = await Upload(CsvImportHarness.Csv(3));
        var mapping = CsvImportHarness.DefaultMapping with { Email = "EMAIL", FirstName = "first" };

        var result = await _h.Start.ExecuteAsync(_h.Workspace, upload.BatchId, mapping, CancellationToken.None);

        Assert.Equal(StartImportOutcome.Started, result.Outcome);
        Assert.Contains("\"email\":\"Email\"", _h.Batches.Batches[0].ColumnMappingJson);
        Assert.Contains("\"firstName\":\"First\"", _h.Batches.Batches[0].ColumnMappingJson);
    }

    [Fact]
    public async Task Start_WithAMappingThatNamesAColumnNotInTheFile_IsRejected_AndNothingIsQueued()
    {
        var upload = await Upload(CsvImportHarness.Csv(3));

        var result = await _h.Start.ExecuteAsync(_h.Workspace, upload.BatchId, CsvImportHarness.DefaultMapping with { Email = "Mail" }, CancellationToken.None);

        Assert.Equal(StartImportOutcome.InvalidMapping, result.Outcome);
        Assert.Contains(result.Problems, p => p.Contains("Mail"));
        Assert.Empty(_h.Scheduler.CsvImports);
        Assert.Equal(CsvImportStatus.Uploaded, _h.Batches.Batches[0].Status);
    }

    [Fact]
    public async Task Start_Twice_IsAlreadyStarted()
    {
        var batch = await _h.UploadAndStartAsync(CsvImportHarness.Csv(3));

        var again = await _h.Start.ExecuteAsync(_h.Workspace, batch.Id, CsvImportHarness.DefaultMapping, CancellationToken.None);

        Assert.Equal(StartImportOutcome.AlreadyStarted, again.Outcome);
        Assert.Single(_h.Scheduler.CsvImports);
    }

    [Fact]
    public async Task Start_ForAnotherWorkspacesBatch_IsNotFound()
    {
        var upload = await Upload(CsvImportHarness.Csv(3));

        var result = await _h.Start.ExecuteAsync(Guid.NewGuid(), upload.BatchId, CsvImportHarness.DefaultMapping, CancellationToken.None);

        Assert.Equal(StartImportOutcome.NotFound, result.Outcome);
        Assert.Empty(_h.Scheduler.CsvImports);
    }

    [Fact]
    public async Task Start_WhenTheBlobIsGone_ReportsItAsAProblem()
    {
        var upload = await Upload(CsvImportHarness.Csv(3));
        _h.Blobs.Blobs.Clear();

        var result = await _h.Start.ExecuteAsync(_h.Workspace, upload.BatchId, CsvImportHarness.DefaultMapping, CancellationToken.None);

        Assert.Equal(StartImportOutcome.InvalidMapping, result.Outcome);
        Assert.Contains("no longer available", result.Problems[0]);
    }
}
