using System.Text;
using Azure.Storage.Blobs;
using ZenLead.Infrastructure.Csv;
using ZenLead.Infrastructure.Storage;

namespace ZenLead.Tests.Infrastructure.Storage;

/// <summary>Needs Azurite on the default ports. Skipped unless AZURITE=1, so the default CI run never touches it.</summary>
[Trait("Category", "Azurite")]
public class AzureBlobStorageTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("AZURITE") == "1";

    private static AzureBlobStorage Storage() => new(new BlobServiceClient("UseDevelopmentStorage=true"));

    [Fact]
    public async Task SaveOpenDelete_RoundTrip_AndTheStreamIsSeekable()
    {
        if (!Enabled) return;
        var path = $"tests/{Guid.NewGuid()}.csv";
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

    [Fact]
    public async Task OpenRead_OfAMissingBlob_ReturnsNull()
    {
        if (!Enabled) return;

        Assert.Null(await Storage().OpenReadAsync($"tests/{Guid.NewGuid()}.csv"));
    }
}
