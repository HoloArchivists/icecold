using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Icecold.Api.Options;

namespace Icecold.Api.Content;

// Serves files from a static HTTP origin (nginx, S3-compatible endpoints, another archive mirror).
// Paths are relative to BaseUrl; the public can never make Icecold fetch an arbitrary URL.
//
// Content identity comes from the origin's validators: a strong ETag when present, otherwise
// Last-Modified. Every body request carries the expected validator as a precondition and every
// response is checked against it, so a file replaced on the origin fails reads instead of
// silently serving or hashing mixed bytes.
public sealed class HttpContentSource : ISeekableContentSource, IDisposable
{
    const int MaxMetadataCacheEntries = 4096;
    const int MaxEntityTagLength = 400;
    const string EntityTagVersionMarker = "-e";
    const string LastModifiedVersionMarker = "-m";

    readonly Uri baseUri;
    readonly HttpClient client;
    readonly TimeSpan requestTimeout;
    readonly TimeSpan metadataCacheTtl;
    readonly ConcurrentDictionary<string, CachedMetadata> metadataCache = new(StringComparer.Ordinal);

    public HttpContentSource(ContentSourceOptions options, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("Source name is required.", nameof(options));

        Name = options.Name;
        baseUri = ParseBaseUri(options);

        if (options.RequestTimeoutSeconds < 1)
            throw new InvalidOperationException($"HTTP content source '{Name}' RequestTimeoutSeconds must be at least 1.");
        if (options.MaxConnections < 1)
            throw new InvalidOperationException($"HTTP content source '{Name}' MaxConnections must be at least 1.");
        if (options.MetadataCacheSeconds < 0)
            throw new InvalidOperationException($"HTTP content source '{Name}' MetadataCacheSeconds must not be negative.");
        if (options.ReadAheadBytes < 16 * 1024)
            throw new InvalidOperationException($"HTTP content source '{Name}' ReadAheadBytes must be at least 16384.");
        if (options.MaxResumeAttempts < 0)
            throw new InvalidOperationException($"HTTP content source '{Name}' MaxResumeAttempts must not be negative.");

        requestTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        metadataCacheTtl = TimeSpan.FromSeconds(options.MetadataCacheSeconds);
        ReadAheadBytes = options.ReadAheadBytes;
        MaxResumeAttempts = options.MaxResumeAttempts;

        // Redirects are not followed: they would let the origin point Icecold at another host and
        // would carry the configured headers along.
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = requestTimeout,
            MaxConnectionsPerServer = options.MaxConnections,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false
        };

        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Icecold", "1.0"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        foreach (var (header, value) in options.Headers)
        {
            if (!client.DefaultRequestHeaders.TryAddWithoutValidation(header, value))
                throw new InvalidOperationException($"HTTP content source '{Name}' has an invalid header '{header}'.");
        }
    }

    public string Name { get; }

    public bool SupportsEfficientRangeReads => true;

    internal int ReadAheadBytes { get; }

    internal int MaxResumeAttempts { get; }

    internal TimeSpan RequestTimeout => requestTimeout;

    public async Task<ContentMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var target = Resolve(path);
        if (TryGetCachedMetadata(target.Path, out var cached))
            return cached;

        var metadata = await FetchMetadataAsync(target, cancellationToken);
        StoreMetadata(metadata);
        return metadata;
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        var target = Resolve(path);
        var expectedVersion = TryGetCachedMetadata(target.Path, out var cached) ? cached.Version : null;
        return await HttpContentStream.OpenAsync(this, target, 0, length: null, expectedVersion, cancellationToken);
    }

    public async Task<Stream> OpenRangeAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        var target = Resolve(path);
        var expectedVersion = TryGetCachedMetadata(target.Path, out var cached) ? cached.Version : null;
        if (length == 0)
        {
            if (cached is not null && offset > cached.Length)
                throw new ContentSourceException($"Range starts beyond the end of '{path}'.");
            return new MemoryStream([], writable: false);
        }

        return await HttpContentStream.OpenAsync(this, target, offset, length, expectedVersion, cancellationToken);
    }

    public async Task<Stream> OpenSeekableReadAsync(string path, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(path, cancellationToken);
        return new HttpSeekableStream(this, Resolve(metadata.Path), metadata.Length, metadata.Version!);
    }

    public string? GetOpenStreamVersion(Stream stream)
        => stream switch
        {
            HttpContentStream http => http.Version,
            HttpSeekableStream seekable => seekable.Version,
            _ => null
        };

    public void Dispose() => client.Dispose();

    async Task<ContentMetadata> FetchMetadataAsync(HttpTarget target, CancellationToken cancellationToken)
    {
        using (var head = await SendAsync(HttpMethod.Head, target, configure: null, cancellationToken))
        {
            // Some origins reject HEAD or omit Content-Length on it; probe with a one-byte GET instead.
            if (head.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            {
                EnsureSuccess(head, target);
                if (head.Content.Headers.ContentLength is not null)
                    return Describe(target, ReadRepresentation(head, target));
            }
        }

        using var probe = await SendAsync(
            HttpMethod.Get,
            target,
            request => request.Headers.Range = new RangeHeaderValue(0, 0),
            cancellationToken);
        EnsureSuccess(probe, target);
        return Describe(target, ReadRepresentation(probe, target));
    }

    ContentMetadata Describe(HttpTarget target, HttpRepresentation representation)
        => new(
            Name,
            target.Path,
            target.DisplayName,
            representation.TotalLength,
            representation.Version,
            representation.LastModified,
            representation.AcceptsRanges);

    internal async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        HttpTarget target,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, target.Url);
        configure?.Invoke(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ContentSourceUnavailableException($"Timed out waiting for the origin to answer for '{target.Path}'.");
        }
        catch (HttpRequestException ex)
        {
            throw new ContentSourceUnavailableException($"Request for '{target.Path}' failed: {ex.Message}", ex);
        }
    }

    // Disposes the response before throwing.
    internal void EnsureSuccess(HttpResponseMessage response, HttpTarget target)
    {
        if (response.IsSuccessStatusCode)
            return;

        var status = response.StatusCode;
        response.Dispose();
        if (status == HttpStatusCode.PreconditionFailed)
            InvalidateMetadata(target.Path);

        throw status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => new ContentItemNotFoundException(Name, target.Path),
            HttpStatusCode.PreconditionFailed => new ContentSourceException($"Content '{target.Path}' changed on the origin."),
            HttpStatusCode.RequestedRangeNotSatisfiable => new ContentSourceException($"Range starts beyond the end of '{target.Path}'."),
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError
                => new ContentSourceUnavailableException($"Origin returned {(int)status} for '{target.Path}'."),
            >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest
                => new ContentSourceException($"Origin returned redirect {(int)status} for '{target.Path}'; redirects are not followed."),
            _ => new ContentSourceException($"Origin returned {(int)status} for '{target.Path}'.")
        };
    }

    internal HttpRepresentation ReadRepresentation(HttpResponseMessage response, HttpTarget target)
    {
        var content = response.Content.Headers;
        if (content.ContentEncoding.Any(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new ContentSourceException($"Origin returned encoded content for '{target.Path}'; it must serve the raw bytes.");

        var partial = response.StatusCode == HttpStatusCode.PartialContent;
        var totalLength = partial ? content.ContentRange?.Length : content.ContentLength;
        if (totalLength is null)
            throw new ContentSourceException($"Origin did not report the length of '{target.Path}'.");

        var etag = response.Headers.ETag;
        var lastModified = content.LastModified;
        string version;
        if (etag is { IsWeak: false } && etag.Tag.Length <= MaxEntityTagLength)
            version = FormatVersion(totalLength.Value, EntityTagVersionMarker, etag.Tag);
        else if (lastModified is not null)
            version = FormatVersion(totalLength.Value, LastModifiedVersionMarker, lastModified.Value.UtcTicks.ToString("x16", CultureInfo.InvariantCulture));
        else
            throw new ContentSourceException($"Origin sent neither a strong ETag nor Last-Modified for '{target.Path}', so changes cannot be detected.");

        var acceptsRanges = partial
            || response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
        return new HttpRepresentation(totalLength.Value, version, lastModified, acceptsRanges);
    }

    // Preconditions derived from a version produced by ReadRepresentation. Responses are checked
    // against the version too, because an origin may ignore preconditions.
    internal static void ApplyPreconditions(HttpRequestMessage request, string? version)
    {
        if (version is null)
            return;

        var separator = version.IndexOf('-');
        if (separator < 0 || separator + 2 > version.Length)
            return;

        var marker = version.Substring(separator, 2);
        var value = version[(separator + 2)..];
        if (marker == EntityTagVersionMarker && EntityTagHeaderValue.TryParse(value, out var etag))
            request.Headers.IfMatch.Add(etag);
        else if (marker == LastModifiedVersionMarker && long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ticks))
            request.Headers.IfUnmodifiedSince = new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    internal void InvalidateMetadata(string path) => metadataCache.TryRemove(path, out _);

    bool TryGetCachedMetadata(string path, out ContentMetadata metadata)
    {
        metadata = null!;
        if (!metadataCache.TryGetValue(path, out var entry))
            return false;

        if (Stopwatch.GetTimestamp() >= entry.ExpiresAtTimestamp)
        {
            metadataCache.TryRemove(KeyValuePair.Create(path, entry));
            return false;
        }

        metadata = entry.Metadata;
        return true;
    }

    void StoreMetadata(ContentMetadata metadata)
    {
        if (metadataCacheTtl <= TimeSpan.Zero)
            return;

        if (metadataCache.Count >= MaxMetadataCacheEntries)
            metadataCache.Clear();

        var expiresAt = Stopwatch.GetTimestamp() + (long)(metadataCacheTtl.TotalSeconds * Stopwatch.Frequency);
        metadataCache[metadata.Path] = new CachedMetadata(metadata, expiresAt);
    }

    internal HttpTarget Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ContentSourceException("Path is required.");

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/'))
            throw new ContentSourceException("Content source paths must be relative.");

        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            if (segment is "" or "." or "..")
                throw new ContentSourceException("Path segments must not be empty, '.' or '..'.");
            if (segment.Any(char.IsControl))
                throw new ContentSourceException("Path must not contain control characters.");
        }

        // Escaping every segment keeps ':', '?', '#' and '%' literal, so a segment can never turn
        // into a scheme, query or a different path on the origin.
        var url = new Uri(baseUri, string.Join('/', segments.Select(Uri.EscapeDataString)));
        if (!url.AbsoluteUri.StartsWith(baseUri.AbsoluteUri, StringComparison.Ordinal))
            throw new ContentSourceException("Path escapes the configured content source root.");

        return new HttpTarget(string.Join('/', segments), segments[^1], url);
    }

    static Uri ParseBaseUri(ContentSourceOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"HTTP content source '{options.Name}' must configure an absolute http(s) BaseUrl.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                $"HTTP content source '{options.Name}' BaseUrl must not contain a query, fragment or credentials; use Headers for authentication.");
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    static string FormatVersion(long length, string marker, string validator)
        => $"{length:x16}{marker}{validator}";

    sealed record CachedMetadata(ContentMetadata Metadata, long ExpiresAtTimestamp);
}

sealed record HttpTarget(string Path, string DisplayName, Uri Url);

sealed record HttpRepresentation(long TotalLength, string Version, DateTimeOffset? LastModified, bool AcceptsRanges);

// Forward-only body of a GET, optionally limited to a byte range. A dropped connection, stalled
// read or truncated body is resumed with a new range request pinned to the same version.
sealed class HttpContentStream : Stream
{
    readonly HttpContentSource source;
    readonly HttpTarget target;
    readonly long end;
    HttpResponseMessage? response;
    Stream? body;
    long position;
    long responseRemaining;
    bool acceptsRanges;
    int resumesLeft;

    HttpContentStream(HttpContentSource source, HttpTarget target, long offset, long end, string version)
    {
        this.source = source;
        this.target = target;
        this.end = end;
        position = offset;
        Version = version;
        resumesLeft = source.MaxResumeAttempts;
    }

    public string Version { get; }

    public static async Task<HttpContentStream> OpenAsync(
        HttpContentSource source,
        HttpTarget target,
        long offset,
        long? length,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var (response, representation) = await SendRangeAsync(source, target, offset, length, expectedVersion, cancellationToken);
        try
        {
            var end = length is null ? representation.TotalLength : Math.Min(offset + length.Value, representation.TotalLength);
            var stream = new HttpContentStream(source, target, offset, end, representation.Version);
            await stream.AttachAsync(response, representation, cancellationToken);
            return stream;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // Sends GET for [offset, offset + length) (or to the end) and validates the response.
    static async Task<(HttpResponseMessage Response, HttpRepresentation Representation)> SendRangeAsync(
        HttpContentSource source,
        HttpTarget target,
        long offset,
        long? length,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var wantsRange = offset > 0 || length is not null;
        var response = await source.SendAsync(
            HttpMethod.Get,
            target,
            request =>
            {
                if (wantsRange)
                    request.Headers.Range = new RangeHeaderValue(offset, length is null ? null : offset + length.Value - 1);
                HttpContentSource.ApplyPreconditions(request, expectedVersion);
            },
            cancellationToken);

        try
        {
            source.EnsureSuccess(response, target);
            var representation = source.ReadRepresentation(response, target);
            if (expectedVersion is not null && representation.Version != expectedVersion)
            {
                source.InvalidateMetadata(target.Path);
                throw new ContentSourceException($"Content '{target.Path}' changed on the origin.");
            }

            if (offset > representation.TotalLength)
                throw new ContentSourceException($"Range starts beyond the end of '{target.Path}'.");

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From != offset)
                    throw new ContentSourceException($"Origin answered a different range than requested for '{target.Path}'.");
            }
            else if (wantsRange && offset > 0)
            {
                // A 200 here means the origin ignored Range; reading up to the offset could mean
                // downloading most of a very large file per request.
                throw new ContentSourceException($"Origin does not support range requests for '{target.Path}'.");
            }

            return (response, representation);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    async Task AttachAsync(HttpResponseMessage newResponse, HttpRepresentation representation, CancellationToken cancellationToken)
    {
        response = newResponse;
        acceptsRanges = representation.AcceptsRanges;
        var contentRange = newResponse.Content.Headers.ContentRange;
        responseRemaining = newResponse.StatusCode == HttpStatusCode.PartialContent && contentRange?.To is { } to
            ? to + 1 - position
            : representation.TotalLength - position;
        body = await newResponse.Content.ReadAsStreamAsync(cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var remaining = end - position;
            if (remaining <= 0 || buffer.Length == 0)
                return 0;

            Exception failure;
            try
            {
                // The origin may legitimately serve a shorter range than requested; a response that
                // ended where its Content-Range said continues with the next range for free.
                if (body is null || responseRemaining <= 0)
                {
                    DetachResponse();
                    await ReopenAsync(cancellationToken);
                }

                var allowed = (int)Math.Min(buffer.Length, Math.Min(remaining, responseRemaining));
                var read = await ReadWithTimeoutAsync(body!, buffer[..allowed], cancellationToken);
                if (read > 0)
                {
                    position += read;
                    responseRemaining -= read;
                    resumesLeft = source.MaxResumeAttempts;
                    return read;
                }

                failure = new ContentSourceUnavailableException($"Origin closed the response for '{target.Path}' early.");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
            {
                failure = ex;
            }

            DetachResponse();
            if (!acceptsRanges || resumesLeft <= 0)
            {
                throw failure as IOException
                    ?? new ContentSourceUnavailableException($"Reading '{target.Path}' failed: {failure.Message}", failure);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * (source.MaxResumeAttempts - resumesLeft + 1)), cancellationToken);
            resumesLeft--;
        }
    }

    async Task ReopenAsync(CancellationToken cancellationToken)
    {
        var (newResponse, representation) = await SendRangeAsync(source, target, position, end - position, Version, cancellationToken);
        try
        {
            await AttachAsync(newResponse, representation, cancellationToken);
        }
        catch
        {
            newResponse.Dispose();
            DetachResponse();
            throw;
        }
    }

    async ValueTask<int> ReadWithTimeoutAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(source.RequestTimeout);
        try
        {
            return await stream.ReadAsync(buffer, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ContentSourceUnavailableException($"Timed out reading '{target.Path}' from the origin.");
        }
    }

    void DetachResponse()
    {
        body?.Dispose();
        response?.Dispose();
        body = null;
        response = null;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DetachResponse();
        base.Dispose(disposing);
    }
}

// Random-access view used by peer-wire, which reads 16 KiB blocks. Each miss fetches a
// ReadAheadBytes window with one range request, so a piece costs a few requests instead of one per block.
sealed class HttpSeekableStream(HttpContentSource source, HttpTarget target, long length, string version) : Stream
{
    byte[]? window;
    long windowStart;
    int windowLength;
    long position;

    public string Version { get; } = version;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (position >= length || buffer.Length == 0)
            return 0;

        if (window is null || position < windowStart || position >= windowStart + windowLength)
            await FillWindowAsync(cancellationToken);

        var offsetInWindow = (int)(position - windowStart);
        var count = Math.Min(buffer.Length, windowLength - offsetInWindow);
        window.AsSpan(offsetInWindow, count).CopyTo(buffer.Span);
        position += count;
        return count;
    }

    async Task FillWindowAsync(CancellationToken cancellationToken)
    {
        window ??= ArrayPool<byte>.Shared.Rent(source.ReadAheadBytes);
        windowLength = 0;
        windowStart = position;

        var wanted = (int)Math.Min(source.ReadAheadBytes, length - position);
        await using var content = await HttpContentStream.OpenAsync(source, target, position, wanted, Version, cancellationToken);
        while (windowLength < wanted)
        {
            var read = await content.ReadAsync(window.AsMemory(windowLength, wanted - windowLength), cancellationToken);
            if (read == 0)
                throw new ContentSourceUnavailableException($"Origin returned fewer bytes than requested for '{target.Path}'.");
            windowLength += read;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        ArgumentOutOfRangeException.ThrowIfNegative(next, nameof(offset));
        position = next;
        return position;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && window is not null)
        {
            ArrayPool<byte>.Shared.Return(window);
            window = null;
        }

        base.Dispose(disposing);
    }
}
