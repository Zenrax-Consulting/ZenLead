namespace ZenLead.Application.Abstractions;

public interface IBlobStorage
{
    Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default);   // null if missing; the stream is seekable
    Task DeleteAsync(string path, CancellationToken ct = default);
}
