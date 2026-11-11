using NekoVup.Core.Regions;

namespace NekoVup.Core.Imaging;

/// <summary>처리 영역 하나에 대한 마스크와 합성용 알파를 만든다. 카메라가 고정이라고 보고 모든 프레임에 같은 것을 쓴다.</summary>
public static class MaskBuilder
{
    /// <summary>바깥 띠(부드럽게 섞이는 구간)를 안쪽 띠보다 얼마나 더 넓힐지(px).</summary>
    public const int FeatherExtra = 6;

    public const double FeatherSigma = 3.0;

    /// <summary>ProPainter에 넘길 마스크 (영역 크기, 워터마크 부분 255, 나머지 0).</summary>
    public static byte[] BuildMask(ProcessRegion region) => BuildMask(region.Rect.Width, region.Rect.Height, region.MaskBoxes);

    /// <summary>움직이는 워터마크는 프레임마다 칠할 사각형이 다르다 (<see cref="MotionPlan.MaskBoxes"/>).</summary>
    public static byte[] BuildMask(int width, int height, IReadOnlyList<PixelRect> maskBoxes)
    {
        var mask = new byte[width * height];
        foreach (var box in maskBoxes) ImageOps.FillRect(mask, width, height, box, 255);
        return mask;
    }

    /// <summary>
    /// 복원 결과를 원본 위에 얹을 때 쓰는 알파.
    /// 안쪽(마스크를 ProPainter와 같은 양만큼 키운 곳)은 255로 복원 결과를 그대로 쓰고,
    /// 그 바깥 몇 픽셀은 가우시안으로 부드럽게 0까지 떨어뜨려 경계선이 보이지 않게 한다.
    /// 알파가 0인 곳은 원본 픽셀이 그대로 남으므로, 영상 → RGB → 영상 변환에서 생기는 색 오차가 마스크 밖으로 번지지 않는다.
    /// </summary>
    /// <param name="maskDilation">ProPainter <c>--mask_dilation</c> 값.</param>
    /// <param name="resizeRatio">ProPainter가 축소해서 처리했다면 그 비율. 축소본에서 n픽셀 키운 것은 원래 크기로 n/비율 픽셀이다.</param>
    public static byte[] BuildAlpha(ProcessRegion region, int maskDilation, double resizeRatio = 1.0) =>
        BuildAlpha(region.Rect.Width, region.Rect.Height, region.MaskBoxes, maskDilation, resizeRatio);

    /// <summary>
    /// 사각형들로 바로 만드는 알파. 알파는 사각형에서 몇 픽셀까지만 퍼지므로 그 둘레만 계산해 옮겨 적는다 —
    /// 움직이는 워터마크는 프레임마다 다시 만들어야 해서, 넓은 영역 전체를 훑으면 느리다. 결과는 전체를 계산한 것과 같다.
    /// </summary>
    public static byte[] BuildAlpha(int width, int height, IReadOnlyList<PixelRect> maskBoxes, int maskDilation, double resizeRatio = 1.0)
    {
        var alpha = new byte[width * height];
        var bounds = new PixelRect(0, 0, width, height);
        PixelRect window = default;
        foreach (var box in maskBoxes)
        {
            var inside = box.ClampTo(bounds);
            if (!inside.IsEmpty) window = window.IsEmpty ? inside : window.Union(inside);
        }
        if (window.IsEmpty) return alpha;

        int inner = InnerRadius(maskDilation, resizeRatio);
        // 흐림이 닿는 거리보다 넉넉히 잡아야 둘레의 가장자리 값(0)이 바깥의 실제 값(0)과 같다.
        window = window.Inflate(inner + FeatherExtra + (int)Math.Ceiling(FeatherSigma * 3) + 2).ClampTo(bounds);
        int w = window.Width, h = window.Height;
        var mask = new byte[w * h];
        foreach (var box in maskBoxes) ImageOps.FillRect(mask, w, h, box.Offset(-window.X, -window.Y), 255);

        var solid = new byte[w * h];
        ImageOps.Dilate(mask, solid, w, h, inner);

        var wide = new byte[w * h];
        ImageOps.Dilate(mask, wide, w, h, inner + FeatherExtra);
        var soft = new byte[w * h];
        ImageOps.GaussianBlur(wide, soft, w, h, FeatherSigma);

        for (int i = 0; i < solid.Length; i++)
            if (soft[i] > solid[i]) solid[i] = soft[i];
        for (int y = 0; y < h; y++)
            solid.AsSpan(y * w, w).CopyTo(alpha.AsSpan((y + window.Y) * width + window.X, w));
        return alpha;
    }

    public static int InnerRadius(int maskDilation, double resizeRatio) =>
        resizeRatio is > 0 and < 1 ? (int)Math.Ceiling(maskDilation / resizeRatio) : maskDilation;

    /// <summary>알파가 0이 아닌 곳이 박스에서 최대 몇 픽셀까지 퍼지는지 (검증·표시용).</summary>
    public static int AlphaReach(int maskGrow, int maskDilation, double resizeRatio = 1.0) =>
        maskGrow + InnerRadius(maskDilation, resizeRatio) + FeatherExtra + (int)Math.Ceiling(FeatherSigma * 3);
}

/// <summary>
/// 움직이는 워터마크의 프레임별 알파. 워터마크가 한자리에 머무는 동안은 같은 알파를 돌려쓴다.
/// 여러 스레드에서 함께 불러도 된다.
/// </summary>
public sealed class MovingAlpha(int width, int height, int maskDilation, double resizeRatio)
{
    private const int Capacity = 16;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _cache = new();

    /// <summary>그 사각형들에 대한 알파. 칠할 것이 없으면 null.</summary>
    public byte[]? For(IReadOnlyList<PixelRect> maskBoxes)
    {
        if (maskBoxes.Count == 0) return null;
        string key = string.Join('|', maskBoxes);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        // 미끄러져 가는 워터마크는 프레임마다 자리가 달라 쌓이기만 한다. 넘치면 비운다.
        if (_cache.Count >= Capacity) _cache.Clear();
        return _cache.GetOrAdd(key, _ => MaskBuilder.BuildAlpha(width, height, maskBoxes, maskDilation, resizeRatio));
    }
}
