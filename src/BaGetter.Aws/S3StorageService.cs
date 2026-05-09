using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using BaGetter.Core;
using Microsoft.Extensions.Options;

namespace BaGetter.Aws;

public class S3StorageService : IStorageService
{
    private const string Separator = "/";
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly AmazonS3Client _client;

    public S3StorageService(IOptionsSnapshot<S3StorageOptions> options, AmazonS3Client client)
    {
        ArgumentNullException.ThrowIfNull(options);

        _bucket = options.Value.Bucket;
        _prefix = options.Value.Prefix;
        _client = client ?? throw new ArgumentNullException(nameof(client));

        if (!string.IsNullOrEmpty(_prefix) && !_prefix.EndsWith(Separator))
            _prefix += Separator;
    }

    private string PrepareKey(string path)
    {
        return _prefix + path.Replace("\\", Separator);
    }

    public async Task<Stream> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = new MemoryStream();

        try
        {
            using (var request = await _client.GetObjectAsync(_bucket, PrepareKey(path), cancellationToken))
            {
                await request.ResponseStream.CopyToAsync(stream, cancellationToken);
            }

            stream.Seek(0, SeekOrigin.Begin);
        }
        catch (Exception)
        {
            stream.Dispose();

            // TODO
            throw;
        }

        return stream;
    }

    public Task<Uri> GetDownloadUriAsync(string path, CancellationToken cancellationToken = default)
    {
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = PrepareKey(path)
        });

        return Task.FromResult(new Uri(url));
    }

    /// <summary>
    /// Stores content in S3.
    /// </summary>
    /// <remarks>
    /// Uses the S3 conditional write header (<c>If-None-Match: *</c>) — generally available
    /// as of November 2024 and supported in all AWS regions — so that two concurrent Lambda
    /// invocations uploading the same key cannot silently overwrite each other.
    /// <para>
    /// When the object already exists:
    /// <list type="bullet">
    ///   <item>Same content (matching MD5) → <see cref="StoragePutResult.AlreadyExists"/>.</item>
    ///   <item>Different content → <see cref="StoragePutResult.Conflict"/>.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public async Task<StoragePutResult> PutAsync(string path, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var seekableContent = new MemoryStream();
        await content.CopyToAsync(seekableContent, 4096, cancellationToken);
        seekableContent.Seek(0, SeekOrigin.Begin);

        var putRequest = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = PrepareKey(path),
            InputStream = seekableContent,
            ContentType = contentType,
            AutoResetStreamPosition = false,
            AutoCloseStream = false
        };

        // Conditional write: reject if the key already exists (GA Nov 2024, all regions).
        putRequest.Headers["If-None-Match"] = "*";

        try
        {
            await _client.PutObjectAsync(putRequest, cancellationToken);
            return StoragePutResult.Success;
        }
        catch (AmazonS3Exception ex)
            when (ex.StatusCode == HttpStatusCode.PreconditionFailed
               || string.Equals(ex.ErrorCode, "PreconditionFailed", StringComparison.OrdinalIgnoreCase))
        {
            // The object already exists. Check whether it has the same content.
            seekableContent.Seek(0, SeekOrigin.Begin);
            var uploadedMd5 = ComputeMd5Base64(seekableContent);

            var metadata = await _client.GetObjectMetadataAsync(_bucket, PrepareKey(path), cancellationToken);

            // S3 ETag for non-multipart uploads is the hex MD5 of the object.
            var existingEtag = metadata.ETag?.Trim('"');
            var existingMd5Hex = existingEtag;

            // Convert our base64 MD5 to hex for comparison.
            var uploadedMd5Hex = Convert.ToHexString(Convert.FromBase64String(uploadedMd5)).ToLowerInvariant();

            return string.Equals(uploadedMd5Hex, existingMd5Hex, StringComparison.OrdinalIgnoreCase)
                ? StoragePutResult.AlreadyExists
                : StoragePutResult.Conflict;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        await _client.DeleteObjectAsync(_bucket, PrepareKey(path), cancellationToken);
    }

    private static string ComputeMd5Base64(Stream stream)
    {
        var hash = MD5.HashData(stream);
        return Convert.ToBase64String(hash);
    }
}

