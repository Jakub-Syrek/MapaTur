using System.Net.Http.Headers;

using MapaTur.Application.Packaging;

namespace MapaTur.Infrastructure.Packaging;

/// <summary>
/// <see cref="IPackageFileFetcher"/> over <see cref="HttpClient"/>. <see cref="GetContentLengthAsync"/> uses a
/// HEAD request; <see cref="OpenRangeAsync"/> issues a ranged GET (<c>Range: bytes={from}-</c>) and streams the
/// body with <see cref="HttpCompletionOption.ResponseHeadersRead"/> so a multi-GB package never buffers in
/// memory. The returned stream owns the HTTP request/response and releases them when disposed.
/// </summary>
public sealed class HttpPackageFileFetcher : IPackageFileFetcher
{
    private readonly HttpClient client;

    /// <summary>Initializes the fetcher.</summary>
    /// <param name="client">HTTP client used for all requests (its lifetime is owned by the caller / DI).</param>
    public HttpPackageFileFetcher(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    /// <inheritdoc />
    public async Task<long> GetContentLengthAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using HttpResponseMessage response = await this.client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? length = response.Content.Headers.ContentLength;
        return length ?? throw new IOException($"Server did not report a Content-Length for '{url}'.");
    }

    /// <inheritdoc />
    public async Task<Stream> OpenRangeAsync(string url, long fromByte, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (fromByte < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromByte), fromByte, "Range start must be non-negative.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(fromByte, null);

        HttpResponseMessage? response = null;
        try
        {
            response = await this.client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var owning = new HttpOwningStream(content, response, request);
            response = null; // ownership transferred to the returned stream
            return owning;
        }
        catch
        {
            response?.Dispose();
            request.Dispose();
            throw;
        }
    }

    /// <summary>A read stream that also disposes the HTTP request/response it was read from.</summary>
    private sealed class HttpOwningStream : Stream
    {
        private readonly Stream inner;
        private readonly HttpResponseMessage response;
        private readonly HttpRequestMessage request;

        public HttpOwningStream(Stream inner, HttpResponseMessage response, HttpRequestMessage request)
        {
            this.inner = inner;
            this.response = response;
            this.request = request;
        }

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
                request.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            request.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}