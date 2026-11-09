using NekoVup.Core.Regions;

namespace NekoVup.Core.Imaging;

/// <summary>처리 영역 하나에 대한 마스크와 합성용 알파를 만든다. 카메라가 고정이라고 보고 모든 프레임에 같은 것을 쓴다.</summary>
public static class MaskBuilder
{
    /// <summary>바깥 띠(부드럽게 섞이는 구간)를 안쪽 띠보다 얼마나 더 넓힐지(px).</summary>
    public const int FeatherExtra = 6;

    public const double FeatherSigma = 3.0;

    /// <summary>ProPainter에 넘길 마스크 (영역 크기, 워터마크 부분 255, 나머지 0).</summary>
    public static byte[] BuildMask(ProcessRegion region)
    {
        int w = region.Rect.Width, h = region.Rect.Height;
        var mask = new byte[w * h];
        foreach (var box in region.MaskBoxes) ImageOps.FillRect(mask, w, h, box, 255);
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
    public static byte[] BuildAlpha(ProcessRegion region, int maskDilation, double resizeRatio = 1.0)
    {
        int w = region.Rect.Width, h = region.Rect.Height;
        var mask = BuildMask(region);

        int inner = InnerRadius(maskDilation, resizeRatio);
        var solid = new byte[w * h];
        ImageOps.Dilate(mask, solid, w, h, inner);

        var wide = new byte[w * h];
        ImageOps.Dilate(mask, wide, w, h, inner + FeatherExtra);
        var soft = new byte[w * h];
        ImageOps.GaussianBlur(wide, soft, w, h, FeatherSigma);

        for (int i = 0; i < solid.Length; i++)
            if (soft[i] > solid[i]) solid[i] = soft[i];
        return solid;
    }

    public static int InnerRadius(int maskDilation, double resizeRatio) =>
        resizeRatio is > 0 and < 1 ? (int)Math.Ceiling(maskDilation / resizeRatio) : maskDilation;

    /// <summary>알파가 0이 아닌 곳이 박스에서 최대 몇 픽셀까지 퍼지는지 (검증·표시용).</summary>
    public static int AlphaReach(int maskGrow, int maskDilation, double resizeRatio = 1.0) =>
        maskGrow + InnerRadius(maskDilation, resizeRatio) + FeatherExtra + (int)Math.Ceiling(FeatherSigma * 3);
}
