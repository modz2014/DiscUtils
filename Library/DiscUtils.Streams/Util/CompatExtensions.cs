﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LTRData.Extensions.Async;

namespace DiscUtils.Streams.Compatibility;

public abstract class CompatibilityStream : Stream
{
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

#if NETSTANDARD2_1_OR_GREATER || NETCOREAPP
    public abstract override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    public abstract override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
    public abstract override int Read(Span<byte> buffer);
    public abstract override void Write(ReadOnlySpan<byte> buffer);
#else
    public abstract ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
    public abstract int Read(Span<byte> buffer);
    public abstract void Write(ReadOnlySpan<byte> buffer);
#endif

    public override int ReadByte()
    {
        Span<byte> b = stackalloc byte[1];
        if (Read(b) != 1)
        {
            return -1;
        }

        return b[0];
    }

    public override void WriteByte(byte value) =>
        Write(stackalloc byte[] { value });

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).AsAsyncResult(callback, state);

    public override int EndRead(IAsyncResult asyncResult) => ((Task<int>)asyncResult).GetAwaiter().GetResult();

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).AsAsyncResult(callback, state);

    public override void EndWrite(IAsyncResult asyncResult) => ((Task)asyncResult).GetAwaiter().GetResult();

}

public abstract class ReadOnlyCompatibilityStream : CompatibilityStream
{
    public sealed override bool CanWrite => false;
    public sealed override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Attempt to write to read-only stream");
    public sealed override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Attempt to write to read-only stream");
    public sealed override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new InvalidOperationException("Attempt to write to read-only stream");
    public sealed override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Attempt to write to read-only stream");
    public sealed override void WriteByte(byte value) => throw new InvalidOperationException("Attempt to write to read-only stream");
    public sealed override void Flush() { }
    public sealed override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public sealed override void SetLength(long value) => throw new InvalidOperationException("Attempt to change length of read-only stream");
}

public static class CompatExtensions
{
    public static int ReadFrom<T>(this T serializable, byte[] bytes, int offset) where T : class, IByteArraySerializable =>
        serializable.ReadFrom(bytes.AsSpan(offset));

    public static void WriteTo<T>(this T serializable, byte[] bytes, int offset) where T : class, IByteArraySerializable =>
        serializable.WriteTo(bytes.AsSpan(offset));

    public static int ReadFrom<T>(ref this T serializable, byte[] bytes, int offset) where T : struct, IByteArraySerializable =>
        serializable.ReadFrom(bytes.AsSpan(offset));

    public static void WriteTo<T>(ref this T serializable, byte[] bytes, int offset) where T : struct, IByteArraySerializable =>
        serializable.WriteTo(bytes.AsSpan(offset));

#if !NETSTANDARD2_1_OR_GREATER && !NETCOREAPP

    public static void NextBytes(this Random random, Span<byte> buffer)
    {
        var bytes = new byte[buffer.Length];
        random.NextBytes(bytes);
        bytes.AsSpan().CopyTo(buffer);
    }

    public static int Read(this Stream stream, Span<byte> buffer)
    {
        if (stream is CompatibilityStream compatibilityStream)
        {
            return compatibilityStream.Read(buffer);
        }

        return ReadUsingArray(stream, buffer);
    }

    public static int ReadUsingArray(Stream stream, Span<byte> buffer)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var numRead = stream.Read(bytes, 0, buffer.Length);
            bytes.AsSpan(0, numRead).CopyTo(buffer);
            return numRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public static ValueTask<int> ReadAsync(this Stream stream, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (stream is CompatibilityStream compatibilityStream)
        {
            return compatibilityStream.ReadAsync(buffer, cancellationToken);
        }

        return ReadUsingArrayAsync(stream, buffer, cancellationToken);
    }

    public static ValueTask<int> ReadUsingArrayAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (MemoryMarshal.TryGetArray<byte>(buffer, out var arraySegment))
        {
            return new(stream.ReadAsync(arraySegment.Array, arraySegment.Offset, arraySegment.Count, cancellationToken));
        }

        return ReadUsingTemporaryArrayAsync(stream, buffer, cancellationToken);
    }

    private static async ValueTask<int> ReadUsingTemporaryArrayAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var numRead = await stream.ReadAsync(bytes, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            bytes.AsSpan(0, numRead).CopyTo(buffer.Span);
            return numRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public static void Write(this Stream stream, ReadOnlySpan<byte> buffer)
    {
        if (stream is CompatibilityStream compatibilityStream)
        {
            compatibilityStream.Write(buffer);
            return;
        }

        var bytes = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(bytes);
            stream.Write(bytes, 0, buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public static ValueTask WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (stream is CompatibilityStream compatibilityStream)
        {
            return compatibilityStream.WriteAsync(buffer, cancellationToken);
        }

        if (MemoryMarshal.TryGetArray(buffer, out var arraySegment))
        {
            return new(stream.WriteAsync(arraySegment.Array, arraySegment.Offset, arraySegment.Count, cancellationToken));
        }

        return WriteUsingTemporaryArrayAsync(stream, buffer, cancellationToken);
    }

    private static async ValueTask WriteUsingTemporaryArrayAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(bytes);
            await stream.WriteAsync(bytes, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public static void AppendData(this IncrementalHash hash, ReadOnlySpan<byte> data)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            data.CopyTo(bytes);
            hash.AppendData(bytes, 0, data.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
#endif
}

