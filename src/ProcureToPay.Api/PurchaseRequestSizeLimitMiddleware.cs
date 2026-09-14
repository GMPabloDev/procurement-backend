using Microsoft.AspNetCore.Http.Features;
using ProcureToPay.Domain.Modules.PurchaseRequests;

namespace ProcureToPay.Api;

/// <summary>
/// Enforces the 5 MiB purchase request snapshot budget independently of the Content-Length header:
/// the Kestrel feature limit applies while the body is read, and a limiting stream keeps the same
/// guarantee when the host lacks that feature (in-process test server, chunked uploads), so the
/// <c>413</c> contract of SPEC 06 REQ-11 cannot be bypassed.
/// </summary>
public sealed class PurchaseRequestSizeLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/v1/purchase-requests"))
        {
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
            {
                feature.MaxRequestBodySize = PurchaseRequestLimits.MaxSnapshotBytes;
            }
            else
            {
                context.Request.Body = new LimitedReadStream(
                    context.Request.Body, PurchaseRequestLimits.MaxSnapshotBytes);
            }
        }

        await next(context);
    }

    private sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int bytes)
        {
            read += bytes;
            if (read > maximumBytes)
            {
                throw new BadHttpRequestException(
                    "The purchase request snapshot exceeds its size budget.",
                    StatusCodes.Status413PayloadTooLarge);
            }

            return bytes;
        }
    }
}
