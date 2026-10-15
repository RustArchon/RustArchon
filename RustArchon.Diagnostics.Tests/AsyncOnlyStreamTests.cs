// Copyright ©2026 Scott Blomfield

using Microsoft.Extensions.Logging;

namespace RustArchon.Diagnostics.Tests;

/// <summary>
/// Kestrel's response stream refuses synchronous writes (AllowSynchronousIO is off), and the endpoint writes the log result straight to it. A MemoryStream
/// never refuses, which is how the first version of the writer passed every test and failed on the first real request.
/// </summary>
public class AsyncOnlyStreamTests
{
    private sealed class AsyncOnlyStream : Stream
    {
        public MemoryStream Inner { get; } = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Inner.WriteAsync(buffer, cancellationToken);
    }

    private static async Task<LogResult> SomeResult(bool summary = false)
    {
        var ring = new LogRing("api", "boot1", 100);
        ring.Append(DateTimeOffset.UnixEpoch, LogLevel.Information, "Cat", "hello", null, null);
        return await new LogQueryEngine().ExecuteAsync(new LogQuery { Summary = summary }, [new RingLogSource("api", ring)], CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_writer_never_writes_synchronously_to_the_output(bool summary)
    {
        var output = new AsyncOnlyStream();

        await LogNdjsonWriter.WriteAsync(await SomeResult(summary), output, CancellationToken.None);

        Assert.Contains("_meta", System.Text.Encoding.UTF8.GetString(output.Inner.ToArray()));
    }
}
