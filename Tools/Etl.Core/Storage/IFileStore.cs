namespace Etl.Core.Storage;

/// <summary>
/// Where a package's files live. Every file-touching Etl.Core type (flat file / Excel / XML
/// sources, the Flat File Destination, File System Task, ForEach Loop) goes through this instead
/// of System.IO directly, so a generated package can run against the local filesystem or an
/// object store (S3) without changing any of those types. One store per package, never mixed.
///
/// Paths are opaque strings to callers -- the store decides what they mean (a filesystem path, or
/// an object key under a bucket). Folder/name joining must go through <see cref="Combine"/>.
/// </summary>
public interface IFileStore
{
    /// <summary>Joins a folder and a file name using this store's own separator convention.</summary>
    string Combine(string folder, string name);

    Task<bool> ExistsAsync(string path, CancellationToken ct);

    /// <summary>Opens for sequential reading. A store may buffer the whole object if it cannot stream.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    /// <summary>
    /// Opens for writing, creating any missing parent folder. <paramref name="append"/> is honored
    /// by the local store only; an object store cannot append and always replaces the object.
    /// </summary>
    Task<Stream> OpenWriteAsync(string path, bool append, CancellationToken ct);

    /// <summary>Paths of files in <paramref name="folder"/> matching a glob <paramref name="pattern"/> (* and ?), ordinal-sorted.</summary>
    Task<IReadOnlyList<string>> ListAsync(string folder, string pattern, bool recurse, CancellationToken ct);

    Task CopyAsync(string source, string destination, bool overwrite, CancellationToken ct);

    /// <summary>Moves (or renames) a file. With <paramref name="overwrite"/> an existing destination is replaced.</summary>
    Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct);

    /// <summary>Deletes a file; a missing file is not an error (matches File.Delete).</summary>
    Task DeleteAsync(string path, CancellationToken ct);

    /// <summary>Creates a folder if missing. A store with no real folders may no-op.</summary>
    Task EnsureFolderAsync(string path, CancellationToken ct);
}
