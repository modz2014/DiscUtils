//
// Copyright (c) 2008-2011, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Streams;
using DiscUtils.Streams.Compatibility;

namespace DiscUtils.Vmdk;

/// <summary>
/// Represents and extent from a sparse disk from 'hosted' software (VMware Workstation, etc).
/// </summary>
/// <remarks>Hosted disks and server disks (ESX, etc) are subtly different formats.</remarks>
internal sealed class HostedSparseExtentStream : CommonSparseExtentStream
{
    private readonly HostedSparseExtentHeader _hostedHeader;

    public HostedSparseExtentStream(Stream file, Ownership ownsFile, long diskOffset, SparseStream parentDiskStream,
                                    Ownership ownsParentDiskStream)
    {
        _fileStream = file;
        _ownsFileStream = ownsFile;
        _diskOffset = diskOffset;
        _parentDiskStream = parentDiskStream;
        _ownsParentDiskStream = ownsParentDiskStream;

        file.Position = 0;
        Span<byte> headerSector = stackalloc byte[Sizes.Sector];
        file.ReadExactly(headerSector);
        _hostedHeader = HostedSparseExtentHeader.Read(headerSector);
        if (_hostedHeader.GdOffset == -1)
        {
            // Fall back to secondary copy that (should) be at the end of the stream, just before the end-of-stream sector marker
            file.Position = file.Length - Sizes.OneKiB;
            file.ReadExactly(headerSector);
            _hostedHeader = HostedSparseExtentHeader.Read(headerSector);

            if (_hostedHeader.MagicNumber != HostedSparseExtentHeader.VmdkMagicNumber)
            {
                throw new IOException("Unable to locate valid VMDK header or footer");
            }
        }

        _header = _hostedHeader;

        if (_hostedHeader.CompressAlgorithm is not 0 and not 1)
        {
            throw new NotSupportedException("Only uncompressed and DEFLATE compressed disks supported");
        }

        _gtCoverage = _header.NumGTEsPerGT * _header.GrainSize * Sizes.Sector;

        LoadGlobalDirectory();
    }

    public override bool CanWrite =>
            // No write support for streamOptimized disks
            _fileStream.CanWrite &&
                   (_hostedHeader.Flags &
                    (HostedSparseExtentFlags.CompressedGrains | HostedSparseExtentFlags.MarkersInUse)) == 0;

    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckDisposed();

        if (!CanWrite)
        {
            throw new InvalidOperationException("Cannot write to this stream");
        }

        if (_position + count > Length)
        {
            throw new IOException("Attempt to write beyond end of stream");
        }

        var totalWritten = 0;
        while (totalWritten < count)
        {
            var grainTable = (int)(_position / _gtCoverage);
            var grainTableOffset = (int)(_position - grainTable * _gtCoverage);

            LoadGrainTable(grainTable);

            var grainSize = (int)(_header.GrainSize * Sizes.Sector);
            var grain = grainTableOffset / grainSize;
            var grainOffset = grainTableOffset - grain * grainSize;

            if (GetGrainTableEntry(grain) == 0)
            {
                AllocateGrain(grainTable, grain);
            }

            var numToWrite = Math.Min(count - totalWritten, grainSize - grainOffset);
            _fileStream.Position = (long)GetGrainTableEntry(grain) * Sizes.Sector + grainOffset;
            _fileStream.Write(buffer, offset + totalWritten, numToWrite);

            _position += numToWrite;
            totalWritten += numToWrite;
        }

        _atEof = _position == Length;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckDisposed();

        if (!CanWrite)
        {
            throw new InvalidOperationException("Cannot write to this stream");
        }

        var totalWritten = 0;
        while (totalWritten < buffer.Length)
        {
            var grainTable = (int)(_position / _gtCoverage);
            var grainTableOffset = (int)(_position - grainTable * _gtCoverage);

            LoadGrainTable(grainTable);

            var grainSize = (int)(_header.GrainSize * Sizes.Sector);
            var grain = grainTableOffset / grainSize;
            var grainOffset = grainTableOffset - grain * grainSize;

            if (GetGrainTableEntry(grain) == 0)
            {
                AllocateGrain(grainTable, grain);
            }

            var numToWrite = Math.Min(buffer.Length - totalWritten, grainSize - grainOffset);
            _fileStream.Position = (long)GetGrainTableEntry(grain) * Sizes.Sector + grainOffset;
            _fileStream.Write(buffer.Slice(totalWritten, numToWrite));

            _position += numToWrite;
            totalWritten += numToWrite;
        }

        _atEof = _position == Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        CheckDisposed();

        if (!CanWrite)
        {
            throw new InvalidOperationException("Cannot write to this stream");
        }

        var totalWritten = 0;
        while (totalWritten < buffer.Length)
        {
            var grainTable = (int)(_position / _gtCoverage);
            var grainTableOffset = (int)(_position - grainTable * _gtCoverage);

            await LoadGrainTableAsync(grainTable, cancellationToken).ConfigureAwait(false);

            var grainSize = (int)(_header.GrainSize * Sizes.Sector);
            var grain = grainTableOffset / grainSize;
            var grainOffset = grainTableOffset - grain * grainSize;

            if (GetGrainTableEntry(grain) == 0)
            {
                await AllocateGrainAsync(grainTable, grain, cancellationToken).ConfigureAwait(false);
            }

            var numToWrite = Math.Min(buffer.Length - totalWritten, grainSize - grainOffset);
            _fileStream.Position = (long)GetGrainTableEntry(grain) * Sizes.Sector + grainOffset;
            await _fileStream.WriteAsync(buffer.Slice(totalWritten, numToWrite), cancellationToken).ConfigureAwait(false);

            _position += numToWrite;
            totalWritten += numToWrite;
        }

        _atEof = _position == Length;
    }

    protected override int ReadGrain(byte[] buffer, int bufferOffset, long grainStart, int grainOffset,
                                     int numToRead)
    {
        if ((_hostedHeader.Flags & HostedSparseExtentFlags.CompressedGrains) != 0)
        {
            _fileStream.Position = grainStart;

            var hdr = _fileStream.ReadStruct<CompressedGrainHeader>();

            var readBuffer = _fileStream.ReadExactly(hdr.DataSize);

            // This is really a zlib stream, so has header and footer.  We ignore this right now, but we sanity
            // check against expected header values...
            var header = EndianUtilities.ToUInt16BigEndian(readBuffer, 0);

            if (header % 31 != 0)
            {
                throw new IOException("Invalid ZLib header found");
            }

            if ((header & 0x0F00) != 8 << 8)
            {
                throw new NotSupportedException("ZLib compression not using DEFLATE algorithm");
            }

            if ((header & 0x0020) != 0)
            {
                throw new NotSupportedException("ZLib compression using preset dictionary");
            }

            var readStream = new MemoryStream(readBuffer, 2, hdr.DataSize - 2, false);
            var deflateStream = new DeflateStream(readStream, CompressionMode.Decompress);

            // Need to skip some bytes, but DefaultStream doesn't support seeking...
            deflateStream.ReadExactly(grainOffset);

            return deflateStream.Read(buffer, bufferOffset, numToRead);
        }

        return base.ReadGrain(buffer, bufferOffset, grainStart, grainOffset, numToRead);
    }

    protected override async ValueTask<int> ReadGrainAsync(Memory<byte> buffer, long grainStart, int grainOffset, CancellationToken cancellationToken)
    {
        if ((_hostedHeader.Flags & HostedSparseExtentFlags.CompressedGrains) != 0)
        {
            _fileStream.Position = grainStart;

            var hdr = await _fileStream.ReadStructAsync<CompressedGrainHeader>(cancellationToken).ConfigureAwait(false);

            var readBuffer = await _fileStream.ReadExactlyAsync(hdr.DataSize, cancellationToken).ConfigureAwait(false);

            // This is really a zlib stream, so has header and footer.  We ignore this right now, but we sanity
            // check against expected header values...
            var header = EndianUtilities.ToUInt16BigEndian(readBuffer, 0);

            if (header % 31 != 0)
            {
                throw new IOException("Invalid ZLib header found");
            }

            if ((header & 0x0F00) != 8 << 8)
            {
                throw new NotSupportedException("ZLib compression not using DEFLATE algorithm");
            }

            if ((header & 0x0020) != 0)
            {
                throw new NotSupportedException("ZLib compression using preset dictionary");
            }

            Stream readStream = new MemoryStream(readBuffer, 2, hdr.DataSize - 2, false);
            var deflateStream = new DeflateStream(readStream, CompressionMode.Decompress);

            // Need to skip some bytes, but DefaultStream doesn't support seeking...
            await deflateStream.ReadExactlyAsync(grainOffset, cancellationToken).ConfigureAwait(false);

            return await deflateStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        return await base.ReadGrainAsync(buffer, grainStart, grainOffset, cancellationToken).ConfigureAwait(false);
    }

    protected override int ReadGrain(Span<byte> buffer, long grainStart, int grainOffset)
    {
        if ((_hostedHeader.Flags & HostedSparseExtentFlags.CompressedGrains) != 0)
        {
            _fileStream.Position = grainStart;

            var hdr = _fileStream.ReadStruct<CompressedGrainHeader>();

            var readBuffer = _fileStream.ReadExactly(hdr.DataSize);

            // This is really a zlib stream, so has header and footer.  We ignore this right now, but we sanity
            // check against expected header values...
            var header = EndianUtilities.ToUInt16BigEndian(readBuffer, 0);

            if (header % 31 != 0)
            {
                throw new IOException("Invalid ZLib header found");
            }

            if ((header & 0x0F00) != 8 << 8)
            {
                throw new NotSupportedException("ZLib compression not using DEFLATE algorithm");
            }

            if ((header & 0x0020) != 0)
            {
                throw new NotSupportedException("ZLib compression using preset dictionary");
            }

            Stream readStream = new MemoryStream(readBuffer, 2, hdr.DataSize - 2, false);
            var deflateStream = new DeflateStream(readStream, CompressionMode.Decompress);

            // Need to skip some bytes, but DefaultStream doesn't support seeking...
            deflateStream.ReadExactly(grainOffset);

            return deflateStream.Read(buffer);
        }

        return base.ReadGrain(buffer, grainStart, grainOffset);
    }

    protected override StreamExtent MapGrain(long grainStart, int grainOffset, int numToRead)
    {
        if ((_hostedHeader.Flags & HostedSparseExtentFlags.CompressedGrains) != 0)
        {
            _fileStream.Position = grainStart;

            var hdr = _fileStream.ReadStruct<CompressedGrainHeader>();

            return new StreamExtent(grainStart + grainOffset, CompressedGrainHeader.Size + hdr.DataSize);
        }

        return base.MapGrain(grainStart, grainOffset, numToRead);
    }

    protected override void LoadGlobalDirectory()
    {
        base.LoadGlobalDirectory();

        if ((_hostedHeader.Flags & HostedSparseExtentFlags.RedundantGrainTable) != 0)
        {
            var numGTs = (int)MathUtilities.Ceil(_header.Capacity * Sizes.Sector, _gtCoverage);
            _redundantGlobalDirectory = new uint[numGTs];
            _fileStream.Position = _hostedHeader.RgdOffset * Sizes.Sector;
            var gdAsBytes = _fileStream.ReadExactly(numGTs * 4);
            for (var i = 0; i < _globalDirectory.Length; ++i)
            {
                _redundantGlobalDirectory[i] = EndianUtilities.ToUInt32LittleEndian(gdAsBytes, i * 4);
            }
        }
    }

    private void AllocateGrain(int grainTable, int grain)
    {
        // Calculate start pos for new grain
        var grainStartPos = MathUtilities.RoundUp(_fileStream.Length, _header.GrainSize * Sizes.Sector);

        // Copy-on-write semantics, read the bytes from parent and write them out to this extent.
        _parentDiskStream.Position = _diskOffset +
                                     (grain + _header.NumGTEsPerGT * grainTable) * _header.GrainSize *
                                     Sizes.Sector;

        var size = (int)Math.Min(_header.GrainSize * Sizes.Sector, _parentDiskStream.Length - _parentDiskStream.Position);

        var content = _parentDiskStream.ReadExactly(size);
        
        _fileStream.Position = grainStartPos;
        
        _fileStream.Write(content, 0, content.Length);

        LoadGrainTable(grainTable);
        SetGrainTableEntry(grain, (uint)(grainStartPos / Sizes.Sector));
        WriteGrainTable();
    }

    private async ValueTask AllocateGrainAsync(int grainTable, int grain, CancellationToken cancellationToken)
    {
        // Calculate start pos for new grain
        var grainStartPos = MathUtilities.RoundUp(_fileStream.Length, _header.GrainSize * Sizes.Sector);

        // Copy-on-write semantics, read the bytes from parent and write them out to this extent.
        _parentDiskStream.Position = _diskOffset +
                                     (grain + _header.NumGTEsPerGT * grainTable) * _header.GrainSize *
                                     Sizes.Sector;

        var size = (int)Math.Min(_header.GrainSize * Sizes.Sector, _parentDiskStream.Length - _parentDiskStream.Position);

        var content = await _parentDiskStream.ReadExactlyAsync(size, cancellationToken).ConfigureAwait(false);

        _fileStream.Position = grainStartPos;

        await _fileStream.WriteAsync(content, cancellationToken).ConfigureAwait(false);

        await LoadGrainTableAsync(grainTable, cancellationToken).ConfigureAwait(false);
        SetGrainTableEntry(grain, (uint)(grainStartPos / Sizes.Sector));
        await WriteGrainTableAsync(cancellationToken).ConfigureAwait(false);
    }

    private void WriteGrainTable()
    {
        if (_grainTable == null)
        {
            throw new InvalidOperationException("No grain table loaded");
        }

        _fileStream.Position = _globalDirectory[_currentGrainTable] * (long)Sizes.Sector;
        _fileStream.Write(_grainTable, 0, _grainTable.Length);

        if (_redundantGlobalDirectory != null)
        {
            _fileStream.Position = _redundantGlobalDirectory[_currentGrainTable] * (long)Sizes.Sector;
            _fileStream.Write(_grainTable, 0, _grainTable.Length);
        }
    }

    private async ValueTask WriteGrainTableAsync(CancellationToken cancellationToken)
    {
        if (_grainTable == null)
        {
            throw new InvalidOperationException("No grain table loaded");
        }

        _fileStream.Position = _globalDirectory[_currentGrainTable] * (long)Sizes.Sector;
        await _fileStream.WriteAsync(_grainTable, cancellationToken).ConfigureAwait(false);

        if (_redundantGlobalDirectory != null)
        {
            _fileStream.Position = _redundantGlobalDirectory[_currentGrainTable] * (long)Sizes.Sector;
            await _fileStream.WriteAsync(_grainTable, cancellationToken).ConfigureAwait(false);
        }
    }
}