namespace JACO.Unified.Infrastructure;

public sealed record StoredFile(string StoredFileName, string RelativePath);

// One storage location for every type's attachments -- no more "upload locally, then
// transfer over HTTP to a separate Approval process" dance. Stored filename is a GUID
// (never the user-supplied name), so path traversal isn't reachable regardless of what a
// caller names the file.
public sealed class RequestAttachmentStorage(string rootPath)
{
    public async Task<StoredFile> SaveAsync(long requestId, string originalFileName, Stream content, CancellationToken ct = default)
    {
        var dir = Path.Combine(rootPath, requestId.ToString());
        Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(originalFileName);
        var storedFileName = $"{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(dir, storedFileName);

        await using var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write);
        await content.CopyToAsync(fs, ct);

        return new StoredFile(storedFileName, Path.Combine(requestId.ToString(), storedFileName));
    }

    public string GetPath(long requestId, string storedFileName) =>
        Path.Combine(rootPath, requestId.ToString(), storedFileName);
}
