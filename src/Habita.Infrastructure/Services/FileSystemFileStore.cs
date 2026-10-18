using Habita.Application.Files;
using Microsoft.Extensions.Configuration;

namespace Habita.Infrastructure.Services;

/// <summary>
/// Stores files under <c>Storage:Root</c> as {organizationId}/{fileId}. Names are generated from GUIDs, never from user input,
/// so a caller cannot choose or traverse a path. The folder must be a private volume, never served by the web server.
/// </summary>
public class FileSystemFileStore : IFileStore
{
    private readonly string _root;

    public FileSystemFileStore(IConfiguration config) =>
        _root = Path.GetFullPath(config["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, "data", "files"));

    private string PathFor(Guid organizationId, Guid fileId) => Path.Combine(_root, organizationId.ToString("N"), fileId.ToString("N"));

    public async Task WriteAsync(Guid organizationId, Guid fileId, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var path = PathFor(organizationId, fileId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content.ToArray(), ct);
    }

    public Task<Stream?> OpenAsync(Guid organizationId, Guid fileId, CancellationToken ct)
    {
        var path = PathFor(organizationId, fileId);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true) : null);
    }

    public Task DeleteAsync(Guid organizationId, Guid fileId, CancellationToken ct)
    {
        var path = PathFor(organizationId, fileId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
