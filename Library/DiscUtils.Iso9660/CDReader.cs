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

using System.Buffers;
using System.Collections.Generic;
using System.IO;
using DiscUtils.Streams;
using DiscUtils.Vfs;

namespace DiscUtils.Iso9660;

/// <summary>
/// Class for reading existing ISO images.
/// </summary>
public class CDReader : VfsFileSystemFacade, IClusterBasedFileSystem,
    IUnixFileSystem, IFileSystemWithClusterMap
{
    /// <summary>
    /// Initializes a new instance of the CDReader class.
    /// </summary>
    /// <param name="data">The stream to read the ISO image from.</param>
    /// <param name="joliet">Whether to read Joliet extensions.</param>
    public CDReader(Stream data, bool joliet)
        : base(new VfsCDReader(data, joliet, false)) {}

    /// <summary>
    /// Initializes a new instance of the CDReader class.
    /// </summary>
    /// <param name="data">The stream to read the ISO image from.</param>
    /// <param name="joliet">Whether to read Joliet extensions.</param>
    /// <param name="hideVersions">Hides version numbers (e.g. ";1") from the end of files.</param>
    public CDReader(Stream data, bool joliet, bool hideVersions)
        : base(new VfsCDReader(data, joliet, hideVersions)) {}

    /// <summary>
    /// Gets which of the Iso9660 variants is being used.
    /// </summary>
    public Iso9660Variant ActiveVariant => GetRealFileSystem<VfsCDReader>().ActiveVariant;

    /// <summary>
    /// Gets the emulation requested of BIOS when the image is loaded.
    /// </summary>
    public BootDeviceEmulation BootEmulation => GetRealFileSystem<VfsCDReader>().BootEmulation;

    /// <summary>
    /// Gets the absolute start position (in bytes) of the boot image, or zero if not found.
    /// </summary>
    public long BootImageStart => GetRealFileSystem<VfsCDReader>().BootImageStart;

    /// <summary>
    /// Gets the memory segment the image should be loaded into (0 for default).
    /// </summary>
    public int BootLoadSegment => GetRealFileSystem<VfsCDReader>().BootLoadSegment;

    /// <summary>
    /// Gets a value indicating whether a boot image is present.
    /// </summary>
    public bool HasBootImage => GetRealFileSystem<VfsCDReader>().HasBootImage;

    /// <summary>
    /// Gets the size (in bytes) of each cluster.
    /// </summary>
    public long ClusterSize => GetRealFileSystem<VfsCDReader>().ClusterSize;

    /// <summary>
    /// Gets the total number of clusters managed by the file system.
    /// </summary>
    public long TotalClusters => GetRealFileSystem<VfsCDReader>().TotalClusters;

    /// <summary>
    /// Gets the size (in bytes) of each sector.
    /// </summary>
    public int SectorSize => GetRealFileSystem<VfsCDReader>().SectorSize;

    /// <summary>
    /// Gets the total number of sectors managed by the file system.
    /// </summary>
    public long TotalSectors => GetRealFileSystem<VfsCDReader>().TotalSectors;

    /// <summary>
    /// Converts a cluster (index) into an absolute byte position in the underlying stream.
    /// </summary>
    /// <param name="cluster">The cluster to convert.</param>
    /// <returns>The corresponding absolute byte position.</returns>
    public long ClusterToOffset(long cluster)
    {
        return GetRealFileSystem<VfsCDReader>().ClusterToOffset(cluster);
    }

    /// <summary>
    /// Converts an absolute byte position in the underlying stream to a cluster (index).
    /// </summary>
    /// <param name="offset">The byte position to convert.</param>
    /// <returns>The cluster containing the specified byte.</returns>
    public long OffsetToCluster(long offset)
    {
        return GetRealFileSystem<VfsCDReader>().OffsetToCluster(offset);
    }

    /// <summary>
    /// Converts a file name to the list of clusters occupied by the file's data.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The clusters.</returns>
    /// <remarks>Note that in some file systems, small files may not have dedicated
    /// clusters.  Only dedicated clusters will be returned.</remarks>
    public IEnumerable<Range<long, long>> PathToClusters(string path)
    {
        return GetRealFileSystem<VfsCDReader>().PathToClusters(path);
    }

    /// <summary>
    /// Converts a file name to the extents containing its data.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>The file extents, as absolute byte positions in the underlying stream.</returns>
    /// <remarks>Use this method with caution - not all file systems will store all bytes
    /// directly in extents.  Files may be compressed, sparse or encrypted.  This method
    /// merely indicates where file data is stored, not what's stored.</remarks>
    public IEnumerable<StreamExtent> PathToExtents(string path)
    {
        return GetRealFileSystem<VfsCDReader>().PathToExtents(path);
    }

    /// <summary>
    /// Gets number of allocated clusters for a file.
    /// </summary>
    /// <param name="path">The path to inspect.</param>
    /// <returns>Number of clusters allocated</returns>
    public long GetAllocatedClustersCount(string path)
        => GetRealFileSystem<VfsCDReader>().GetAllocatedClustersCount(path);

    /// <summary>
    /// Gets an object that can convert between clusters and files.
    /// </summary>
    /// <returns>The cluster map.</returns>
    public ClusterMap BuildClusterMap()
    {
        return GetRealFileSystem<VfsCDReader>().BuildClusterMap();
    }

    /// <summary>
    /// Retrieves Unix-specific information about a file or directory.
    /// </summary>
    /// <param name="path">Path to the file or directory.</param>
    /// <returns>Information about the owner, group, permissions and type of the
    /// file or directory.</returns>
    public UnixFileSystemInfo GetUnixFileInfo(string path)
    {
        return GetRealFileSystem<VfsCDReader>().GetUnixFileInfo(path);
    }

    /// <summary>
    /// Detects if a stream contains a valid ISO file system.
    /// </summary>
    /// <param name="data">The stream to inspect.</param>
    /// <returns><c>true</c> if the stream contains an ISO file system, else false.</returns>
    public static bool Detect(Stream data)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(IsoUtilities.SectorSize);
        try
        {
            if (data.Length < 0x8000 + IsoUtilities.SectorSize)
            {
                return false;
            }

            data.Position = 0x8000;
            var numRead = data.ReadMaximum(buffer, 0, IsoUtilities.SectorSize);
            if (numRead != IsoUtilities.SectorSize)
            {
                return false;
            }

            var bvd = new BaseVolumeDescriptor(buffer);

            return bvd.StandardIdentifier == BaseVolumeDescriptor.Iso9660StandardIdentifier;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Opens a stream containing the boot image.
    /// </summary>
    /// <returns>The boot image as a stream.</returns>
    public Stream OpenBootImage()
    {
        return GetRealFileSystem<VfsCDReader>().OpenBootImage();
    }
}