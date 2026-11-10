namespace NekoVup.Core.Regions;

/// <summary>타임라인의 처리 구간에서 잡은 손잡이.</summary>
public enum RangeGrip
{
    None,
    Start,
    End,
}

/// <summary>
/// 타임라인에서 처리 구간의 손잡이를 잡고 끄는 계산 (위치는 화면의 가로 픽셀). 화면과 떼어 두어 테스트로 확인한다.
/// 손잡이는 구간 안쪽으로 꺾인 대괄호([ ])라 안쪽으로는 그 끝까지, 바깥쪽으로는 조금만 잡아 준다 —
/// 넓게 잡으면 손잡이 옆을 눌러 재생 헤드를 옮기려던 것이 구간을 바꾼다.
/// </summary>
public static class RangeEditing
{
    /// <summary>손잡이 바깥쪽·안쪽으로 잡아 주는 거리(픽셀).</summary>
    public const double OuterReach = 3, InnerReach = 8;

    /// <summary>
    /// 손잡이를 누른 뒤 이만큼(픽셀) 움직여야 끄는 것으로 본다. 그 전에 떼면 그 자리로 옮기는 클릭이다 —
    /// 누르기만 해도 경계가 누른 자리로 튀면, 손잡이 위에 있던 재생 헤드를 옮기려다 구간이 바뀐다.
    /// </summary>
    public const double DragThreshold = 3;

    /// <summary>가로 위치 <paramref name="x"/>를 누르면 잡히는 손잡이. <paramref name="startX"/>·<paramref name="endX"/>는 구간 시작·끝의 화면 위치.</summary>
    public static RangeGrip HitTest(double x, double startX, double endX)
    {
        bool onStart = x >= startX - OuterReach && x <= startX + InnerReach;
        bool onEnd = x >= endX - InnerReach && x <= endX + OuterReach;
        // 구간이 아주 짧아 두 손잡이가 겹치면 누른 자리에서 가까운 쪽
        if (onStart && onEnd) return Math.Abs(x - startX) <= Math.Abs(endX - x) ? RangeGrip.Start : RangeGrip.End;
        return onStart ? RangeGrip.Start : onEnd ? RangeGrip.End : RangeGrip.None;
    }

    /// <summary>
    /// 잡은 손잡이를 <paramref name="seconds"/>로 옮긴 구간. 시작은 끝보다 <paramref name="minRange"/> 앞에서,
    /// 끝은 시작보다 <paramref name="minRange"/> 뒤에서 멈추고, 영상(0 ~ <paramref name="duration"/>) 밖으로 나가지 않는다.
    /// </summary>
    public static (double Start, double End) Move(RangeGrip grip, double seconds, double start, double end, double duration, double minRange) => grip switch
    {
        RangeGrip.Start => (Math.Clamp(seconds, 0, Math.Max(0, end - minRange)), end),
        RangeGrip.End => (start, Math.Clamp(seconds, Math.Min(duration, start + minRange), Math.Max(duration, 0))),
        _ => (start, end),
    };
}
