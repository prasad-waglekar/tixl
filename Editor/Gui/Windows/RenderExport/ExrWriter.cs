#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace T3.Editor.Gui.Windows.RenderExport;

/// <summary>
/// A minimal, dependency-free OpenEXR writer: single-part, scanline-based, half-float RGBA.
/// WIC has no EXR encoder, and the format is simple enough that a native dependency isn't worth it.
///
/// Pixel values are written as-is (linear, not clamped), so values above 1.0 survive.
/// Only lossless modes are supported, so the file round-trips exactly.
/// </summary>
internal static class ExrWriter
{
    internal enum Compression : byte
    {
        /// <summary>Fastest to write, largest files (1 scanline per block).</summary>
        None = 0,

        /// <summary>Lossless zlib with a predictor (16 scanlines per block). Supported by every EXR reader.</summary>
        Zip = 3,
    }

    /// <summary>Bytes per pixel in the source rows: four 16-bit half floats, interleaved as R, G, B, A.</summary>
    internal const int SourceBytesPerPixel = 8;

    private const int HalfPixelType = 1; // OpenEXR: UINT = 0, HALF = 1, FLOAT = 2
    private const int ZipLinesPerBlock = 16;

    // EXR requires channels sorted by name. Values are the byte offset of that channel within a source pixel (R,G,B,A).
    private static readonly (string Name, int SourceOffset)[] _channels = [("A", 6), ("B", 4), ("G", 2), ("R", 0)];

    /// <summary>
    /// Writes an RGBA half-float image.
    /// </summary>
    /// <param name="readRow">
    /// Fills <c>dest</c> with <c>width * 8</c> bytes (RGBA16F, little endian) for the scanline <c>y</c>, where y=0 is the top row.
    /// May be called concurrently from several threads, each with its own <c>dest</c> buffer.
    /// </param>
    internal static void WriteRgbaHalf(string filepath, int width, int height, Action<int, byte[]> readRow, Compression compression = Compression.Zip)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"Invalid image size {width}×{height}");

        var linesPerBlock = compression == Compression.Zip ? ZipLinesPerBlock : 1;
        var blockCount = (height + linesPerBlock - 1) / linesPerBlock;

        // Encode all blocks first (in parallel), so the offset table can be written before the data.
        var blocks = new byte[blockCount][];
        Parallel.For(0, blockCount, blockIndex =>
                                    {
                                        var firstLine = blockIndex * linesPerBlock;
                                        var lineCount = Math.Min(linesPerBlock, height - firstLine);
                                        blocks[blockIndex] = EncodeBlock(width, firstLine, lineCount, readRow, compression);
                                    });

        var header = BuildHeader(width, height, compression);

        // Each block on disk is: int32 first scanline, int32 byte count, data.
        const int blockPrefixSize = 8;
        var offsetTableSize = (long)blockCount * sizeof(ulong);
        var nextBlockOffset = (ulong)(header.Length + offsetTableSize);

        using var fileStream = new FileStream(filepath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        using var writer = new BinaryWriter(fileStream);

        writer.Write(header);

        for (var i = 0; i < blockCount; i++)
        {
            writer.Write(nextBlockOffset);
            nextBlockOffset += (ulong)(blockPrefixSize + blocks[i].Length);
        }

        for (var i = 0; i < blockCount; i++)
        {
            writer.Write(i * linesPerBlock);
            writer.Write(blocks[i].Length);
            writer.Write(blocks[i]);
        }
    }

    /// <summary>
    /// Converts interleaved RGBA source rows into EXR's planar-per-scanline layout (all A, then all B, G, R of a line)
    /// and compresses the result if requested.
    /// </summary>
    private static byte[] EncodeBlock(int width, int firstLine, int lineCount, Action<int, byte[]> readRow, Compression compression)
    {
        var rowBuffer = new byte[width * SourceBytesPerPixel];
        var channelBytes = width * 2;
        var raw = new byte[lineCount * _channels.Length * channelBytes];

        for (var line = 0; line < lineCount; line++)
        {
            readRow(firstLine + line, rowBuffer);

            var lineStart = line * _channels.Length * channelBytes;
            for (var c = 0; c < _channels.Length; c++)
            {
                var dest = lineStart + c * channelBytes;
                var sourceOffset = _channels[c].SourceOffset;
                for (var x = 0; x < width; x++)
                {
                    raw[dest++] = rowBuffer[x * SourceBytesPerPixel + sourceOffset];
                    raw[dest++] = rowBuffer[x * SourceBytesPerPixel + sourceOffset + 1];
                }
            }
        }

        if (compression == Compression.None)
            return raw;

        var compressed = ZipCompress(raw);

        // Like OpenEXR: if compression doesn't help, store the original bytes. Readers detect this by size.
        return compressed.Length < raw.Length ? compressed : raw;
    }

    /// <summary>OpenEXR's ZIP: de-interleave even/odd bytes, delta-encode, then zlib.</summary>
    private static byte[] ZipCompress(byte[] raw)
    {
        var length = raw.Length;
        var tmp = new byte[length];

        var evenIndex = 0;
        var oddIndex = (length + 1) / 2;
        for (var i = 0; i < length; i += 2)
        {
            tmp[evenIndex++] = raw[i];
            if (i + 1 < length)
                tmp[oddIndex++] = raw[i + 1];
        }

        var previous = (int)tmp[0];
        for (var i = 1; i < length; i++)
        {
            var current = (int)tmp[i];
            tmp[i] = unchecked((byte)(current - previous + (128 + 256)));
            previous = current;
        }

        using var output = new MemoryStream(length / 2 + 64);
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(tmp, 0, length);
        }

        return output.ToArray();
    }

    private static byte[] BuildHeader(int width, int height, Compression compression)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.ASCII);

        // Magic number, then version 2 with no flags (single-part, scanline, short names).
        w.Write((byte)0x76);
        w.Write((byte)0x2f);
        w.Write((byte)0x31);
        w.Write((byte)0x01);
        w.Write(2);

        WriteAttribute(w, "channels", "chlist", v =>
                                                {
                                                    foreach (var (name, _) in _channels)
                                                    {
                                                        WriteString(v, name);
                                                        v.Write(HalfPixelType);
                                                        v.Write((byte)0); // pLinear
                                                        v.Write((byte)0); // reserved
                                                        v.Write((byte)0);
                                                        v.Write((byte)0);
                                                        v.Write(1); // xSampling
                                                        v.Write(1); // ySampling
                                                    }

                                                    v.Write((byte)0); // end of list
                                                });

        WriteAttribute(w, "compression", "compression", v => v.Write((byte)compression));
        WriteAttribute(w, "dataWindow", "box2i", v => WriteBox(v, width, height));
        WriteAttribute(w, "displayWindow", "box2i", v => WriteBox(v, width, height));
        WriteAttribute(w, "lineOrder", "lineOrder", v => v.Write((byte)0)); // INCREASING_Y
        WriteAttribute(w, "pixelAspectRatio", "float", v => v.Write(1f));
        WriteAttribute(w, "screenWindowCenter", "v2f", v =>
                                                       {
                                                           v.Write(0f);
                                                           v.Write(0f);
                                                       });
        WriteAttribute(w, "screenWindowWidth", "float", v => v.Write(1f));

        w.Write((byte)0); // end of header
        w.Flush();
        return stream.ToArray();
    }

    private static void WriteBox(BinaryWriter w, int width, int height)
    {
        w.Write(0);
        w.Write(0);
        w.Write(width - 1);
        w.Write(height - 1);
    }

    private static void WriteAttribute(BinaryWriter w, string name, string type, Action<BinaryWriter> writeValue)
    {
        using var valueStream = new MemoryStream();
        using (var valueWriter = new BinaryWriter(valueStream, Encoding.ASCII, leaveOpen: true))
        {
            writeValue(valueWriter);
        }

        WriteString(w, name);
        WriteString(w, type);
        w.Write((int)valueStream.Length);
        w.Write(valueStream.GetBuffer(), 0, (int)valueStream.Length);
    }

    private static void WriteString(BinaryWriter w, string value)
    {
        w.Write(Encoding.ASCII.GetBytes(value));
        w.Write((byte)0);
    }
}
