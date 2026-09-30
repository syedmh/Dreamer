using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Husaynia.BaselineCapture;

public sealed record PngArtifactInspection(
    int Width,
    int Height,
    long Length,
    string Sha256,
    double ByteEntropy);

public static class PngArtifactInspector
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    public const int MaximumPngBytes = 64 * 1024 * 1024;
    private const int MaximumDecodedBytes = 64 * 1024 * 1024;

    public static PngArtifactInspection Inspect(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length is < 67 or > MaximumPngBytes
            || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("png-invalid-signature-or-size");
        }

        var offset = Signature.Length;
        var sawHeader = false;
        var sawImageData = false;
        var sawEnd = false;
        var sawPalette = false;
        var imageDataEnded = false;
        var chunkIndex = 0;
        var width = 0;
        var height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        using var compressed = new MemoryStream();

        while (offset < png.Length)
        {
            if (png.Length - offset < 12)
            {
                throw new InvalidDataException("png-truncated-chunk");
            }

            var rawLength = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (rawLength > MaximumPngBytes)
            {
                throw new InvalidDataException("png-invalid-chunk-length");
            }

            var length = (int)rawLength;
            offset += 4;
            var typeBytes = png.AsSpan(offset, 4);
            var type = Encoding.ASCII.GetString(typeBytes);
            offset += 4;
            if (length < 0 || length > MaximumPngBytes || png.Length - offset < length + 4)
            {
                throw new InvalidDataException("png-invalid-chunk-length");
            }

            var data = png.AsSpan(offset, length);
            offset += length;
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            offset += 4;
            if (ComputeCrc(typeBytes, data) != expectedCrc)
            {
                throw new InvalidDataException("png-crc-mismatch");
            }

            if (chunkIndex++ == 0 && type != "IHDR")
            {
                throw new InvalidDataException("png-header-not-first");
            }

            if (sawImageData && type is not ("IDAT" or "IEND"))
            {
                imageDataEnded = true;
            }

            switch (type)
            {
                case "IHDR":
                    if (sawHeader || sawImageData || length != 13)
                    {
                        throw new InvalidDataException("png-invalid-header");
                    }

                    var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                    var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                    if (rawWidth > int.MaxValue || rawHeight > int.MaxValue)
                    {
                        throw new InvalidDataException("png-dimensions-too-large");
                    }

                    width = (int)rawWidth;
                    height = (int)rawHeight;
                    bitDepth = data[8];
                    colorType = data[9];
                    if (width <= 0 || height <= 0
                        || data[10] != 0
                        || data[11] != 0
                        || data[12] != 0
                        || !IsSupportedColorDepth(colorType, bitDepth))
                    {
                        throw new InvalidDataException("png-unsupported-header");
                    }

                    sawHeader = true;
                    break;
                case "PLTE":
                    if (!sawHeader
                        || sawImageData
                        || sawPalette
                        || length == 0
                        || length % 3 != 0
                        || length > 768)
                    {
                        throw new InvalidDataException("png-invalid-palette");
                    }

                    sawPalette = true;
                    break;
                case "IDAT":
                    if (!sawHeader || sawEnd || imageDataEnded)
                    {
                        throw new InvalidDataException("png-invalid-image-data-order");
                    }

                    compressed.Write(data);
                    if (compressed.Length > MaximumPngBytes)
                    {
                        throw new InvalidDataException("png-image-data-too-large");
                    }

                    sawImageData = true;
                    break;
                case "IEND":
                    if (!sawHeader || !sawImageData || sawEnd || length != 0)
                    {
                        throw new InvalidDataException("png-invalid-end");
                    }

                    sawEnd = true;
                    if (offset != png.Length)
                    {
                        throw new InvalidDataException("png-trailing-data");
                    }

                    break;
                default:
                    if (type.Length != 4
                        || type.Any(character =>
                            !((character >= 'A' && character <= 'Z')
                              || (character >= 'a' && character <= 'z')))
                        || char.IsUpper(type[0]))
                    {
                        throw new InvalidDataException("png-unknown-critical-chunk");
                    }

                    break;
            }
        }

        if (!sawHeader || !sawImageData || !sawEnd || colorType == 3 && !sawPalette)
        {
            throw new InvalidDataException("png-incomplete");
        }

        ValidateDecodedScanlines(compressed.ToArray(), width, height, colorType, bitDepth);
        return new(
            width,
            height,
            png.LongLength,
            CaptureIO.Sha256(png),
            CaptureIO.ComputeByteEntropy(png));
    }

    private static void ValidateDecodedScanlines(
        byte[] compressed,
        int width,
        int height,
        byte colorType,
        byte bitDepth)
    {
        var samplesPerPixel = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("png-unsupported-color-type")
        };
        long rowBytes;
        long decodedBytes;
        try
        {
            rowBytes = checked(((long)width * samplesPerPixel * bitDepth + 7) / 8);
            decodedBytes = checked((rowBytes + 1) * height);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("png-decoded-data-too-large", ex);
        }
        if (decodedBytes > MaximumDecodedBytes)
        {
            throw new InvalidDataException("png-decoded-data-too-large");
        }

        using var input = new MemoryStream(compressed, writable: false);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
        var row = new byte[checked((int)rowBytes + 1)];
        for (var y = 0; y < height; y++)
        {
            ReadExactly(inflater, row);
            if (row[0] > 4)
            {
                throw new InvalidDataException("png-invalid-filter");
            }
        }

        if (inflater.ReadByte() != -1)
        {
            throw new InvalidDataException("png-decoded-length-mismatch");
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                throw new InvalidDataException("png-truncated-image-data");
            }

            offset += read;
        }
    }

    private static bool IsSupportedColorDepth(byte colorType, byte bitDepth) =>
        colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false
        };

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type)
        {
            crc = UpdateCrc(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdateCrc(crc, value);
        }

        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }
}
