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

namespace DiscUtils.Streams;

/// <summary>
/// Base class for reading binary data from a stream.
/// </summary>
public abstract class DataReader
{
    private const int _bufferSize = sizeof(UInt64);

    protected readonly Stream _stream;

    protected byte[] _buffer;

    public DataReader(Stream stream)
    {
        _stream = stream;
    }

    public long Length => _stream.Length;

    public long Position => _stream.Position;

    public void Skip(int bytes)
    {
        ReadBytes(bytes);
    }

    public abstract ushort ReadUInt16();

    public abstract int ReadInt32();

    public abstract uint ReadUInt32();

    public abstract long ReadInt64();

    public abstract ulong ReadUInt64();

    public virtual byte[] ReadBytes(int count)
    {
        return _stream.ReadExactly(count);
    }

    protected void ReadToBuffer(int count)
    {
        _buffer ??= new byte[_bufferSize];

        _stream.ReadExactly(_buffer, 0, count);
    }
}