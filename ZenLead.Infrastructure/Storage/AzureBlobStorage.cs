using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ZenLead.Application.Abstractions;

namespace ZenLead.Infrastructure.Storage;

public class AzureBlobStorage(BlobServiceClient service) : IBlobStorage
{
    private const string Container = "zenlead";

    private async Task<BlobContainerClient> ContainerAsync(CancellationToken ct)
    {
        var c = service.GetBlobContainerClient(Container);
        await c.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        return c;
    }

    public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct = default)
        => await (await ContainerAsync(ct)).GetBlobClient(path)
            .UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var blob = (await ContainerAsync(ct)).GetBlobClient(path);
        return await blob.ExistsAsync(ct) ? await blob.OpenReadAsync(cancellationToken: ct) : null;
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
        => await (await ContainerAsync(ct)).GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct);
}
