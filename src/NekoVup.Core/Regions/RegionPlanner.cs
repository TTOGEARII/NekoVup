namespace NekoVup.Core.Regions;

public sealed record RegionOptions
{
    /// <summary>워터마크 박스 둘레에 붙이는 여백(px). 복원 모델이 참고할 주변 화면이다.</summary>
    public int Pad { get; init; } = 96;

    /// <summary>처리 영역 면적 상한(px). 넘으면 ProPainter에 축소 비율을 넘긴다.</summary>
    public long MaxArea { get; init; } = 300_000;

    /// <summary>마스크를 만들 때 박스를 바깥으로 키우는 양(px).</summary>
    public int MaskGrow { get; init; } = 2;
}

/// <summary>ProPainter가 한 번에 처리하는 잘라낸 영역 하나.</summary>
public sealed record ProcessRegion
{
    /// <summary>프레임 안에서의 영역 위치·크기. 가로세로는 8의 배수, 좌표는 짝수다.</summary>
    public required PixelRect Rect { get; init; }

    /// <summary>이 영역에 속한 워터마크 박스 (프레임 좌표, 프레임 안으로 잘린 것).</summary>
    public required IReadOnlyList<PixelRect> Boxes { get; init; }

    /// <summary>마스크에 흰색으로 칠할 사각형들 (영역 기준 좌표, <see cref="RegionOptions.MaskGrow"/>만큼 키운 것).</summary>
    public required IReadOnlyList<PixelRect> MaskBoxes { get; init; }

    /// <summary>ProPainter <c>--resize_ratio</c>. 축소가 필요 없으면 1.</summary>
    public double ResizeRatio { get; init; } = 1.0;

    public bool NeedsResize => ResizeRatio < 1.0;
}

/// <summary>워터마크 박스들로부터 처리 영역을 계산한다.</summary>
public static class RegionPlanner
{
    public static IReadOnlyList<ProcessRegion> Plan(IReadOnlyList<PixelRect> boxes, int frameWidth, int frameHeight, RegionOptions? options = null)
    {
        options ??= new RegionOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Pad);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaskGrow);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxArea, 64);
        if (frameWidth < 8 || frameHeight < 8)
            throw new ArgumentException($"프레임이 너무 작습니다: {frameWidth}x{frameHeight}");

        var frame = new PixelRect(0, 0, frameWidth, frameHeight);
        var groups = new List<(PixelRect Rect, List<PixelRect> Boxes)>();
        foreach (var raw in boxes)
        {
            var box = raw.ClampTo(frame);
            if (box.IsEmpty) continue;
            groups.Add((Align(box.Inflate(options.Pad).ClampTo(frame), frameWidth, frameHeight), [box]));
        }
        if (groups.Count == 0)
            throw new ArgumentException("프레임 안에 들어오는 워터마크 박스가 없습니다.");

        // 겹치는 영역은 하나로 합친다. 합치면 커져서 다른 영역과 새로 겹칠 수 있으므로 변화가 없을 때까지 반복.
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < groups.Count && !merged; i++)
            for (int j = i + 1; j < groups.Count && !merged; j++)
            {
                if (!groups[i].Rect.Intersects(groups[j].Rect)) continue;
                var union = Align(groups[i].Rect.Union(groups[j].Rect), frameWidth, frameHeight);
                groups[i].Boxes.AddRange(groups[j].Boxes);
                groups[i] = (union, groups[i].Boxes);
                groups.RemoveAt(j);
                merged = true;
            }
        } while (merged);

        return [.. groups
            .OrderBy(g => g.Rect.Y).ThenBy(g => g.Rect.X)
            .Select(g => new ProcessRegion
            {
                Rect = g.Rect,
                Boxes = g.Boxes,
                MaskBoxes = [.. g.Boxes
                    .Select(b => b.Inflate(options.MaskGrow).ClampTo(g.Rect).Offset(-g.Rect.X, -g.Rect.Y))
                    .Where(b => !b.IsEmpty)],
                ResizeRatio = ResizeRatioFor(g.Rect.Area, options.MaxArea),
            })];
    }

    /// <summary>면적이 상한을 넘으면 sqrt(상한/면적), 아니면 1. 소수 넷째 자리에서 내림한다(올림하면 상한을 넘을 수 있다).</summary>
    public static double ResizeRatioFor(long area, long maxArea)
    {
        if (area <= maxArea) return 1.0;
        return Math.Floor(Math.Sqrt((double)maxArea / area) * 10_000) / 10_000;
    }

    /// <summary>좌표를 짝수로 내리고 크기를 8의 배수로 올린 뒤, 프레임 밖으로 나가면 안쪽으로 민다.</summary>
    internal static PixelRect Align(PixelRect rect, int frameWidth, int frameHeight)
    {
        var (x, w) = AlignAxis(rect.X, rect.Right, frameWidth);
        var (y, h) = AlignAxis(rect.Y, rect.Bottom, frameHeight);
        return new PixelRect(x, y, w, h);
    }

    private static (int Start, int Size) AlignAxis(int start, int end, int limit)
    {
        int s = Math.Max(0, start) & ~1;
        int size = (Math.Min(end, limit) - s + 7) / 8 * 8;
        size = Math.Min(size, limit / 8 * 8);
        if (s + size > limit) s = (limit - size) & ~1;
        return (s, size);
    }
}

/// <summary>빠른 모드(delogo 필터)용 박스 보정.</summary>
public static class DelogoPlanner
{
    /// <summary>delogo는 박스가 프레임 가장자리에 닿으면 실패하므로 사방으로 1px 이상 띄운다.</summary>
    public static IReadOnlyList<PixelRect> Plan(IReadOnlyList<PixelRect> boxes, int frameWidth, int frameHeight)
    {
        var inner = new PixelRect(1, 1, frameWidth - 2, frameHeight - 2);
        var result = boxes.Select(b => b.ClampTo(inner)).Where(b => !b.IsEmpty).ToList();
        if (result.Count == 0)
            throw new ArgumentException("프레임 안에 들어오는 워터마크 박스가 없습니다.");
        return result;
    }
}
