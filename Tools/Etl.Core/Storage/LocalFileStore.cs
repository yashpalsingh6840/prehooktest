namespace Etl.Core.Storage;

/// <summary>The local filesystem -- exactly the System.IO behavior these types had before <see cref="IFileStore"/> existed.</summary>
public sealed class LocalFileStore : IFileStore
{
    public static LocalFileStore Instance { get; } = new();

    public string Combine(string folder, string name) => Path.Combine(folder, name);

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(File.Exists(path));

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
        Task.FromResult<Stream>(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));

    public Task<Stream> OpenWriteAsync(string path, bool append, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return Task.FromResult<Stream>(new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None));
    }

    public Task<IReadOnlyList<string>> ListAsync(string folder, string pattern, bool recurse, CancellationToken ct)
    {
        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        IReadOnlyList<string> files = Directory.EnumerateFiles(folder, pattern, option)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        return Task.FromResult(files);
    }

    public Task CopyAsync(string source, string destination, bool overwrite, CancellationToken ct)
    {
        File.Copy(source, destination, overwrite);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct)
    {
        if (overwrite && File.Exists(destination)) File.Delete(destination);
        File.Move(source, destination);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        File.Delete(path);
        return Task.CompletedTask;
    }

    public Task EnsureFolderAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }
}
