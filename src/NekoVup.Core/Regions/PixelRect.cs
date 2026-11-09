using System.Globalization;

namespace NekoVup.Core.Regions;

/// <summary>픽셀 단위 사각형. 좌상단 (X, Y), 크기 (Width, Height).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => (long)Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public PixelRect Inflate(int amount) =>
        new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>bounds 안으로 잘라낸다. 겹치지 않으면 빈 사각형이 된다.</summary>
    public PixelRect ClampTo(PixelRect bounds)
    {
        int left = Math.Max(X, bounds.X), top = Math.Max(Y, bounds.Y);
        int right = Math.Min(Right, bounds.Right), bottom = Math.Min(Bottom, bounds.Bottom);
        return right <= left || bottom <= top ? default : FromEdges(left, top, right, bottom);
    }

    /// <summary>면적이 겹치는지 (모서리만 맞닿은 것은 겹침이 아니다).</summary>
    public bool Intersects(PixelRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public PixelRect Union(PixelRect other) =>
        FromEdges(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    /// <summary>"x,y,w,h" 형식을 읽는다.</summary>
    public static bool TryParse(string? text, out PixelRect rect)
    {
        rect = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split([',', ':', 'x', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return false;
        Span<int> v = stackalloc int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i])) return false;
        rect = new PixelRect(v[0], v[1], v[2], v[3]);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");
}
