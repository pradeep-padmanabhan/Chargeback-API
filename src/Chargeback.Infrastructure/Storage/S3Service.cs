using System.Globalization;

namespace Chargeback.Infrastructure.Storage;

/// <summary>A one-time pre-signed PUT: the client uploads the bytes straight to S3; the API never streams them.</summary>
public sealed record PresignedUpload(Uri Url, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> RequiredHeaders);

/// <summary>Private evidence bucket (common guide §5). External calls: never inside a database transaction.</summary>
public interface IS3Service
{
    /// <summary>A new URL per call (no reuse), valid for <paramref name="ttl"/>, bound to the key, content type and length.</summary>
    Task<PresignedUpload> CreatePresignedPutAsync(string key, string contentType, long contentLength, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>Whether the object has been uploaded (HEAD). Used when the client confirms an upload.</summary>
    Task<bool> ObjectExistsAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// KNOWN_LIMITATION_S3_: bucket names and IAM policy are not confirmed (guide §8 #29), so no real S3 client exists.
/// Returns a plausible pre-signed URL on a reserved <c>.invalid</c> host (nothing can be uploaded to it) and reports
/// every object as present, so the rest of the flow can be exercised end to end. Replace before any shared environment.
/// </summary>
public sealed class KnownLimitationS3Service(TimeProvider timeProvider) : IS3Service
{
    public const string Marker = "KNOWN_LIMITATION_S3_";
    public const string StubHost = "known-limitation-s3.invalid";

    public Task<PresignedUpload> CreatePresignedPutAsync(string key, string contentType, long contentLength, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var expiresAt = timeProvider.GetUtcNow().Add(ttl);
        var seconds = ((long)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var path = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        var url = new Uri($"https://{StubHost}/{path}?X-Amz-Expires={seconds}&X-Amz-Signature={Marker}{Guid.NewGuid():N}");
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Type"] = contentType,
            ["Content-Length"] = contentLength.ToString(CultureInfo.InvariantCulture),
        };
        return Task.FromResult(new PresignedUpload(url, expiresAt, headers));
    }

    public Task<bool> ObjectExistsAsync(string key, CancellationToken cancellationToken) => Task.FromResult(true);
}
