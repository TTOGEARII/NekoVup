using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;

namespace NekoVup.Core.Imaging;

public readonly record struct PngHeader(int Width, int Height, int BitDepth, int ColorType, bool Interlaced)
{
    /// <summary>파일에 저장된 픽셀당 샘플 수 (팔레트는 1).</summary>
    public int SourceChannels => ColorType switch
    {
        0 => 1,
        2 => 3,
        3 => 1,
        4 => 2,
        6 => 4,
        _ => throw new InvalidDataException($"알 수 없는 PNG 색 형식: {ColorType}"),
    };
}

/// <summary>
/// 외부 라이브러리 없이 동작하는 PNG 읽기/쓰기. ffmpeg·ProPainter(OpenCV)가 만드는 8비트(및 16비트) 비인터레이스 PNG를 대상으로 한다.
/// 압축은 .NET 내장 zlib을 쓴다. 프레임마다 불리므로 임시 버퍼는 모두 <see cref="ArrayPool{T}"/>에서 빌린다.
/// </summary>
public static class PngCodec
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const uint IHDR = 0x49484452, PLTE = 0x504C5445, IDAT = 0x49444154, IEND = 0x49454E44;

    public static PngHeader ReadHeader(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || !png[..8].SequenceEqual(Signature))
            throw new InvalidDataException("PNG 파일이 아닙니다.");
        if (BinaryPrimitives.ReadUInt32BigEndian(png[12..]) != IHDR)
            throw new InvalidDataException("PNG 머리 부분이 손상되었습니다.");

        var d = png.Slice(16, 13);
        var header = new PngHeader(
            checked((int)BinaryPrimitives.ReadUInt32BigEndian(d)),
            checked((int)BinaryPrimitives.ReadUInt32BigEndian(d[4..])),
            d[8], d[9], d[12] != 0);
        if (header.Width <= 0 || header.Height <= 0)
            throw new InvalidDataException("PNG 크기가 올바르지 않습니다.");
        return header;
    }

    /// <summary>파일을 읽어 풀에서 빌린 버퍼에 담는다. 다 쓰면 <see cref="ArrayPool{T}.Return"/>으로 돌려줘야 한다.</summary>
    public static byte[] ReadFilePooled(string path, out int length)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        length = checked((int)RandomAccess.GetLength(handle));
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        int read = 0;
        while (read < length)
        {
            int n = RandomAccess.Read(handle, buffer.AsSpan(read, length - read), read);
            if (n <= 0) throw new EndOfStreamException($"파일을 끝까지 읽지 못했습니다: {path}");
            read += n;
        }
        return buffer;
    }

    /// <summary>
    /// PNG를 풀어 dest에 채운다. dest 크기는 Width × Height × channels 이상이어야 한다.
    /// channels: 1 = 밝기, 3 = RGB, 4 = RGBA.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> png, Span<byte> dest, int channels)
    {
        if (channels is not (1 or 3 or 4)) throw new ArgumentOutOfRangeException(nameof(channels));
        var header = ReadHeader(png);
        if (header.Interlaced) throw new NotSupportedException("인터레이스 PNG는 지원하지 않습니다.");
        if (header.BitDepth is not (8 or 16) || (header.ColorType == 3 && header.BitDepth != 8))
            throw new NotSupportedException($"지원하지 않는 PNG 비트 깊이: {header.BitDepth}");

        int width = header.Width, height = header.Height;
        int bytesPerPixel = header.SourceChannels * header.BitDepth / 8;
        int rowBytes = checked(width * bytesPerPixel);
        int rawLength = checked(height * (rowBytes + 1));
        if (dest.Length < (long)width * height * channels) throw new ArgumentException("대상 버퍼가 작습니다.", nameof(dest));

        // IDAT 조각들을 한 줄로 이어 붙인다.
        var compressed = ArrayPool<byte>.Shared.Rent(png.Length);
        var raw = ArrayPool<byte>.Shared.Rent(rawLength);
        try
        {
            int compressedLength = 0;
            ReadOnlySpan<byte> palette = default;
            for (int pos = 8; pos + 12 <= png.Length;)
            {
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[pos..]));
                uint type = BinaryPrimitives.ReadUInt32BigEndian(png[(pos + 4)..]);
                if (length < 0 || pos + 12 + (long)length > png.Length) throw new InvalidDataException("PNG 조각이 잘렸습니다.");
                var data = png.Slice(pos + 8, length);
                if (type == IDAT)
                {
                    data.CopyTo(compressed.AsSpan(compressedLength));
                    compressedLength += length;
                }
                else if (type == PLTE) palette = data;
                else if (type == IEND) break;
                pos += 12 + length;
            }
            if (compressedLength == 0) throw new InvalidDataException("PNG에 그림 데이터가 없습니다.");
            if (header.ColorType == 3 && palette.IsEmpty) throw new InvalidDataException("팔레트 PNG에 팔레트가 없습니다.");

            using (var memory = new MemoryStream(compressed, 0, compressedLength, writable: false))
            using (var zlib = new ZLibStream(memory, CompressionMode.Decompress))
            {
                int read = zlib.ReadAtLeast(raw.AsSpan(0, rawLength), rawLength, throwOnEndOfStream: false);
                if (read != rawLength) throw new InvalidDataException("PNG 그림 데이터가 모자랍니다.");
            }

            Unfilter(raw.AsSpan(0, rawLength), height, rowBytes, bytesPerPixel);

            for (int y = 0; y < height; y++)
            {
                var src = raw.AsSpan(y * (rowBytes + 1) + 1, rowBytes);
                var dst = dest.Slice(y * width * channels, width * channels);
                ConvertRow(src, dst, header, palette, channels);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static void Unfilter(Span<byte> raw, int height, int rowBytes, int bpp)
    {
        int stride = rowBytes + 1;
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * stride];
            var row = raw.Slice(y * stride + 1, rowBytes);
            ReadOnlySpan<byte> prior = y == 0 ? default : raw.Slice((y - 1) * stride + 1, rowBytes);

            switch (filter)
            {
                case 0:
                    break;
                case 1: // Sub
                    for (int i = bpp; i < row.Length; i++) row[i] += row[i - bpp];
                    break;
                case 2: // Up
                    if (y > 0)
                    {
                        int i = 0;
                        for (; i <= row.Length - Vector<byte>.Count; i += Vector<byte>.Count)
                            (new Vector<byte>(row[i..]) + new Vector<byte>(prior[i..])).CopyTo(row[i..]);
                        for (; i < row.Length; i++) row[i] += prior[i];
                    }
                    break;
                case 3: // Average
                    for (int i = 0; i < row.Length; i++)
                    {
                        int left = i >= bpp ? row[i - bpp] : 0;
                        int up = y > 0 ? prior[i] : 0;
                        row[i] += (byte)((left + up) >> 1);
                    }
                    break;
                case 4: // Paeth
                    for (int i = 0; i < row.Length; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0;
                        int b = y > 0 ? prior[i] : 0;
                        int c = i >= bpp && y > 0 ? prior[i - bpp] : 0;
                        int p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                    }
                    break;
                default:
                    throw new InvalidDataException($"알 수 없는 PNG 필터: {filter}");
            }
        }
    }

    private static void ConvertRow(ReadOnlySpan<byte> src, Span<byte> dst, PngHeader header, ReadOnlySpan<byte> palette, int channels)
    {
        // 가장 흔한 경우는 그대로 복사한다.
        if (header.BitDepth == 8 &&
            ((header.ColorType == 2 && channels == 3) || (header.ColorType == 6 && channels == 4) || (header.ColorType == 0 && channels == 1)))
        {
            src.CopyTo(dst);
            return;
        }

        int step = header.BitDepth / 8; // 16비트는 상위 바이트만 쓴다
        int sourceChannels = header.SourceChannels;
        int width = header.Width;
        for (int x = 0; x < width; x++)
        {
            var s = src.Slice(x * sourceChannels * step);
            byte r, g, b, a = 255;
            switch (header.ColorType)
            {
                case 0: r = g = b = s[0]; break;
                case 2: r = s[0]; g = s[step]; b = s[2 * step]; break;
                case 3:
                    int index = s[0] * 3;
                    if (index + 2 >= palette.Length) throw new InvalidDataException("PNG 팔레트 범위를 벗어난 값이 있습니다.");
                    r = palette[index]; g = palette[index + 1]; b = palette[index + 2];
                    break;
                case 4: r = g = b = s[0]; a = s[step]; break;
                default: r = s[0]; g = s[step]; b = s[2 * step]; a = s[3 * step]; break;
            }

            var d = dst.Slice(x * channels);
            if (channels == 1) d[0] = (byte)((r * 77 + g * 150 + b * 29 + 128) >> 8);
            else
            {
                d[0] = r; d[1] = g; d[2] = b;
                if (channels == 4) d[3] = a;
            }
        }
    }

    /// <summary>픽셀을 PNG 파일로 쓴다. channels: 1 = 밝기, 3 = RGB, 4 = RGBA.</summary>
    public static void Write(string path, ReadOnlySpan<byte> pixels, int width, int height, int channels, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var buffer = new PooledBufferStream(Math.Max(4096, pixels.Length / 4));
        Encode(buffer, pixels, width, height, channels, level);
        using var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None);
        RandomAccess.Write(handle, buffer.WrittenSpan, 0);
    }

    public static void Encode(Stream output, ReadOnlySpan<byte> pixels, int width, int height, int channels, CompressionLevel level = CompressionLevel.Fastest)
    {
        int colorType = channels switch
        {
            1 => 0,
            3 => 2,
            4 => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(channels)),
        };
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int rowBytes = checked(width * channels);
        if (pixels.Length < (long)rowBytes * height) throw new ArgumentException("픽셀 버퍼가 작습니다.", nameof(pixels));

        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8;
        ihdr[9] = (byte)colorType;
        ihdr[10] = ihdr[11] = ihdr[12] = 0;
        WriteChunk(output, IHDR, ihdr);

        // 필터는 모든 줄에 Up(윗줄과의 차)을 쓴다. 벡터 연산으로 빠르고, 평평한 영역(투명 부분)이 0으로 채워져 잘 눌린다.
        int rawLength = checked(height * (rowBytes + 1));
        var raw = ArrayPool<byte>.Shared.Rent(rawLength);
        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * rowBytes, rowBytes);
                var target = raw.AsSpan(y * (rowBytes + 1), rowBytes + 1);
                if (y == 0)
                {
                    target[0] = 0;
                    row.CopyTo(target[1..]);
                    continue;
                }

                target[0] = 2;
                var prior = pixels.Slice((y - 1) * rowBytes, rowBytes);
                var filtered = target[1..];
                int i = 0;
                for (; i <= rowBytes - Vector<byte>.Count; i += Vector<byte>.Count)
                    (new Vector<byte>(row[i..]) - new Vector<byte>(prior[i..])).CopyTo(filtered[i..]);
                for (; i < rowBytes; i++) filtered[i] = (byte)(row[i] - prior[i]);
            }

            using var compressed = new PooledBufferStream(Math.Max(4096, rawLength / 4));
            using (var zlib = new ZLibStream(compressed, level, leaveOpen: true))
                zlib.Write(raw, 0, rawLength);
            WriteChunk(output, IDAT, compressed.WrittenSpan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }

        WriteChunk(output, IEND, default);
    }

    private static void WriteChunk(Stream output, uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(head[4..], type);
        output.Write(head);
        output.Write(data);

        uint crc = Crc32.Append(Crc32.Append(0, head[4..]), data);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        output.Write(tail);
    }
}

/// <summary>PNG 조각 검증용 CRC-32 (IEEE 802.3).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        // 8바이트씩 처리하는 표 (slicing-by-8).
        var table = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        for (int n = 0; n < 256; n++)
        {
            uint c = table[n];
            for (int slice = 1; slice < 8; slice++)
            {
                c = table[c & 0xFF] ^ (c >> 8);
                table[slice * 256 + n] = c;
            }
        }
        return table;
    }

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        var t = Table;
        while (data.Length >= 8)
        {
            uint lo = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ c;
            uint hi = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            c = t[7 * 256 + (lo & 0xFF)] ^ t[6 * 256 + ((lo >> 8) & 0xFF)] ^ t[5 * 256 + ((lo >> 16) & 0xFF)] ^ t[4 * 256 + (lo >> 24)]
              ^ t[3 * 256 + (hi & 0xFF)] ^ t[2 * 256 + ((hi >> 8) & 0xFF)] ^ t[1 * 256 + ((hi >> 16) & 0xFF)] ^ t[hi >> 24];
            data = data[8..];
        }
        foreach (byte b in data) c = t[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}

/// <summary>풀에서 빌린 배열에 쌓는 쓰기 전용 스트림. 프레임마다 큰 배열을 새로 할당하지 않기 위한 것.</summary>
internal sealed class PooledBufferStream(int initialCapacity) : Stream
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));
    private int _length;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public override void Write(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + data.Length));
            _buffer.AsSpan(0, _length).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = bigger;
        }
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void WriteByte(byte value) => Write([value]);
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
        base.Dispose(disposing);
    }
}
