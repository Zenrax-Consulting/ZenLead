using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Enums;
using ZenLead.Infrastructure.Csv;
using ZenLead.Infrastructure.Storage;
using ZenLead.IntegrationTests.Support;
using ZenLead.Tests.Support;

namespace ZenLead.IntegrationTests.Storage;

/// <summary>
/// CSV import storage (F15) on Azurite: the blob round trip, a 10 MB file streamed through the CSV reader without being loaded whole,
/// and a full import through the API, Hangfire on SQL Server and Azurite (parking-lot F15 Azurite item). Skipped, with the reason,
/// when Azurite isn't running. Moved here from ZenLead.Tests, where it needed AZURITE=1.
/// </summary>
[Trait("Category", TestCategories.Integration)]
[Trait("Category", TestCategories.Azurite)]
public class AzuriteBlobStorageTests(AzuriteFixture azurite, IntegrationApiFactory factory)
    : IClassFixture<AzuriteFixture>, IClassFixture<IntegrationApiFactory>, IAsyncLifetime
{
    private readonly List<string> _paths = [];

    private AzureBlobStorage Storage() => new(new BlobServiceClient(AzuriteFixture.ConnectionString));

    private string NewPath() { var p = $"integration-tests/{Guid.NewGuid():N}.csv"; _paths.Add(p); return p; }

    public Task InitializeAsync() => factory.ResetAsync();

    public async Task DisposeAsync()
    {
        if (azurite.UnavailableReason is not null) return;
        foreach (var path in _paths) await Storage().DeleteAsync(path);
    }

    [SkippableFact]
    public async Task SaveOpenDelete_RoundTrip_AndTheStreamIsSeekable()
    {
        azurite.SkipIfUnavailable();
        var path = NewPath();
        var storage = Storage();

        await storage.SaveAsync(path, new MemoryStream(Encoding.UTF8.GetBytes("Email\na@x.com\nb@x.com\n")), "text/csv");
        await using (var stream = (await storage.OpenReadAsync(path))!)
        {
            Assert.True(stream.CanSeek);
            var preview = await new CsvHelperRowReader().PreviewAsync(stream, 5, 100);     // the reader rewinds the stream itself
            Assert.Equal(2, preview.RowCount);
        }

        await storage.DeleteAsync(path);
        Assert.Null(await storage.OpenReadAsync(path));
    }

    [SkippableFact]
    public async Task OpenRead_OfAMissingBlob_ReturnsNull()
    {
        azurite.SkipIfUnavailable();

        Assert.Null(await Storage().OpenReadAsync($"integration-tests/{Guid.NewGuid():N}.csv"));
    }

    [SkippableFact]
    public async Task ATenMegabyteCsv_StreamsFromBlobThroughTheReader_WithoutBeingLoadedWhole()
    {
        azurite.SkipIfUnavailable();
        var path = NewPath();
        var (csv, rows) = BuildCsv(targetBytes: 10 * 1024 * 1024);
        await Storage().SaveAsync(path, new MemoryStream(csv), "text/csv");
        csv = null!;                                            // only the blob holds the file from here on

        await using var stream = (await Storage().OpenReadAsync(path))!;
        Assert.IsNotType<MemoryStream>(stream);
        var reader = new CsvHelperRowReader();
        var preview = await reader.PreviewAsync(stream, 3, 1_000_000);
        Assert.Equal(rows, preview.RowCount);

        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;
        var read = 0;
        await foreach (var row in reader.ReadAsync(stream, preview.Delimiter))
        {
            Assert.NotNull(row.Row);
            if (++read % 25_000 == 0) peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
        }

        Assert.Equal(rows, read);
        // loose: the file is 10 MB of UTF-8 (≈ 20 MB as .NET strings); streaming keeps the live heap to buffers and a few rows
        Assert.True(peak < 8 * 1024 * 1024, $"Live heap grew by {peak / 1024 / 1024} MB while reading: the file looks fully buffered.");
    }

    [SkippableFact]
    public async Task ACsvImport_UploadedThroughTheApi_IsProcessedByHangfire_FromAzurite()
    {
        azurite.SkipIfUnavailable();
        var (workspaceId, _, token) = await factory.CreateWorkspaceAndUserAsync();
        var client = factory.ClientFor(token);
        const string csv = "Full Name;Work Email;Company\nAda Lovelace;ada@acme.example.test;Acme\nAda Again;ADA@acme.example.test;Acme\n" +
                           "No Email;;Acme\nGrace Hopper;grace@navy.example.test;Navy\n";

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "leads.csv");
        var upload = await (await client.PostAsync("/api/v1/leads/import", form)).ReadAsync<CsvUploadResponse>();
        Assert.Equal(4, upload.RowCount);
        Assert.Equal("Work Email", upload.SuggestedMapping.Email);

        var start = await client.PostJsonAsync($"/api/v1/leads/import/{upload.BatchId}/start",
            new ColumnMappingRequest("Full Name", null, null, "Work Email", null, "Company", null, null, null, null));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        await using (var server = HangfireTestServer.For(factory.Services))
            await server.WaitForIdleAsync();

        var status = await (await client.GetAsync($"/api/v1/leads/import/{upload.BatchId}/status")).ReadAsync<ImportStatusResponse>();
        Assert.Equal(CsvImportStatus.Completed, status.Status);
        Assert.Equal(4, status.ProcessedRowCount);
        Assert.Equal(2, status.ImportedCount);
        Assert.Equal(1, status.SkippedDuplicateCount);           // same address, different case
        Assert.Equal(1, status.ErrorCount);                      // no email
        await using var db = factory.Sql.CreateContext();
        Assert.Equal(2, await db.Leads.IgnoreQueryFilters().CountAsync(l => l.WorkspaceId == workspaceId && l.Source == LeadSource.Csv));
        _paths.Add($"imports/{workspaceId}/{upload.BatchId}.csv");
    }

    private static (byte[] Bytes, int Rows) BuildCsv(int targetBytes)
    {
        var sb = new StringBuilder("Name,Email,Company,Title\n", targetBytes + 200);
        var rows = 0;
        while (sb.Length < targetBytes)
        {
            sb.Append("Person ").Append(rows).Append(",person").Append(rows).Append("@bulk.example.test,Bulk Co ")
              .Append(rows % 500).Append(",Head of Operations\n");
            rows++;
        }
        return (Encoding.UTF8.GetBytes(sb.ToString()), rows);
    }
}
