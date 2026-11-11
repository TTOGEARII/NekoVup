using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NekoVup.Core.Chunks;
using NekoVup.Core.Imaging;

namespace NekoVup.Core.Pipeline;

/// <summary>
/// 조각별 ProPainter 결과를 하나의 프레임 열로 합친다.
/// 겹치는 구간은 가중 평균으로 부드럽게 넘기고, 알파를 붙여 RGBA PNG로 저장한다(ffmpeg가 원본 위에 얹는다).
/// </summary>
public static class ChunkMerger
{
    /// <summary>ProPainter가 <c>--save_frames</c>로 내놓는 프레임 폴더: &lt;출력 루트&gt;/&lt;입력 폴더 이름&gt;/frames.</summary>
    public static string ChunkFramesDir(string inpaintRoot, ChunkRange chunk) =>
        Path.Combine(inpaintRoot, ChunkName(chunk), "frames");

    public static string ChunkName(ChunkRange chunk) => "c" + chunk.Index.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>ProPainter 출력 파일 이름: 0부터 세는 4자리 번호.</summary>
    public static string ChunkFrameName(int indexInChunk) => indexInChunk.ToString("D4", CultureInfo.InvariantCulture) + ".png";

    /// <summary>ffmpeg의 %06d.png와 맞춘 이름: 1부터 세는 6자리 번호.</summary>
    public static string SequenceFrameName(int frame) => (frame + 1).ToString("D6", CultureInfo.InvariantCulture) + ".png";

    /// <param name="inpaintRoot">ProPainter 출력 루트.</param>
    /// <param name="outputDir">합친 RGBA 프레임을 쓸 폴더.</param>
    /// <param name="alpha">영역 크기의 알파 (<see cref="MaskBuilder.BuildAlpha"/>).</param>
    public static void Merge(
        string inpaintRoot, IReadOnlyList<ChunkRange> chunks, string outputDir,
        int regionWidth, int regionHeight, byte[] alpha,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (alpha.Length != regionWidth * regionHeight) throw new ArgumentException("알파 크기가 영역과 다릅니다.", nameof(alpha));
        Merge(inpaintRoot, chunks, outputDir, regionWidth, regionHeight, _ => alpha, progress, cancellationToken);
    }

    /// <summary>
    /// 프레임마다 알파가 다른 꼴 (움직이는 워터마크). <paramref name="alphaAt"/>이 null을 주는 프레임은 지울 것이 없는 프레임이다 —
    /// 복원 결과를 읽지 않고(그 조각은 복원하지 않았을 수도 있다) 온통 투명한 프레임을 쓴다. 여러 스레드에서 함께 부른다.
    /// </summary>
    public static void Merge(
        string inpaintRoot, IReadOnlyList<ChunkRange> chunks, string outputDir,
        int regionWidth, int regionHeight, Func<int, byte[]?> alphaAt,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDir);
        int frameCount = chunks[^1].End;

        // 알파가 0인 곳은 색도 0으로 비워 파일을 작게 만든다. 다만 색 정보는 2×2로 묶여 섞이므로(yuv420)
        // 알파 경계 바로 바깥 2픽셀까지는 색을 남겨, 가장자리 색이 검게 물들지 않게 한다.
        var keeps = new ConditionalWeakTable<byte[], byte[]>();
        byte[] Keep(byte[] alpha) => keeps.GetValue(alpha, a =>
        {
            var keep = new byte[a.Length];
            ImageOps.Dilate(a, keep, regionWidth, regionHeight, 2);
            return keep;
        });
        var blank = new Lazy<byte[]>(() =>
        {
            using var stream = new MemoryStream();
            PngCodec.Encode(stream, new byte[regionWidth * regionHeight * 4], regionWidth, regionHeight, 4);
            return stream.ToArray();
        });

        int done = 0;
        var gate = new Lock();
        var options = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        try
        {
            Parallel.For(0, frameCount, options, frame =>
            {
                string target = Path.Combine(outputDir, SequenceFrameName(frame));
                // 완성된 파일만 최종 이름을 가지므로(임시 이름으로 쓴 뒤 바꿈) 이미 있으면 건너뛰어도 안전하다.
                if (!File.Exists(target))
                {
                    var alpha = alphaAt(frame);
                    if (alpha is null)
                    {
                        string temp = target + ".tmp";
                        File.WriteAllBytes(temp, blank.Value);
                        File.Move(temp, target, overwrite: true);
                    }
                    else
                    {
                        if (alpha.Length != regionWidth * regionHeight) throw new ArgumentException("알파 크기가 영역과 다릅니다.", nameof(alphaAt));
                        MergeFrame(inpaintRoot, chunks, frame, regionWidth, regionHeight, alpha, Keep(alpha), target);
                    }
                }
                // 여러 스레드가 동시에 끝내므로, 세는 것과 알리는 것을 한데 묶어야 진행률이 뒤로 가지 않는다.
                if (progress is not null)
                {
                    lock (gate) progress.Report(++done);
                }
            });
        }
        catch (AggregateException ex)
        {
            // 병렬 처리 중에 난 오류는 한 묶음(AggregateException)으로 온다. 부르는 쪽이 다룰 수 있는 오류 하나로 풀어 준다.
            var first = ex.Flatten().InnerExceptions[0];
            if (first is OperationCanceledException or IOException or UnauthorizedAccessException)
                ExceptionDispatchInfo.Capture(first).Throw();
            // 깨진 PNG, 지원하지 않는 형식 등: 복원 도구가 내놓은 프레임이 잘못됐다.
            throw new JobException("복원된 프레임을 합치지 못했습니다: " + first.Message + " 작업 폴더를 지우고 다시 시도하세요.", first);
        }
    }

    private static void MergeFrame(
        string inpaintRoot, IReadOnlyList<ChunkRange> chunks, int frame,
        int regionWidth, int regionHeight, byte[] alpha, byte[] keep, string target)
    {
        var weights = Crossfade.Weights(chunks, frame);
        int pixels = regionWidth * regionHeight;

        byte[]? blended = null;
        float[]? accumulator = null;
        byte[]? resized = null;
        var rgba = ArrayPool<byte>.Shared.Rent(pixels * 4);
        try
        {
            int srcWidth = 0, srcHeight = 0;
            for (int k = 0; k < weights.Count; k++)
            {
                var (chunk, weight) = weights[k];
                string path = Path.Combine(ChunkFramesDir(inpaintRoot, chunk), ChunkFrameName(frame - chunk.Start));
                var file = PngCodec.ReadFilePooled(path, out int fileLength);
                try
                {
                    var png = file.AsSpan(0, fileLength);
                    var header = PngCodec.ReadHeader(png);
                    if (k == 0)
                    {
                        (srcWidth, srcHeight) = (header.Width, header.Height);
                        blended = ArrayPool<byte>.Shared.Rent(srcWidth * srcHeight * 3);
                    }
                    else if (header.Width != srcWidth || header.Height != srcHeight)
                    {
                        throw new InvalidDataException($"조각마다 프레임 크기가 다릅니다: {path}");
                    }

                    int length = srcWidth * srcHeight * 3;
                    if (weights.Count == 1)
                    {
                        PngCodec.Decode(png, blended, 3);
                    }
                    else
                    {
                        // 겹치는 구간: 가중 평균
                        accumulator ??= ArrayPool<float>.Shared.Rent(length);
                        var scratch = ArrayPool<byte>.Shared.Rent(length);
                        try
                        {
                            PngCodec.Decode(png, scratch, 3);
                            if (k == 0)
                                for (int i = 0; i < length; i++) accumulator[i] = scratch[i] * weight;
                            else
                                for (int i = 0; i < length; i++) accumulator[i] += scratch[i] * weight;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(scratch);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(file);
                }
            }

            if (accumulator is not null)
            {
                int length = srcWidth * srcHeight * 3;
                for (int i = 0; i < length; i++) blended![i] = (byte)Math.Clamp((int)(accumulator[i] + 0.5f), 0, 255);
            }

            // ProPainter가 축소해서 처리했다면 영역 크기로 되돌린다.
            ReadOnlySpan<byte> rgb = blended;
            if (srcWidth != regionWidth || srcHeight != regionHeight)
            {
                resized = ArrayPool<byte>.Shared.Rent(pixels * 3);
                ImageOps.ResizeBicubic(blended, srcWidth, srcHeight, resized, regionWidth, regionHeight, 3);
                rgb = resized;
            }

            var output = rgba.AsSpan(0, pixels * 4);
            for (int i = 0; i < pixels; i++)
            {
                if (keep[i] == 0)
                {
                    output.Slice(i * 4, 4).Clear();
                    continue;
                }
                output[i * 4] = rgb[i * 3];
                output[i * 4 + 1] = rgb[i * 3 + 1];
                output[i * 4 + 2] = rgb[i * 3 + 2];
                output[i * 4 + 3] = alpha[i];
            }

            string temp = target + ".tmp";
            PngCodec.Write(temp, output, regionWidth, regionHeight, 4);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgba);
            if (blended is not null) ArrayPool<byte>.Shared.Return(blended);
            if (accumulator is not null) ArrayPool<float>.Shared.Return(accumulator);
            if (resized is not null) ArrayPool<byte>.Shared.Return(resized);
        }
    }
}
