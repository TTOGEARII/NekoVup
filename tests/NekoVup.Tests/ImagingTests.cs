using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using NekoVup.Core.Chunks;
using NekoVup.Core.Imaging;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Regions;

namespace NekoVup.Tests;

public class PngCodecTests
{
    private static byte[] Noise(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>줄마다 다른 필터(0~4)를 쓰는 참조용 PNG 인코더. 디코더가 모든 필터를 푸는지 확인하는 데 쓴다.</summary>
    private static byte[] EncodeWithAllFilters(byte[] pixels, int width, int height, int channels, int colorType, int bitDepth = 8)
    {
        int bpp = channels * bitDepth / 8, rowBytes = width * bpp;
        var raw = new byte[height * (rowBytes + 1)];
        for (int y = 0; y < height; y++)
        {
            int filter = y % 5;
            raw[y * (rowBytes + 1)] = (byte)filter;
            for (int i = 0; i < rowBytes; i++)
            {
                int x = pixels[y * rowBytes + i];
                int a = i >= bpp ? pixels[y * rowBytes + i - bpp] : 0;
                int b = y > 0 ? pixels[(y - 1) * rowBytes + i] : 0;
                int c = i >= bpp && y > 0 ? pixels[(y - 1) * rowBytes + i - bpp] : 0;
                int p = a + b - c;
                int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                int paeth = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                int predicted = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => paeth };
                raw[y * (rowBytes + 1) + 1 + i] = (byte)(x - predicted);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        var idat = compressed.ToArray();

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "tEXt", Encoding.ASCII.GetBytes("Comment\0test"));
        // IDAT를 일부러 여러 조각으로 나눈다 (libpng·ffmpeg가 그렇게 쓴다)
        for (int offset = 0; offset < idat.Length; offset += 37)
            Chunk(png, "IDAT", idat.AsSpan(offset, Math.Min(37, idat.Length - offset)).ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> head = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, head[4..]);
            s.Write(head);
            s.Write(data);
            Span<byte> crc = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Append(Crc32.Append(0, head[4..]), data));
            s.Write(crc);
        }
    }

    [Fact]
    public void Crc32_Matches_Known_Value()
    {
        Assert.Equal(0xCBF43926u, Crc32.Append(0, "123456789"u8));
        // 8바이트 단위 처리와 나머지 처리가 이어져도 같은 값이어야 한다
        var data = Noise(1000, 1);
        uint whole = Crc32.Append(0, data);
        uint split = Crc32.Append(Crc32.Append(0, data.AsSpan(0, 333)), data.AsSpan(333));
        Assert.Equal(whole, split);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Round_Trips_Exactly(int channels)
    {
        const int w = 67, h = 41; // 벡터 폭으로 나누어떨어지지 않는 크기
        var pixels = Noise(w * h * channels, channels);
        using var stream = new MemoryStream();
        PngCodec.Encode(stream, pixels, w, h, channels);
        var png = stream.ToArray();

        var header = PngCodec.ReadHeader(png);
        Assert.Equal((w, h, 8), (header.Width, header.Height, header.BitDepth));
        Assert.Equal(channels, header.SourceChannels);

        var decoded = new byte[w * h * channels];
        PngCodec.Decode(png, decoded, channels);
        Assert.Equal(pixels, decoded);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 6)]
    [InlineData(1, 0)]
    [InlineData(2, 4)]
    public void Decodes_All_Five_Filters_And_Split_Idat(int channels, int colorType)
    {
        const int w = 53, h = 23;
        var pixels = Noise(w * h * channels, 7);
        var png = EncodeWithAllFilters(pixels, w, h, channels, colorType);

        var rgba = new byte[w * h * 4];
        PngCodec.Decode(png, rgba, 4);
        for (int i = 0; i < w * h; i++)
        {
            var (r, g, b, a) = channels switch
            {
                1 => (pixels[i], pixels[i], pixels[i], (byte)255),
                2 => (pixels[i * 2], pixels[i * 2], pixels[i * 2], pixels[i * 2 + 1]),
                3 => (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2], (byte)255),
                _ => (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]),
            };
            Assert.Equal((r, g, b, a), (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]));
        }
    }

    [Fact]
    public void Decodes_16_Bit_By_Taking_High_Byte()
    {
        const int w = 9, h = 5;
        var pixels = Noise(w * h * 3 * 2, 3);
        var png = EncodeWithAllFilters(pixels, w, h, 3, colorType: 2, bitDepth: 16);
        var rgb = new byte[w * h * 3];
        PngCodec.Decode(png, rgb, 3);
        for (int i = 0; i < rgb.Length; i++) Assert.Equal(pixels[i * 2], rgb[i]);
    }

    [Fact]
    public void Converts_Rgb_To_Gray_And_Rgba()
    {
        byte[] pixels = [255, 255, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255];
        using var stream = new MemoryStream();
        PngCodec.Encode(stream, pixels, 4, 1, 3);
        var gray = new byte[4];
        PngCodec.Decode(stream.ToArray(), gray, 1);
        Assert.Equal(255, gray[0]);
        Assert.Equal(0, gray[1]);
        Assert.InRange(gray[2], 70, 85);
        Assert.InRange(gray[3], 25, 35);

        var rgba = new byte[16];
        PngCodec.Decode(stream.ToArray(), rgba, 4);
        Assert.Equal([255, 255, 255, 255, 0, 0, 0, 255, 255, 0, 0, 255, 0, 0, 255, 255], rgba);
    }

    [Fact]
    public void Writes_And_Reads_Files_With_Korean_Paths()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nekoVup 테스트 " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "마스크 파일.png");
            var pixels = Noise(32 * 16 * 4, 9);
            PngCodec.Write(path, pixels, 32, 16, 4);

            var file = PngCodec.ReadFilePooled(path, out int length);
            var decoded = new byte[pixels.Length];
            PngCodec.Decode(file.AsSpan(0, length), decoded, 4);
            System.Buffers.ArrayPool<byte>.Shared.Return(file);
            Assert.Equal(pixels, decoded);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Rejects_Broken_Files()
    {
        Assert.Throws<InvalidDataException>(() => PngCodec.ReadHeader(new byte[100]));
        using var stream = new MemoryStream();
        PngCodec.Encode(stream, new byte[30], 10, 1, 3);
        var truncated = stream.ToArray()[..^20];
        Assert.ThrowsAny<Exception>(() => PngCodec.Decode(truncated, new byte[30], 3));
    }
}

public class ImageOpsTests
{
    [Fact]
    public void Dilate_Grows_Rectangle_By_Radius()
    {
        const int w = 40, h = 30;
        var src = new byte[w * h];
        ImageOps.FillRect(src, w, h, new PixelRect(10, 10, 5, 3), 255);
        var dst = new byte[w * h];
        ImageOps.Dilate(src, dst, w, h, 4);

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            bool inside = x >= 6 && x < 19 && y >= 6 && y < 17;
            Assert.Equal(inside ? 255 : 0, dst[y * w + x]);
        }
    }

    [Fact]
    public void Dilate_Clips_At_Borders_And_Radius_Zero_Copies()
    {
        const int w = 8, h = 8;
        var src = new byte[w * h];
        src[0] = 255;
        var dst = new byte[w * h];
        ImageOps.Dilate(src, dst, w, h, 2);
        Assert.Equal(9, dst.Count(v => v == 255));

        ImageOps.Dilate(src, dst, w, h, 0);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void Gaussian_Keeps_Flat_Areas_And_Softens_Edges()
    {
        const int w = 64, h = 16;
        var src = new byte[w * h];
        ImageOps.FillRect(src, w, h, new PixelRect(32, 0, 32, 16), 255);
        var dst = new byte[w * h];
        ImageOps.GaussianBlur(src, dst, w, h, 3);

        Assert.Equal(0, dst[8 * w + 5]);
        Assert.Equal(255, dst[8 * w + 60]);
        Assert.InRange(dst[8 * w + 31], 100, 127); // 경계 바로 왼쪽: 절반에 조금 못 미침
        Assert.InRange(dst[8 * w + 32], 128, 155);
        for (int x = 1; x < w; x++) Assert.True(dst[8 * w + x] >= dst[8 * w + x - 1]); // 단조 증가
        Assert.Equal(0, dst[8 * w + 21]);   // 3σ(9px) 밖은 그대로
        Assert.Equal(255, dst[8 * w + 42]);
    }

    [Fact]
    public void Resize_Same_Size_Is_Copy_And_Flat_Stays_Flat()
    {
        var src = Enumerable.Range(0, 6 * 4 * 3).Select(i => (byte)(i * 3)).ToArray();
        var same = new byte[src.Length];
        ImageOps.ResizeBicubic(src, 6, 4, same, 6, 4, 3);
        Assert.Equal(src, same);

        var flat = Enumerable.Repeat((byte)137, 10 * 10 * 3).ToArray();
        var big = new byte[25 * 17 * 3];
        ImageOps.ResizeBicubic(flat, 10, 10, big, 25, 17, 3);
        Assert.All(big, v => Assert.Equal(137, v));
    }

    [Fact]
    public void Resize_Upscales_A_Gradient_Smoothly()
    {
        const int sw = 16, sh = 8, dw = 32, dh = 16;
        var src = new byte[sw * sh];
        for (int y = 0; y < sh; y++)
        for (int x = 0; x < sw; x++)
            src[y * sw + x] = (byte)(x * 16);
        var dst = new byte[dw * dh];
        ImageOps.ResizeBicubic(src, sw, sh, dst, dw, dh, 1);

        // 가운데 부분은 원래 기울기(픽셀당 8)를 그대로 따라간다
        for (int x = 4; x < dw - 4; x++)
        {
            double expected = ((x + 0.5) / 2 - 0.5) * 16;
            Assert.InRange(dst[8 * dw + x], expected - 1.5, expected + 1.5);
        }
    }
}

public class MaskBuilderTests
{
    private static ProcessRegion Region() =>
        Assert.Single(RegionPlanner.Plan([new PixelRect(1040, 40, 190, 56)], 1280, 720));

    [Fact]
    public void Mask_Is_White_On_Grown_Box_Only()
    {
        var region = Region();
        var mask = MaskBuilder.BuildMask(region);
        int w = region.Rect.Width;

        Assert.Equal(region.Rect.Width * region.Rect.Height, mask.Length);
        Assert.Equal(194 * 60, mask.Count(v => v == 255));
        Assert.Equal(mask.Length - 194 * 60, mask.Count(v => v == 0));
        Assert.Equal(255, mask[38 * w + 94]);   // 박스에서 2px 바깥 모서리
        Assert.Equal(0, mask[37 * w + 94]);
        Assert.Equal(0, mask[38 * w + 93]);
    }

    [Fact]
    public void Alpha_Is_Solid_Over_Inpainted_Area_And_Fades_To_Zero()
    {
        var region = Region();
        var alpha = MaskBuilder.BuildAlpha(region, maskDilation: 4);
        int w = region.Rect.Width, h = region.Rect.Height;
        var maskBox = region.MaskBoxes[0];

        int reach = MaskBuilder.AlphaReach(maskGrow: 0, maskDilation: 4);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            // 마스크 박스까지의 거리(체비쇼프)
            int dx = Math.Max(Math.Max(maskBox.X - x, x - (maskBox.Right - 1)), 0);
            int dy = Math.Max(Math.Max(maskBox.Y - y, y - (maskBox.Bottom - 1)), 0);
            int distance = Math.Max(dx, dy);
            byte a = alpha[y * w + x];
            if (distance <= 4) Assert.Equal(255, a);            // ProPainter가 손대는 범위는 전부 불투명
            else if (distance > reach) Assert.Equal(0, a);      // 멀리는 완전히 투명 → 원본 그대로
        }

        // 바깥으로 갈수록 줄어든다 (박스 오른쪽 가운데 줄)
        int row = maskBox.Y + maskBox.Height / 2;
        for (int x = maskBox.Right; x < w - 1; x++) Assert.True(alpha[row * w + x + 1] <= alpha[row * w + x]);
        Assert.InRange(alpha[row * w + maskBox.Right - 1 + 10], 100, 160); // 넓힌 띠의 경계(10px)에서 절반쯤
    }

    [Fact]
    public void Alpha_Widens_When_ProPainter_Works_On_A_Smaller_Copy()
    {
        Assert.Equal(4, MaskBuilder.InnerRadius(4, 1.0));
        Assert.Equal(8, MaskBuilder.InnerRadius(4, 0.5));
        Assert.Equal(5, MaskBuilder.InnerRadius(4, 0.874));

        var region = Region();
        int w = region.Rect.Width;
        var normal = MaskBuilder.BuildAlpha(region, 4, 1.0);
        var shrunk = MaskBuilder.BuildAlpha(region, 4, 0.5);
        Assert.True(shrunk.Count(v => v == 255) > normal.Count(v => v == 255));
        var box = region.MaskBoxes[0];
        Assert.Equal(255, shrunk[(box.Y + 5) * w + box.Right - 1 + 8]);
        Assert.NotEqual(255, normal[(box.Y + 5) * w + box.Right - 1 + 8]);
    }
}

public class ChunkMergerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nekoVup 병합 " + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private void WriteChunk(string root, ChunkRange chunk, int w, int h, Func<int, byte> valueForFrame)
    {
        string dir = ChunkMerger.ChunkFramesDir(root, chunk);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < chunk.Length; i++)
        {
            var pixels = new byte[w * h * 3];
            Array.Fill(pixels, valueForFrame(chunk.Start + i));
            PngCodec.Write(Path.Combine(dir, ChunkMerger.ChunkFrameName(i)), pixels, w, h, 3);
        }
    }

    private static byte[] ReadRgba(string path, int w, int h)
    {
        var bytes = File.ReadAllBytes(path);
        var rgba = new byte[w * h * 4];
        PngCodec.Decode(bytes, rgba, 4);
        return rgba;
    }

    [Fact]
    public void Crossfades_Overlap_And_Attaches_Alpha()
    {
        const int w = 16, h = 8;
        string root = Path.Combine(_dir, "pp"), output = Path.Combine(_dir, "out");
        var chunks = ChunkPlanner.Plan(50, chunkLength: 30, overlap: 10); // [0,30) [20,50)
        Assert.Equal(2, chunks.Count);
        WriteChunk(root, chunks[0], w, h, _ => 100);
        WriteChunk(root, chunks[1], w, h, _ => 210);

        var alpha = new byte[w * h];
        ImageOps.FillRect(alpha, w, h, new PixelRect(4, 2, 4, 4), 255);
        alpha[3 * w + 9] = 77;

        int reported = 0;
        ChunkMerger.Merge(root, chunks, output, w, h, alpha, new Counter(() => Interlocked.Increment(ref reported)), CancellationToken.None);

        Assert.Equal(50, Directory.GetFiles(output, "*.png").Length);
        Assert.Equal(50, reported);
        Assert.True(File.Exists(Path.Combine(output, "000001.png")));
        Assert.True(File.Exists(Path.Combine(output, "000050.png")));
        Assert.Empty(Directory.GetFiles(output, "*.tmp"));

        byte ColorAt(int frame) => ReadRgba(Path.Combine(output, ChunkMerger.SequenceFrameName(frame)), w, h)[(3 * w + 5) * 4];
        Assert.Equal(100, ColorAt(0));
        Assert.Equal(100, ColorAt(19));
        Assert.Equal(210, ColorAt(30));
        Assert.Equal(210, ColorAt(49));
        // 겹침 20..29: 가중치 (10,1) … (1,10) → 100에서 210으로 고르게 넘어간다
        Assert.Equal(110, ColorAt(20));
        Assert.Equal(200, ColorAt(29));
        for (int f = 20; f < 30; f++) Assert.True(ColorAt(f + 1) > ColorAt(f));

        var frame0 = ReadRgba(Path.Combine(output, "000001.png"), w, h);
        Assert.Equal(255, frame0[(3 * w + 5) * 4 + 3]);
        Assert.Equal(77, frame0[(3 * w + 9) * 4 + 3]);
        Assert.Equal(0, frame0[(3 * w + 14) * 4 + 3]);
        Assert.Equal(0, frame0[(3 * w + 14) * 4]);      // 알파에서 먼 곳은 색도 비운다
        Assert.Equal(100, frame0[(3 * w + 11) * 4]);    // 알파 경계 2px 안쪽은 색을 남긴다
        Assert.Equal(0, frame0[(3 * w + 11) * 4 + 3]);
    }

    [Fact]
    public void Per_Frame_Alpha_Follows_The_Frame_And_Leaves_The_Rest_Transparent()
    {
        const int w = 16, h = 8;
        string root = Path.Combine(_dir, "pp"), output = Path.Combine(_dir, "out");
        var chunks = ChunkPlanner.Plan(50, chunkLength: 30, overlap: 10); // [0,30) [20,50)
        WriteChunk(root, chunks[0], w, h, _ => 100);   // 뒤 조각은 워터마크가 지나가지 않아 복원하지 않았다

        var left = new byte[w * h];
        ImageOps.FillRect(left, w, h, new PixelRect(1, 2, 3, 3), 255);
        var right = new byte[w * h];
        ImageOps.FillRect(right, w, h, new PixelRect(11, 2, 3, 3), 255);

        int reported = 0;
        ChunkMerger.Merge(root, chunks, output, w, h, frame => frame < 10 ? left : frame < 20 ? right : null,
            new Counter(() => Interlocked.Increment(ref reported)), CancellationToken.None);

        Assert.Equal(50, Directory.GetFiles(output, "*.png").Length);
        Assert.Equal(50, reported);

        byte[] Frame(int frame) => ReadRgba(Path.Combine(output, ChunkMerger.SequenceFrameName(frame)), w, h);
        const int leftPixel = (3 * w + 2) * 4, rightPixel = (3 * w + 12) * 4;
        Assert.Equal(255, Frame(9)[leftPixel + 3]);
        Assert.Equal(100, Frame(9)[leftPixel]);
        Assert.Equal(0, Frame(9)[rightPixel + 3]);
        Assert.Equal(0, Frame(10)[leftPixel + 3]);
        Assert.Equal(255, Frame(10)[rightPixel + 3]);
        Assert.Equal(100, Frame(19)[rightPixel]);
        foreach (int frame in new[] { 20, 29, 30, 49 })
            Assert.All(Frame(frame), v => Assert.Equal(0, v));
    }

    [Fact]
    public void Upscales_Shrunken_Chunk_Frames_To_Region_Size()
    {
        const int w = 32, h = 16;
        string root = Path.Combine(_dir, "pp"), output = Path.Combine(_dir, "out");
        var chunks = ChunkPlanner.Plan(3);
        WriteChunk(root, chunks[0], 24, 12, f => (byte)(50 + f * 50));
        var alpha = Enumerable.Repeat((byte)255, w * h).ToArray();

        ChunkMerger.Merge(root, chunks, output, w, h, alpha, null, CancellationToken.None);

        var bytes = File.ReadAllBytes(Path.Combine(output, "000003.png"));
        var header = PngCodec.ReadHeader(bytes);
        Assert.Equal((w, h, 4), (header.Width, header.Height, header.SourceChannels));
        var rgba = new byte[w * h * 4];
        PngCodec.Decode(bytes, rgba, 4);
        for (int i = 0; i < w * h; i++) Assert.Equal(150, rgba[i * 4]);
    }

    [Fact]
    public void Skips_Frames_That_Already_Exist()
    {
        const int w = 8, h = 8;
        string root = Path.Combine(_dir, "pp"), output = Path.Combine(_dir, "out");
        var chunks = ChunkPlanner.Plan(4);
        WriteChunk(root, chunks[0], w, h, _ => 10);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "000002.png"), "이미 있는 파일");

        ChunkMerger.Merge(root, chunks, output, w, h, new byte[w * h], null, CancellationToken.None);

        Assert.Equal("이미 있는 파일", File.ReadAllText(Path.Combine(output, "000002.png")));
        Assert.Equal(4, Directory.GetFiles(output, "*.png").Length);
    }

    [Fact]
    public void Honours_Cancellation()
    {
        var chunks = ChunkPlanner.Plan(4);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ChunkMerger.Merge(Path.Combine(_dir, "pp"), chunks, Path.Combine(_dir, "out"), 8, 8, new byte[64], null, cancelled.Token));
    }

    private sealed class Counter(Action tick) : IProgress<int>
    {
        public void Report(int value) => tick();
    }
}
