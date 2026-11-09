namespace NekoVup.Core.Regions;

/// <summary>박스의 어느 부분을 잡고 끄는지.</summary>
public enum BoxGrip
{
    None,

    /// <summary>빈 곳에서 끌어 새 박스를 그린다.</summary>
    Draw,

    /// <summary>박스 안을 잡고 옮긴다.</summary>
    Move,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// 미리보기에서 박스를 그리고·옮기고·크기를 바꿀 때의 좌표 계산. 모두 영상 픽셀 좌표다.
/// 화면(UI)과 무관하게 두어 단위 테스트로 검증한다.
/// </summary>
public static class BoxEditing
{
    /// <summary>박스가 이보다 작아지지 않게 한다 (영상 픽셀).</summary>
    public const int MinSize = 4;

    /// <summary>화면 좌표를 영상 좌표로 (화면에 그려진 크기 → 영상 픽셀 크기, 가로·세로 따로).</summary>
    public static (double X, double Y) ToVideo(double screenX, double screenY, double shownWidth, double shownHeight, int videoWidth, int videoHeight) =>
        (screenX * videoWidth / shownWidth, screenY * videoHeight / shownHeight);

    /// <summary>두 모서리로 사각형을 만든다: 뒤집혀도 바로잡고, 프레임 안으로 자르고, 최소 크기를 지킨다.</summary>
    public static PixelRect FromCorners(double x1, double y1, double x2, double y2, int frameWidth, int frameHeight)
    {
        int left = (int)Math.Round(Math.Clamp(Math.Min(x1, x2), 0, Math.Max(0, frameWidth - MinSize)));
        int top = (int)Math.Round(Math.Clamp(Math.Min(y1, y2), 0, Math.Max(0, frameHeight - MinSize)));
        int right = (int)Math.Round(Math.Clamp(Math.Max(x1, x2), left + MinSize, Math.Max(left + MinSize, frameWidth)));
        int bottom = (int)Math.Round(Math.Clamp(Math.Max(y1, y2), top + MinSize, Math.Max(top + MinSize, frameHeight)));
        return PixelRect.FromEdges(left, top, right, bottom);
    }

    /// <summary>
    /// 끌기 한 번의 결과. <paramref name="start"/>는 누른 순간의 박스, (dx, dy)는 누른 지점에서 지금까지 움직인 양이다.
    /// 옮길 때는 크기를 유지한 채 프레임 안에서만 움직이고, 크기를 바꿀 때는 잡은 변만 따라간다.
    /// </summary>
    public static PixelRect Drag(PixelRect start, BoxGrip grip, double dx, double dy, int frameWidth, int frameHeight)
    {
        double left = start.X, top = start.Y, right = start.Right, bottom = start.Bottom;
        switch (grip)
        {
            case BoxGrip.Move:
                double moveX = Math.Clamp(dx, -left, Math.Max(-left, frameWidth - right));
                double moveY = Math.Clamp(dy, -top, Math.Max(-top, frameHeight - bottom));
                return new PixelRect((int)Math.Round(left + moveX), (int)Math.Round(top + moveY), start.Width, start.Height);
            case BoxGrip.Left or BoxGrip.TopLeft or BoxGrip.BottomLeft:
                left += dx;
                break;
            case BoxGrip.Right or BoxGrip.TopRight or BoxGrip.BottomRight:
                right += dx;
                break;
        }
        switch (grip)
        {
            case BoxGrip.Top or BoxGrip.TopLeft or BoxGrip.TopRight:
                top += dy;
                break;
            case BoxGrip.Bottom or BoxGrip.BottomLeft or BoxGrip.BottomRight:
                bottom += dy;
                break;
        }
        return grip is BoxGrip.None or BoxGrip.Draw ? start : FromCorners(left, top, right, bottom, frameWidth, frameHeight);
    }

    /// <summary>손잡이 여덟 개의 위치 (영상 좌표).</summary>
    public static IReadOnlyList<(BoxGrip Grip, double X, double Y)> Handles(PixelRect rect)
    {
        double midX = rect.X + rect.Width / 2.0, midY = rect.Y + rect.Height / 2.0;
        return
        [
            (BoxGrip.TopLeft, rect.X, rect.Y), (BoxGrip.Top, midX, rect.Y), (BoxGrip.TopRight, rect.Right, rect.Y),
            (BoxGrip.Right, rect.Right, midY), (BoxGrip.BottomRight, rect.Right, rect.Bottom), (BoxGrip.Bottom, midX, rect.Bottom),
            (BoxGrip.BottomLeft, rect.X, rect.Bottom), (BoxGrip.Left, rect.X, midY),
        ];
    }

    /// <summary>
    /// 그 지점에서 누르면 무엇을 하게 되는지. 선택된 박스의 손잡이가 먼저, 그다음 박스 안(위에 그려진 것부터), 아니면 새로 그리기.
    /// </summary>
    /// <param name="reachX">손잡이를 잡았다고 보는 거리 (영상 픽셀, 가로).</param>
    /// <param name="reachY">세로.</param>
    /// <returns>잡은 부분과 대상 박스 번호 (새로 그리기면 -1).</returns>
    public static (BoxGrip Grip, int Index) HitTest(IReadOnlyList<PixelRect> boxes, int selectedIndex, double x, double y, double reachX, double reachY)
    {
        if (selectedIndex >= 0 && selectedIndex < boxes.Count)
        {
            foreach (var (grip, hx, hy) in Handles(boxes[selectedIndex]))
                if (Math.Abs(x - hx) <= reachX && Math.Abs(y - hy) <= reachY)
                    return (grip, selectedIndex);
        }
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            var r = boxes[i];
            if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return (BoxGrip.Move, i);
        }
        return (BoxGrip.Draw, -1);
    }
}
