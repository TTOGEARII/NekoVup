using System.Buffers;
using NekoVup.Core.Regions;

namespace NekoVup.Core.Imaging;

/// <summary>마스크·알파 생성과 조각 병합에 필요한 최소한의 이미지 연산. 모두 바이트 배열(줄 순서, 채널 섞어 저장) 위에서 동작한다.</summary>
public static class ImageOps
{
    public static void FillRect(Span<byte> gray, int width, int height, PixelRect rect, byte value)
    {
        var r = rect.ClampTo(new PixelRect(0, 0, width, height));
        for (int y = r.Y; y < r.Bottom; y++)
            gray.Slice(y * width + r.X, r.Width).Fill(value);
    }

    /// <summary>정사각형 (2·radius+1) 커널 팽창. 가로·세로로 나눠 최댓값을 구한다.</summary>
    public static void Dilate(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            src[..(width * height)].CopyTo(dst);
            return;
        }

        var temp = ArrayPool<byte>.Shared.Rent(width * height);
        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = src.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    int from = Math.Max(0, x - radius), to = Math.Min(width - 1, x + radius);
                    byte max = 0;
                    for (int k = from; k <= to; k++) if (row[k] > max) max = row[k];
                    temp[y * width + x] = max;
                }
            }
            for (int y = 0; y < height; y++)
            {
                int from = Math.Max(0, y - radius), to = Math.Min(height - 1, y + radius);
                for (int x = 0; x < width; x++)
                {
                    byte max = 0;
                    for (int k = from; k <= to; k++)
                    {
                        byte v = temp[k * width + x];
                        if (v > max) max = v;
                    }
                    dst[y * width + x] = max;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    /// <summary>가우시안 흐림. 커널 반경은 3σ, 가장자리는 끝 픽셀을 늘려 쓴다.</summary>
    public static void GaussianBlur(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, double sigma)
    {
        if (sigma <= 0)
        {
            src[..(width * height)].CopyTo(dst);
            return;
        }

        int radius = (int)Math.Ceiling(sigma * 3);
        Span<float> kernel = stackalloc float[radius * 2 + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++) sum += kernel[i + radius] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var temp = ArrayPool<float>.Shared.Rent(width * height);
        try
        {
            for (int y = 0; y < height; y++)
            {
                var row = src.Slice(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    float acc = 0;
                    for (int k = -radius; k <= radius; k++)
                        acc += row[Math.Clamp(x + k, 0, width - 1)] * kernel[k + radius];
                    temp[y * width + x] = acc;
                }
            }
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float acc = 0;
                for (int k = -radius; k <= radius; k++)
                    acc += temp[Math.Clamp(y + k, 0, height - 1) * width + x] * kernel[k + radius];
                dst[y * width + x] = (byte)Math.Clamp((int)(acc + 0.5f), 0, 255);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(temp);
        }
    }

    /// <summary>
    /// 바이큐빅(Catmull-Rom) 크기 변경. ProPainter가 축소해서 내놓은 프레임을 원래 영역 크기로 되돌리는 데 쓴다(확대 전용, 축소 시 앨리어싱 방지 없음).
    /// </summary>
    public static void ResizeBicubic(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, Span<byte> dst, int dstWidth, int dstHeight, int channels)
    {
        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            src[..(srcWidth * srcHeight * channels)].CopyTo(dst);
            return;
        }

        var xIndex = ArrayPool<int>.Shared.Rent(dstWidth * 4);
        var xWeight = ArrayPool<float>.Shared.Rent(dstWidth * 4);
        var yIndex = ArrayPool<int>.Shared.Rent(dstHeight * 4);
        var yWeight = ArrayPool<float>.Shared.Rent(dstHeight * 4);
        var temp = ArrayPool<float>.Shared.Rent(dstWidth * srcHeight * channels);
        try
        {
            BuildTaps(srcWidth, dstWidth, xIndex, xWeight);
            BuildTaps(srcHeight, dstHeight, yIndex, yWeight);

            // 가로 방향
            for (int y = 0; y < srcHeight; y++)
            {
                var row = src.Slice(y * srcWidth * channels, srcWidth * channels);
                var outRow = temp.AsSpan(y * dstWidth * channels, dstWidth * channels);
                for (int x = 0; x < dstWidth; x++)
                for (int c = 0; c < channels; c++)
                {
                    float acc = 0;
                    for (int t = 0; t < 4; t++) acc += row[xIndex[x * 4 + t] * channels + c] * xWeight[x * 4 + t];
                    outRow[x * channels + c] = acc;
                }
            }

            // 세로 방향
            int stride = dstWidth * channels;
            for (int y = 0; y < dstHeight; y++)
            {
                var outRow = dst.Slice(y * stride, stride);
                var r0 = temp.AsSpan(yIndex[y * 4] * stride, stride);
                var r1 = temp.AsSpan(yIndex[y * 4 + 1] * stride, stride);
                var r2 = temp.AsSpan(yIndex[y * 4 + 2] * stride, stride);
                var r3 = temp.AsSpan(yIndex[y * 4 + 3] * stride, stride);
                float w0 = yWeight[y * 4], w1 = yWeight[y * 4 + 1], w2 = yWeight[y * 4 + 2], w3 = yWeight[y * 4 + 3];
                for (int i = 0; i < stride; i++)
                {
                    float v = r0[i] * w0 + r1[i] * w1 + r2[i] * w2 + r3[i] * w3;
                    outRow[i] = (byte)Math.Clamp((int)(v + 0.5f), 0, 255);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(xIndex);
            ArrayPool<float>.Shared.Return(xWeight);
            ArrayPool<int>.Shared.Return(yIndex);
            ArrayPool<float>.Shared.Return(yWeight);
            ArrayPool<float>.Shared.Return(temp);
        }
    }

    private static void BuildTaps(int srcSize, int dstSize, int[] index, float[] weight)
    {
        double scale = (double)srcSize / dstSize;
        for (int i = 0; i < dstSize; i++)
        {
            double center = (i + 0.5) * scale - 0.5;
            int first = (int)Math.Floor(center) - 1;
            double t = center - Math.Floor(center);
            for (int k = 0; k < 4; k++)
            {
                index[i * 4 + k] = Math.Clamp(first + k, 0, srcSize - 1);
                weight[i * 4 + k] = (float)CatmullRom(k - 1 - t);
            }
        }
    }

    private static double CatmullRom(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return 1.5 * x * x * x - 2.5 * x * x + 1;
        if (x < 2) return -0.5 * x * x * x + 2.5 * x * x - 4 * x + 2;
        return 0;
    }
}
