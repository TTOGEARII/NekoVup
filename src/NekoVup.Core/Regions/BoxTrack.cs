using System.Globalization;
using NekoVup.Core.Media;
using NekoVup.Core.Pipeline;

namespace NekoVup.Core.Regions;

/// <summary>움직이는 워터마크의 한 순간: 이 시각(초, 원본 기준)에 박스가 여기 있다.</summary>
public readonly record struct BoxKey(double Seconds, PixelRect Rect);

/// <summary>
/// 움직이는 워터마크 박스 하나. 위치를 적어 둔 시각들 사이는 직선으로 이어 간다
/// (<see cref="Jump"/>면 다음 시각까지 제자리에 있다가 건너뛴다). 첫 시각 앞과 마지막 시각 뒤는 그 자리에 머문다.
/// </summary>
public sealed record BoxTrack
{
    public required IReadOnlyList<BoxKey> Keys { get; init; }

    /// <summary>자리를 옮길 때 미끄러지지 않고 한 번에 건너뛰는 워터마크 (몇 초마다 다른 구석에 나타나는 것).</summary>
    public bool Jump { get; init; }

    /// <summary>자리를 적을 수 있는 가장 늦은 시각 (100일). 터무니없는 시각이 프레임 번호를 넘치게 하지 않도록 둔 한계다.</summary>
    public const double MaxSeconds = 8_640_000;

    /// <summary>"시각=x,y,w,h;시각=x,y,w,h…" 형식을 읽는다. 시각은 분:초("1:23.5")나 초("83.5").</summary>
    public static bool TryParse(string? text, bool jump, out BoxTrack track)
    {
        track = new BoxTrack { Keys = [] };
        if (string.IsNullOrWhiteSpace(text)) return false;
        var keys = new List<BoxKey>();
        foreach (string part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = part.IndexOf('=');
            if (at <= 0) return false;
            if (!TimeText.TryParse(part[..at], out double seconds) || seconds > MaxSeconds) return false;
            if (!PixelRect.TryParse(part[(at + 1)..], out var rect) || rect.IsEmpty) return false;
            keys.Add(new BoxKey(seconds, rect));
        }
        if (keys.Count == 0) return false;
        track = new BoxTrack { Keys = keys, Jump = jump };
        return true;
    }

    public override string ToString() =>
        (Jump ? "jump:" : "move:") + string.Join(';', Keys.Select(k => string.Create(CultureInfo.InvariantCulture, $"{k.Seconds:R}={k.Rect}")));
}

/// <summary>
/// 박스 하나가 프레임마다 어디 있는지 (프레임 번호는 원본 기준). 시각으로 적은 <see cref="BoxTrack"/>을 프레임 번호로 옮긴 것이고,
/// 움직이지 않는 박스도 자리가 하나뿐인 경로로 다룬다.
/// </summary>
public sealed class BoxPath
{
    private readonly long[] _frames;
    private readonly PixelRect[] _rects;

    private BoxPath(long[] frames, PixelRect[] rects, bool jump, PixelRect? bounds = null)
    {
        (_frames, _rects) = (frames, rects);
        Jump = jump;
        Bounds = bounds;
        Moves = rects.Any(r => r != rects[0]);
    }

    public bool Jump { get; }

    /// <summary>
    /// 프레임마다 자리를 이 안으로 자른다 (<see cref="ClampTo"/>가 정한다). 이때 <see cref="Rects"/>는 자르지 않은 자리다 —
    /// 화면 밖에서 들어오는 워터마크는 밖에 있는 자리부터 이어 가야 중간 자리가 맞는다.
    /// </summary>
    public PixelRect? Bounds { get; }

    /// <summary>자리가 한 번이라도 바뀌는지.</summary>
    public bool Moves { get; }

    /// <summary>위치를 적어 둔 프레임들 (오름차순)과 그때의 자리.</summary>
    public IReadOnlyList<long> Frames => _frames;

    public IReadOnlyList<PixelRect> Rects => _rects;

    public static BoxPath Fixed(PixelRect rect) => new([0], [rect], jump: false);

    /// <summary>경로에 쓸 수 있는 자리인지: 시각이 0부터 <see cref="BoxTrack.MaxSeconds"/>까지고 자리가 비지 않았다.</summary>
    public static bool IsUsable(BoxKey key) => key.Seconds >= 0 && key.Seconds <= BoxTrack.MaxSeconds && !key.Rect.IsEmpty;

    /// <summary>시각을 그때 화면에 나오는 프레임으로 옮긴다. 같은 프레임에 둘이 적혀 있으면 뒤에 적은 것을 쓴다.</summary>
    public static BoxPath From(BoxTrack track, double fps)
    {
        var byFrame = new SortedDictionary<long, PixelRect>();
        foreach (var key in track.Keys)
        {
            if (!IsUsable(key)) continue;
            byFrame[FrameGrid.FrameAt(key.Seconds, fps)] = key.Rect;
        }
        if (byFrame.Count == 0) throw new ArgumentException("움직이는 박스에 적어 둔 위치가 없습니다.");
        return new BoxPath([.. byFrame.Keys], [.. byFrame.Values], track.Jump);
    }

    /// <summary>프레임 번호로 적어 둔 위치에서 (화면에서 장면마다 맞춰 둔 것). 빈 자리는 뺀다.</summary>
    public static BoxPath FromFrames(IEnumerable<KeyValuePair<long, PixelRect>> keys, bool jump)
    {
        var byFrame = new SortedDictionary<long, PixelRect>();
        foreach (var (frame, rect) in keys)
            if (frame >= 0 && !rect.IsEmpty) byFrame[frame] = rect;
        if (byFrame.Count == 0) throw new ArgumentException("움직이는 박스에 적어 둔 위치가 없습니다.");
        return new BoxPath([.. byFrame.Keys], [.. byFrame.Values], jump);
    }

    /// <summary>그 프레임에서의 자리. 직선으로 가는 구간은 네 변을 따로 이어 가며 반올림한다.</summary>
    public PixelRect At(long frame)
    {
        var rect = Raw(frame);
        return Bounds is { } bounds ? rect.ClampTo(bounds) : rect;
    }

    private PixelRect Raw(long frame)
    {
        int next = Array.BinarySearch(_frames, frame);
        next = next >= 0 ? next + 1 : ~next;   // frame보다 뒤에 있는 첫 위치
        if (next == 0) return _rects[0];
        var a = _rects[next - 1];
        if (next == _frames.Length || Jump) return a;
        var b = _rects[next];
        if (a == b) return a;
        double u = (double)(frame - _frames[next - 1]) / (_frames[next] - _frames[next - 1]);
        return PixelRect.FromEdges(Mix(a.X, b.X, u), Mix(a.Y, b.Y, u), Mix(a.Right, b.Right, u), Mix(a.Bottom, b.Bottom, u));
    }

    private static int Mix(int a, int b, double u) => (int)Math.Floor(a + (b - a) * u + 0.5);

    /// <summary>
    /// 박스가 지나가는 자리들 (처리 영역을 잡는 데 쓴다). 머무는 자리는 그 사각형, 직선으로 가는 구간은 두 끝을 함께 덮는 사각형 —
    /// 그 사이의 자리는 모두 그 안에 든다. 사이에 프레임이 없는 구간은 두 끝을 따로 둔다.
    /// </summary>
    public IReadOnlyList<PixelRect> Footprints()
    {
        var result = new List<PixelRect>();
        if (Jump || _rects.Length == 1)
        {
            foreach (var rect in _rects) Add(rect);
        }
        else
        {
            for (int i = 0; i + 1 < _rects.Length; i++)
            {
                // 바로 다음 프레임에 적어 둔 자리와는 사이가 없다 — 함께 덮으면 밖으로 나갔다가 다른 데로 들어오는 박스가 그 사이를 다 차지한다.
                if (_frames[i + 1] - _frames[i] > 1) Add(_rects[i].Union(_rects[i + 1]));
                else { Add(_rects[i]); Add(_rects[i + 1]); }
            }
        }
        return result;

        void Add(PixelRect rect)
        {
            if (Bounds is { } bounds) rect = rect.ClampTo(bounds);
            if (!result.Contains(rect)) result.Add(rect);
        }
    }

    /// <summary>
    /// 프레임마다 <paramref name="bounds"/> 안에 들어오는 경로로 바꾼다. 한 번도 걸치지 않으면 null.
    /// 머무는 자리(고정·건너뛰는 박스)는 통째로 밖에 있는 자리를 빼고 나머지를 잘라 두면 된다. 직선으로 가는 박스는 자른 자리끼리
    /// 이으면 실제와 달라지므로 (걸쳐 있던 쪽이 덜 가려진다) 자리는 그대로 두고 프레임마다 자른다. 밖에 적어 둔 자리는 가는 방향을
    /// 정하므로 버리지 않고, 그 길에서 <paramref name="bounds"/>에 걸쳐 있는 첫·마지막 프레임의 자리로 바꿔 둔다 — 남긴 자리는
    /// 모두 걸쳐 있어서 그 사이의 자리도 걸쳐 있다 (자른 것이 비지 않는다). 밖에 나가 있는 동안은 나간 자리에 머문다.
    /// </summary>
    public BoxPath? ClampTo(PixelRect bounds)
    {
        var frames = new List<long>(_frames.Length);
        var rects = new List<PixelRect>(_rects.Length);
        bool slides = !Jump && _rects.Length > 1;
        for (int i = 0; i < _rects.Length; i++)
        {
            Keep(_frames[i]);
            if (!slides || i + 1 == _rects.Length || Visible(i, bounds) is not (long first, long last)) continue;
            // 들어오는 자리까지 화면을 가로질러 가지 않게, 들어오기 바로 앞 프레임까지는 나간 자리에 둔다.
            if (first > _frames[i] && frames.Count > 0 && frames[^1] < first - 1)
            {
                frames.Add(first - 1);
                rects.Add(rects[^1]);
            }
            Keep(first);
            Keep(last);
        }
        if (frames.Count == 0) return null;
        var cut = rects.ConvertAll(r => r.ClampTo(bounds));
        bool perFrame = slides && frames.Count > 1 && !cut.SequenceEqual(rects);
        return perFrame ? new BoxPath([.. frames], [.. rects], Jump, bounds) : new BoxPath([.. frames], [.. cut], Jump);

        void Keep(long frame)
        {
            var rect = Raw(frame);
            if (rect.ClampTo(bounds).IsEmpty || (frames.Count > 0 && frames[^1] == frame)) return;
            frames.Add(frame);
            rects.Add(rect);
        }
    }

    /// <summary>
    /// 프레임마다 자르는 경로를, 자른 자리만 적어 둔 경로로 바꾼다 (화면의 숫자 칸은 밖의 자리를 담지 못한다). 자른 자리끼리 그냥
    /// 이으면 걸쳐 있던 쪽이 덜 가려지므로, 변이 <see cref="Bounds"/>의 경계를 넘는 프레임의 앞뒤에도 자리를 적어 둔다 —
    /// 그 사이에서는 자른 변도 직선으로 간다.
    /// </summary>
    public BoxPath Flatten()
    {
        if (Bounds is not { } bounds) return this;
        var frames = new SortedSet<long>(_frames);
        for (int i = 0; i + 1 < _frames.Length; i++)
        {
            long from = _frames[i], to = _frames[i + 1];
            var (a, b) = (_rects[i], _rects[i + 1]);
            Cross(a.X, b.X, bounds.X);
            Cross(a.Right, b.Right, bounds.Right);
            Cross(a.Y, b.Y, bounds.Y);
            Cross(a.Bottom, b.Bottom, bounds.Bottom);

            // start에서 end로 가는 변이 limit을 넘는 프레임의 앞뒤
            void Cross(long start, long end, long limit)
            {
                if (start == limit || end == limit || start < limit == end < limit) return;
                double at = from + (double)(limit - start) / (end - start) * (to - from);
                frames.Add((long)Math.Floor(at));
                frames.Add((long)Math.Ceiling(at));
            }
        }
        return FromFrames(frames.Select(frame => KeyValuePair.Create(frame, At(frame))), Jump);
    }

    /// <summary>
    /// i번째 자리에서 다음 자리로 직선으로 가는 동안 <paramref name="bounds"/>에 걸쳐 있는 프레임들의 처음과 끝. 네 변이 저마다
    /// 한쪽으로만 가므로 걸쳐 있는 프레임은 한 덩어리다. 두 끝이 다 걸쳐 있거나(따로 둘 자리가 없다) 한 번도 걸치지 않으면 null.
    /// </summary>
    private (long First, long Last)? Visible(int i, PixelRect bounds)
    {
        long from = _frames[i], to = _frames[i + 1];
        bool start = Shows(from), end = Shows(to);
        if (start && end) return null;
        long inside = start ? from : end ? to : Crossing(_rects[i], _rects[i + 1], bounds, from, to);
        if (!Shows(inside)) return null;
        return (start ? from : Edge(from, inside), end ? to : Edge(to, inside));

        bool Shows(long frame) => !Raw(frame).ClampTo(bounds).IsEmpty;

        // 걸치지 않은 hidden에서 걸친 shown 쪽으로 가며 처음 걸치는 프레임
        long Edge(long hidden, long shown)
        {
            while (Math.Abs(shown - hidden) > 1)
            {
                long middle = hidden + (shown - hidden) / 2;
                if (Shows(middle)) shown = middle; else hidden = middle;
            }
            return shown;
        }
    }

    /// <summary>두 끝이 다 밖에 있는 길이 <paramref name="bounds"/>를 가로지르면 그 한가운데 프레임 (아니면 from).</summary>
    private static long Crossing(PixelRect a, PixelRect b, PixelRect bounds, long from, long to)
    {
        double low = 0, high = 1;
        Below(a.X, b.X, bounds.Right);
        Below(-a.Right, -b.Right, -bounds.X);
        Below(a.Y, b.Y, bounds.Bottom);
        Below(-a.Bottom, -b.Bottom, -bounds.Y);
        return low < high ? from + (long)Math.Round((low + high) / 2 * (to - from)) : from;

        // start에서 end로 가는 값이 limit보다 작은 동안으로 좁힌다
        void Below(double start, double end, double limit)
        {
            double step = end - start;
            if (step == 0) { if (start >= limit) high = -1; }
            else if (step > 0) high = Math.Min(high, (limit - start) / step);
            else low = Math.Max(low, (limit - start) / step);
        }
    }
}

/// <summary>
/// 박스들의 움직임과 그것을 덮는 처리 영역. 영역은 영상 내내 같은 자리에 두고(박스가 지나가는 자리를 모두 덮는다),
/// 그 안에서 마스크만 프레임마다 옮긴다 — 워터마크가 비켜 간 프레임에서는 가려졌던 화면이 보이므로 복원 모델이 그것을 가져다 쓴다.
/// </summary>
public sealed class MotionPlan
{
    private readonly IReadOnlyList<BoxPath> _paths;
    private readonly PixelRect _frame;
    private readonly int _maskGrow;
    private readonly bool[] _moves;

    private MotionPlan(IReadOnlyList<ProcessRegion> regions, IReadOnlyList<BoxPath> paths, PixelRect frame, int maskGrow)
    {
        (Regions, _paths, _frame, _maskGrow) = (regions, paths, frame, maskGrow);
        _moves = [.. regions.Select(r => paths.Any(p => p.Moves && p.Footprints().Any(f => f.ClampTo(frame).Intersects(r.Rect))))];
    }

    /// <summary>워터마크 제거를 하지 않을 때.</summary>
    public static MotionPlan None { get; } = new([], [], default, 0);

    public IReadOnlyList<ProcessRegion> Regions { get; }

    /// <summary>움직이는 박스가 하나라도 있는지.</summary>
    public bool AnyMoves => _moves.Contains(true);

    public static MotionPlan Create(IReadOnlyList<BoxPath> paths, int frameWidth, int frameHeight, RegionOptions? options = null)
    {
        options ??= new RegionOptions();
        var frame = new PixelRect(0, 0, frameWidth, frameHeight);
        // 화면에 걸쳐 있는 동안만 따라간다 (빠른 모드와 같다). 건너뛰는 박스가 화면 밖에 적어 둔 자리는 쓰지 않는다.
        IReadOnlyList<BoxPath> inside = [.. paths.Select(p => p.ClampTo(frame)).OfType<BoxPath>()];
        var regions = RegionPlanner.Plan([.. inside.SelectMany(p => p.Footprints())], frameWidth, frameHeight, options);
        return new MotionPlan(regions, inside, frame, options.MaskGrow);
    }

    /// <summary>이 영역의 마스크가 프레임마다 달라지는지 (움직이는 박스가 지나가는 영역).</summary>
    public bool Moves(int region) => _moves[region];

    /// <summary>그 프레임(원본 기준 번호)에 이 영역의 마스크에 칠할 사각형들 (영역 기준 좌표). 워터마크가 다른 데 가 있으면 비어 있다.</summary>
    public IReadOnlyList<PixelRect> MaskBoxes(int region, long frame)
    {
        var target = Regions[region];
        if (!_moves[region]) return target.MaskBoxes;
        var result = new List<PixelRect>();
        foreach (var path in _paths)
        {
            var box = path.At(frame).ClampTo(_frame);
            if (box.IsEmpty || !box.Intersects(target.Rect)) continue;
            var mask = box.Inflate(_maskGrow).ClampTo(target.Rect).Offset(-target.Rect.X, -target.Rect.Y);
            if (!mask.IsEmpty) result.Add(mask);
        }
        return result;
    }
}
