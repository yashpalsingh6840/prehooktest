using System.Net;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Etl.Core.Storage;

/// <summary>
/// An <see cref="IFileStore"/> over one S3 bucket. A "path" is an object key: folders are just key
/// prefixes joined with '/', and both '/' and '\' in a caller-supplied path are accepted (a path
/// copied from a Windows-flavoured .dtsx still resolves). Credentials and region come from the
/// standard AWS chain (environment, shared profile, IAM role) -- nothing secret is ever configured
/// here.
///
/// Semantics that differ from <see cref="LocalFileStore"/>, all deliberate:
/// <list type="bullet">
/// <item>No append: an object cannot be appended to, so <c>OpenWriteAsync</c> always replaces it.
/// The write is buffered and uploaded when the stream is disposed (use <c>await using</c>).</item>
/// <item>Folders do not exist: <c>EnsureFolderAsync</c> is a no-op.</item>
/// <item>Move is copy-then-delete; a single CopyObject is limited to 5 GB.</item>
/// <item>Listing is case-insensitive on the pattern (matching SSIS on Windows) but keys stay case-sensitive.</item>
/// </list>
/// </summary>
public sealed class S3FileStore(IAmazonS3 client, string bucket) : IFileStore
{
    /// <summary>Builds a store with a client from the default AWS credential chain. A null region uses the SDK's own resolution.</summary>
    public static S3FileStore Create(string bucket, string? region = null) =>
        new(string.IsNullOrWhiteSpace(region) ? new AmazonS3Client() : new AmazonS3Client(RegionEndpoint.GetBySystemName(region)), bucket);

    public string Combine(string folder, string name)
    {
        var f = Normalize(folder);
        var n = Normalize(name);
        return f.Length == 0 ? n : n.Length == 0 ? f : f + "/" + n;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        try
        {
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = Normalize(path) }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            await ThrowIfBucketMissingAsync(ct);
            return false;
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = Normalize(path) }, ct);
            return new ResponseOwningStream(response);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            await ThrowIfBucketMissingAsync(ct);
            throw new FileNotFoundException($"s3://{bucket}/{Normalize(path)} not found.", path, ex);
        }
    }

    public Task<Stream> OpenWriteAsync(string path, bool append, CancellationToken ct) =>
        Task.FromResult<Stream>(new UploadOnDisposeStream(client, bucket, Normalize(path), ct));

    public async Task<IReadOnlyList<string>> ListAsync(string folder, string pattern, bool recurse, CancellationToken ct)
    {
        var prefix = Normalize(folder);
        if (prefix.Length > 0) prefix += "/";
        var matcher = GlobToRegex(pattern);

        var keys = new List<string>();
        string? token = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, ContinuationToken = token }, ct);
            foreach (var obj in response.S3Objects ?? [])
            {
                var key = obj.Key;
                if (key is null || key.EndsWith('/')) continue; // a console-created "folder" marker, not a file
                var rest = key[prefix.Length..];
                if (!recurse && rest.Contains('/')) continue;
                var name = rest[(rest.LastIndexOf('/') + 1)..];
                if (matcher.IsMatch(name)) keys.Add(key);
            }
            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (token is not null);

        return keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public async Task CopyAsync(string source, string destination, bool overwrite, CancellationToken ct)
    {
        var src = Normalize(source);
        var dst = Normalize(destination);
        if (!overwrite && await ExistsAsync(dst, ct))
            throw new IOException($"s3://{bucket}/{dst} already exists.");
        try
        {
            await client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket, SourceKey = src, DestinationBucket = bucket, DestinationKey = dst,
            }, ct);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            await ThrowIfBucketMissingAsync(ct);
            throw new FileNotFoundException($"s3://{bucket}/{src} not found.", source, ex);
        }
    }

    public async Task MoveAsync(string source, string destination, bool overwrite, CancellationToken ct)
    {
        await CopyAsync(source, destination, overwrite, ct);
        await DeleteAsync(source, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct) =>
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = Normalize(path) }, ct);

    public Task EnsureFolderAsync(string path, CancellationToken ct) => Task.CompletedTask;

    private bool _bucketVerified;

    /// <summary>
    /// A HEAD request cannot tell a missing KEY from a missing BUCKET (both are a bare 404), so on
    /// any "not found" we ask once whether the bucket itself exists. Without this a mistyped
    /// <c>Storage:Bucket</c> surfaces as "file not found: &lt;key&gt;" and sends the reader hunting
    /// for a file that was never the problem.
    /// </summary>
    private async Task ThrowIfBucketMissingAsync(CancellationToken ct)
    {
        if (_bucketVerified) return;
        try
        {
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, ct);
            _bucketVerified = true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
            throw new InvalidOperationException(
                $"The S3 bucket '{bucket}' does not exist (or is in another region) -- check Storage:Bucket and Storage:Region.", ex);
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static bool IsNotFound(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode is "NoSuchKey" or "NotFound";

    private static Regex GlobToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Streams the object body and releases the underlying HTTP response on dispose.</summary>
    private sealed class ResponseOwningStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Buffers writes in memory and uploads them as one PutObject when disposed.</summary>
    private sealed class UploadOnDisposeStream(IAmazonS3 client, string bucket, string key, CancellationToken ct) : MemoryStream
    {
        private bool _uploaded;

        public override async ValueTask DisposeAsync()
        {
            await UploadAsync();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            // Only reached by a synchronous `using`; callers are expected to `await using`.
            if (disposing) UploadAsync().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        private async Task UploadAsync()
        {
            if (_uploaded) return;
            _uploaded = true;
            Position = 0;
            await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = this, AutoCloseStream = false }, ct);
        }
    }
}
