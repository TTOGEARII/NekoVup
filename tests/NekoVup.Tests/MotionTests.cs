using NekoVup.Core.Chunks;
using NekoVup.Core.Imaging;
using NekoVup.Core.Media;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Runtime;

namespace NekoVup.Tests;

public class BoxTrackTests
{
    private static readonly PixelRect A = new(100, 50, 80, 30), B = new(400, 250, 80, 30);

    [Fact]
    public void Parses_Times_And_Rects()
    {
        Assert.True(BoxTrack.TryParse("0=100,50,80,30; 0:04=400,250,80,30;1:02.5=10,20,30,40", jump: false, out var track));
        Assert.False(track.Jump);
        Assert.Equal([new BoxKey(0, A), new BoxKey(4, B), new BoxKey(62.5, new PixelRect(10, 20, 30, 40))], track.Keys);

        Assert.True(BoxTrack.TryParse("3=1,2,3,4", jump: true, out track));
        Assert.True(track.Jump);
        Assert.Single(track.Keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ; ")]
    [InlineData("100,50,80,30")]        // 시각이 없다
    [InlineData("=100,50,80,30")]
    [InlineData("abc=100,50,80,30")]
    [InlineData("1=100,50,80")]
    [InlineData("1=100,50,0,30")]       // 빈 박스
    [InlineData("0=1,2,3,4;2=여기")]
    [InlineData("8640001=1,2,3,4")]     // 100일보다 뒤
    [InlineData("99999999999999999999999=1,2,3,4")]
    public void Rejects_Malformed_Text(string? text)
    {
        Assert.False(BoxTrack.TryParse(text, jump: false, out var track));
        Assert.Empty(track.Keys);
    }

    [Fact]
    public void Takes_Times_Up_To_A_Hundred_Days()
    {
        Assert.True(BoxTrack.TryParse("2400:00:00=1,2,3,4", jump: false, out var track));
        Assert.Equal(BoxTrack.MaxSeconds, Assert.Single(track.Keys).Seconds);
    }

    [Fact]
    public void Text_Form_Round_Trips_And_Tells_The_Two_Kinds_Apart()
    {
        var move = new BoxTrack { Keys = [new BoxKey(0, A), new BoxKey(4.5, B)] };
        Assert.Equal("move:0=100,50,80,30;4.5=400,250,80,30", move.ToString());
        Assert.Equal("jump:0=100,50,80,30;4.5=400,250,80,30", (move with { Jump = true }).ToString());

        Assert.True(BoxTrack.TryParse(move.ToString()["move:".Length..], false, out var again));
        Assert.Equal(move.Keys, again.Keys);
    }
}

public class BoxPathTests
{
    private static readonly PixelRect A = new(100, 50, 80, 30), B = new(400, 250, 80, 30);

    private static BoxPath Path(bool jump, params (double Seconds, PixelRect Rect)[] keys) =>
        BoxPath.From(new BoxTrack { Keys = [.. keys.Select(k => new BoxKey(k.Seconds, k.Rect))], Jump = jump }, 30);

    [Fact]
    public void Fixed_Box_Never_Moves()
    {
        var path = BoxPath.Fixed(A);
        Assert.False(path.Moves);
        Assert.Equal(A, path.At(0));
        Assert.Equal(A, path.At(100_000));
        Assert.Equal([A], path.Footprints());
    }

    [Fact]
    public void Slides_In_A_Straight_Line_Between_Keys_And_Holds_Outside_Them()
    {
        var path = Path(false, (1, A), (5, B));
        Assert.True(path.Moves);
        Assert.Equal([30L, 150L], path.Frames);

        Assert.Equal(A, path.At(0));        // 첫 위치 앞
        Assert.Equal(A, path.At(30));
        Assert.Equal(new PixelRect(175, 100, 80, 30), path.At(60));
        Assert.Equal(new PixelRect(250, 150, 80, 30), path.At(90));
        Assert.Equal(B, path.At(150));
        Assert.Equal(B, path.At(9000));     // 마지막 위치 뒤
    }

    [Fact]
    public void Rounds_Each_Edge_To_The_Nearest_Pixel_And_Scales_The_Size()
    {
        var slide = BoxPath.From(new BoxTrack { Keys = [new BoxKey(0, new PixelRect(0, 0, 10, 10)), new BoxKey(4, new PixelRect(2, 0, 10, 10))] }, 1);
        Assert.Equal(new PixelRect(1, 0, 10, 10), slide.At(1));   // 0.5 → 1
        Assert.Equal(new PixelRect(1, 0, 10, 10), slide.At(2));
        Assert.Equal(new PixelRect(2, 0, 10, 10), slide.At(3));   // 1.5 → 2

        var grow = BoxPath.From(new BoxTrack { Keys = [new BoxKey(0, new PixelRect(0, 0, 10, 10)), new BoxKey(4, new PixelRect(0, 0, 20, 30))] }, 1);
        Assert.Equal(new PixelRect(0, 0, 15, 20), grow.At(2));
    }

    [Fact]
    public void Jumping_Box_Stays_Put_Until_The_Next_Key()
    {
        var path = Path(true, (0, A), (4, B), (8, A));
        Assert.Equal(A, path.At(119));
        Assert.Equal(B, path.At(120));
        Assert.Equal(B, path.At(239));
        Assert.Equal(A, path.At(240));
        Assert.Equal([A, B], path.Footprints());   // 같은 자리는 한 번만
    }

    [Fact]
    public void Keys_Are_Sorted_And_The_Later_One_Wins_On_The_Same_Frame()
    {
        var path = Path(false, (4, B), (0, A));
        Assert.Equal([0L, 120L], path.Frames);
        Assert.Equal([A, B], path.Rects);

        // 0.99초와 1초는 같은 프레임(30)에 나온다
        var same = Path(false, (0.99, A), (1.0, B));
        Assert.Equal([30L], same.Frames);
        Assert.Equal([B], same.Rects);
        Assert.False(same.Moves);
    }

    [Fact]
    public void Unusable_Keys_Are_Dropped_And_None_Left_Is_An_Error()
    {
        var path = Path(false, (double.NaN, B), (-1, B), (2, default), (3, A), (1e18, B), (double.PositiveInfinity, B));
        Assert.Equal([90L], path.Frames);
        Assert.Equal([A], path.Rects);

        Assert.Throws<ArgumentException>(() => Path(false));
        Assert.Throws<ArgumentException>(() => Path(false, (-1, A)));
    }

    [Fact]
    public void Footprints_Cover_Every_Place_The_Box_Passes()
    {
        var c = new PixelRect(400, 600, 80, 30);
        var path = Path(false, (0, A), (4, B), (8, c));
        var prints = path.Footprints();
        Assert.Equal([A.Union(B), B.Union(c)], prints);
        for (long frame = 0; frame <= 240; frame += 7)
        {
            var at = path.At(frame);
            Assert.Contains(prints, p => at.ClampTo(p) == at);
        }

        Assert.Equal([A, B, c], Path(true, (0, A), (4, B), (8, c)).Footprints());
    }

    [Fact]
    public void Clamping_Keeps_The_Box_Inside_On_Every_Frame()
    {
        var bounds = new PixelRect(1, 1, 318, 198);
        (double, PixelRect)[] keys = [(0, new PixelRect(0, 0, 50, 20)), (4, new PixelRect(400, 50, 50, 20)), (8, new PixelRect(300, 190, 50, 20))];

        // 머물다 건너뛰는 박스: 적어 둔 자리를 잘라 둔다
        var clamped = Assert.IsType<BoxPath>(Path(true, keys).ClampTo(bounds));
        Assert.Equal([0L, 240L], clamped.Frames);
        Assert.Equal([new PixelRect(1, 1, 49, 19), new PixelRect(300, 190, 19, 9)], clamped.Rects);
        Assert.Null(clamped.Bounds);

        // 직선으로 가는 박스: 자리는 그대로 두고 프레임마다 자른다. 밖에 적어 둔 4초 자리로 가다가 95번 프레임을 끝으로 나가고,
        // 나간 자리에 있다가 218번 프레임에 다시 들어온다 (그 사이에 화면을 가로질러 가지 않는다).
        clamped = Assert.IsType<BoxPath>(Path(false, keys).ClampTo(bounds));
        PixelRect exit = new(317, 40, 50, 20), entry = new(318, 164, 50, 20);
        Assert.Equal([0L, 95L, 217L, 218L, 240L], clamped.Frames);
        Assert.Equal([keys[0].Item2, exit, exit, entry, keys[2].Item2], clamped.Rects);
        Assert.Equal(bounds, clamped.Bounds);
        Assert.Equal(new PixelRect(1, 1, 49, 19), clamped.At(0));
        Assert.Equal(new PixelRect(160, 20, 50, 20), clamped.At(48));     // 4초 자리로 가는 길 위 (400×48/120, 50×48/120)
        Assert.Equal(new PixelRect(317, 40, 2, 20), clamped.At(95));
        Assert.Equal(new PixelRect(317, 40, 2, 20), clamped.At(120));
        Assert.Equal(new PixelRect(317, 40, 2, 20), clamped.At(217));
        Assert.Equal(new PixelRect(318, 164, 1, 20), clamped.At(218));
        Assert.Equal(new PixelRect(300, 190, 19, 9), clamped.At(240));
        for (long frame = 0; frame <= 260; frame++)
        {
            var at = clamped.At(frame);
            Assert.False(at.IsEmpty, $"프레임 {frame}: 자리가 비었습니다.");
            Assert.Contains(clamped.Footprints(), p => at.ClampTo(p) == at);
        }

        // 다 화면 안이면 그대로다
        var inside = Assert.IsType<BoxPath>(Path(false, (0, A), (4, A.Offset(20, 10))).ClampTo(new PixelRect(0, 0, 640, 360)));
        Assert.Null(inside.Bounds);
        Assert.Equal([A, A.Offset(20, 10)], inside.Rects);

        Assert.Null(BoxPath.Fixed(new PixelRect(500, 500, 10, 10)).ClampTo(bounds));
    }

    [Fact]
    public void A_Slide_Between_Two_Places_Outside_Is_Followed_While_It_Crosses()
    {
        // 왼쪽 밖에서 오른쪽 밖으로 4초 동안 1000px 간다. 13번 프레임에 들어와 100번 프레임을 끝으로 나간다.
        var screen = new PixelRect(0, 0, 640, 360);
        var path = Path(false, (0, new PixelRect(-200, 100, 100, 40)), (4, new PixelRect(800, 100, 100, 40)));
        var clamped = Assert.IsType<BoxPath>(path.ClampTo(screen));

        Assert.Equal([13L, 100L], clamped.Frames);
        Assert.Equal([new PixelRect(-92, 100, 100, 40), new PixelRect(633, 100, 100, 40)], clamped.Rects);
        Assert.Equal(new PixelRect(0, 100, 8, 40), clamped.At(0));
        Assert.Equal(new PixelRect(300, 100, 100, 40), clamped.At(60));   // 실제 자리 -200 + 1000×60/120
        Assert.Equal(new PixelRect(633, 100, 7, 40), clamped.At(100));
        Assert.Equal(new PixelRect(633, 100, 7, 40), clamped.At(500));
    }

    [Fact]
    public void A_Slide_That_Never_Shows_Is_Nothing()
    {
        var screen = new PixelRect(0, 0, 640, 360);
        // 화면 왼쪽 밖에서 아래로만 간다
        Assert.Null(Path(false, (0, new PixelRect(-200, 100, 100, 40)), (4, new PixelRect(-200, 300, 100, 40))).ClampTo(screen));
        // 왼쪽 밖에서 위쪽 밖으로, 모서리 바깥을 비껴간다
        Assert.Null(Path(false, (0, new PixelRect(-300, 100, 100, 40)), (4, new PixelRect(100, -300, 100, 40))).ClampTo(screen));
        // 밖에 적어 둔 자리 하나
        Assert.Null(Path(false, (0, new PixelRect(700, 100, 100, 40))).ClampTo(screen));
    }

    [Fact]
    public void Sliding_In_From_Outside_Keeps_The_Real_Position_On_The_Way()
    {
        // 왼쪽 밖에 60px 걸친 자리에서 4초 동안 360px 간다. 걸친 자리를 먼저 자르고 이으면 중간에서 왼쪽 변이 실제보다 안쪽에 놓인다.
        var path = Path(false, (0, new PixelRect(-60, 100, 100, 40)), (4, new PixelRect(300, 100, 100, 40)));
        var clamped = Assert.IsType<BoxPath>(path.ClampTo(new PixelRect(1, 1, 638, 358)));

        Assert.Equal(new PixelRect(1, 100, 39, 40), clamped.At(0));
        Assert.Equal(new PixelRect(1, 100, 69, 40), clamped.At(10));     // 실제 자리 -30..70
        Assert.Equal(new PixelRect(120, 100, 100, 40), clamped.At(60));
        Assert.Equal(new PixelRect(300, 100, 100, 40), clamped.At(120));
    }

    [Fact]
    public void Flattening_Writes_Cut_Places_That_Join_Into_The_Same_Path()
    {
        // 화면의 숫자 칸에 넣을 수 있게 자른 자리만 적는다. 변이 경계를 넘는 프레임에도 자리를 적어 두어서, 그 사이를 이어도 프레임마다 자른 것과 같다.
        var screen = new PixelRect(0, 0, 640, 360);
        var clamped = Assert.IsType<BoxPath>(Path(false, (0, new PixelRect(-30, 100, 50, 40)), (2, new PixelRect(100, 100, 50, 40))).ClampTo(screen));
        var flat = clamped.Flatten();
        Assert.Null(flat.Bounds);
        Assert.Equal([0L, 13L, 14L, 60L], flat.Frames);                  // 왼쪽 변이 13.8번 프레임에 화면으로 들어온다
        Assert.Equal(new PixelRect(0, 100, 20, 40), flat.At(0));
        Assert.Equal(new PixelRect(35, 100, 50, 40), flat.At(30));       // 자른 두 끝만 이으면 왼쪽 변이 50에 놓인다
        AssertSamePlaces(clamped, flat, screen, 0, 80);

        // 나갔다가 다른 데로 들어오는 길, 화면을 가로질러 지나가는 길
        var bounds = new PixelRect(1, 1, 318, 198);
        clamped = Assert.IsType<BoxPath>(Path(false, (0, new PixelRect(0, 0, 50, 20)), (4, new PixelRect(400, 50, 50, 20)), (8, new PixelRect(300, 190, 50, 20))).ClampTo(bounds));
        AssertSamePlaces(clamped, clamped.Flatten(), bounds, 0, 260);
        clamped = Assert.IsType<BoxPath>(Path(false, (0, new PixelRect(-200, 100, 100, 40)), (4, new PixelRect(800, 100, 100, 40))).ClampTo(screen));
        AssertSamePlaces(clamped, clamped.Flatten(), screen, 0, 140);

        // 자를 것이 없는 경로는 그대로다
        var inside = Path(false, (0, A), (4, B));
        Assert.Same(inside, inside.Flatten());
    }

    private static void AssertSamePlaces(BoxPath expected, BoxPath actual, PixelRect bounds, long from, long to)
    {
        for (long frame = from; frame <= to; frame++)
        {
            var (e, a) = (expected.At(frame), actual.At(frame));
            Assert.True(a.ClampTo(bounds) == a, $"프레임 {frame}: {a}가 화면 밖에 걸칩니다.");
            // 자른 자리를 반올림해 적은 것을 다시 이으므로 1픽셀까지는 다를 수 있다
            Assert.True(Math.Abs(e.X - a.X) <= 1 && Math.Abs(e.Y - a.Y) <= 1 && Math.Abs(e.Right - a.Right) <= 1 && Math.Abs(e.Bottom - a.Bottom) <= 1,
                $"프레임 {frame}: {e} 자리가 {a}로 바뀌었습니다.");
        }
    }

    [Fact]
    public void Leaving_And_Coming_Back_Elsewhere_Does_Not_Cover_The_Way_Between()
    {
        // 왼쪽 위에서 위로 나갔다가 오른쪽 위로 들어온다. 나간 자리와 들어온 자리 사이(화면 위쪽 가장자리)는 지나가지 않는다 —
        // 그 사이까지 덮으면 처리 영역이 화면 너비만큼 커진다.
        PixelRect left = new(100, 40, 190, 56), right = new(1700, 40, 190, 56);
        var path = Path(false, (0, left), (2, left.Offset(0, -240)), (4, right.Offset(0, -240)), (6, right));
        var clamped = Assert.IsType<BoxPath>(path.ClampTo(new PixelRect(0, 0, 1920, 1080)));

        Assert.All(clamped.Footprints(), print => Assert.Equal(190, print.Width));
        for (long frame = 0; frame <= 200; frame++)
        {
            var at = clamped.At(frame);
            Assert.Contains(clamped.Footprints(), p => at.ClampTo(p) == at);
        }
        Assert.Equal(2, MotionPlan.Create([path], 1920, 1080).Regions.Count);
    }

    [Fact]
    public void FromFrames_Sorts_Keys_And_Drops_Empty_Ones()
    {
        var keys = new Dictionary<long, PixelRect> { [90] = B, [30] = A, [60] = default, [-1] = B };
        var path = BoxPath.FromFrames(keys, jump: true);

        Assert.Equal([30L, 90L], path.Frames);
        Assert.Equal([A, B], path.Rects);
        Assert.Equal(A, path.At(89));
        Assert.Equal(B, path.At(90));
        Assert.Throws<ArgumentException>(() => BoxPath.FromFrames(new Dictionary<long, PixelRect> { [0] = default }, jump: false));
    }
}

public class MotionPlanTests
{
    private const int W = 1280, H = 720;

    private static BoxPath Path(bool jump, params (double Seconds, PixelRect Rect)[] keys) =>
        BoxPath.From(new BoxTrack { Keys = [.. keys.Select(k => new BoxKey(k.Seconds, k.Rect))], Jump = jump }, 30);

    [Fact]
    public void Nothing_To_Remove()
    {
        Assert.Empty(MotionPlan.None.Regions);
        Assert.False(MotionPlan.None.AnyMoves);
    }

    [Fact]
    public void Fixed_Boxes_Plan_Exactly_Like_Before()
    {
        var box = new PixelRect(1040, 40, 190, 56);
        var plan = MotionPlan.Create([BoxPath.Fixed(box)], W, H);
        var expected = Assert.Single(RegionPlanner.Plan([box], W, H));

        var region = Assert.Single(plan.Regions);
        Assert.Equal(expected.Rect, region.Rect);
        Assert.Equal(expected.MaskBoxes, region.MaskBoxes);
        Assert.False(plan.AnyMoves);
        Assert.False(plan.Moves(0));
        Assert.Same(region.MaskBoxes, plan.MaskBoxes(0, 12345));
    }

    [Fact]
    public void Sliding_Box_Gets_One_Region_And_A_Mask_That_Follows_It()
    {
        PixelRect a = new(100, 100, 80, 30), b = new(400, 250, 80, 30);
        var plan = MotionPlan.Create([Path(false, (0, a), (4, b))], W, H);

        var region = Assert.Single(plan.Regions);
        Assert.True(plan.AnyMoves);
        Assert.True(plan.Moves(0));
        var cover = a.Union(b);
        Assert.Equal(cover, cover.ClampTo(region.Rect));   // 지나가는 자리를 모두 덮는다

        PixelRect Mask(PixelRect box) => new(box.X - 2 - region.Rect.X, box.Y - 2 - region.Rect.Y, box.Width + 4, box.Height + 4);
        Assert.Equal([Mask(a)], plan.MaskBoxes(0, 0));
        Assert.Equal([Mask(new PixelRect(250, 175, 80, 30))], plan.MaskBoxes(0, 60));
        Assert.Equal([Mask(b)], plan.MaskBoxes(0, 500));
    }

    [Fact]
    public void Jumping_Box_Gets_A_Region_Per_Place_And_Masks_Only_Where_It_Is()
    {
        PixelRect a = new(40, 40, 100, 40), b = new(1100, 640, 100, 40);
        var plan = MotionPlan.Create([Path(true, (0, a), (3, b))], W, H);

        Assert.Equal(2, plan.Regions.Count);
        Assert.True(plan.Moves(0) && plan.Moves(1));
        Assert.Single(plan.MaskBoxes(0, 89));
        Assert.Empty(plan.MaskBoxes(1, 89));
        Assert.Empty(plan.MaskBoxes(0, 90));
        var mask = Assert.Single(plan.MaskBoxes(1, 90));
        var region = plan.Regions[1].Rect;
        Assert.Equal(new PixelRect(b.X - 2 - region.X, b.Y - 2 - region.Y, 104, 44), mask);
    }

    [Fact]
    public void Places_Outside_The_Frame_Are_Not_Used()
    {
        // 5초 자리는 화면 밖이다. 그리로 건너뛰면 마스크가 비어 워터마크가 남는다 — 빼고 첫 자리에 머문다.
        PixelRect a = new(100, 100, 50, 50), gone = new(5000, 100, 50, 50);
        var jump = MotionPlan.Create([Path(true, (0, a), (5, gone))], W, H);
        Assert.Single(jump.Regions);
        Assert.False(jump.AnyMoves);
        Assert.Single(jump.MaskBoxes(0, 149));
        Assert.Equal(jump.MaskBoxes(0, 149), jump.MaskBoxes(0, 400));

        // 직선으로 가는 박스는 그 자리를 향해 가다가 화면을 벗어난다 (36번 프레임이 마지막) — 그 뒤로는 나간 자리의 가장자리만 남는다.
        var slide = MotionPlan.Create([Path(false, (0, a), (5, gone))], W, H);
        Assert.Single(slide.Regions);
        var first = Assert.Single(slide.MaskBoxes(0, 0));
        Assert.Equal(first.Offset(980, 0), Assert.Single(slide.MaskBoxes(0, 30)));   // 4900×30/150
        var edge = Assert.Single(slide.MaskBoxes(0, 36));
        Assert.True(edge.Width <= 6, $"나간 자리의 마스크가 넓습니다: {edge}");
        Assert.Equal([edge], slide.MaskBoxes(0, 75));
        Assert.Equal([edge], slide.MaskBoxes(0, 400));

        Assert.Throws<ArgumentException>(() => MotionPlan.Create([Path(true, (0, gone))], W, H));
        Assert.Throws<ArgumentException>(() => MotionPlan.Create([Path(false, (0, gone), (5, gone.Offset(0, 300)))], W, H));
    }

    [Fact]
    public void Sliding_All_The_Way_Out_Is_Followed_Until_The_Last_Frame_It_Shows()
    {
        // 4초 자리는 화면 바로 밖이다 (왼쪽 변이 화면 오른쪽 끝). 밖의 자리를 빼 버리면 박스가 제자리에 머물러 워터마크를 놓친다.
        PixelRect a = new(1000, 100, 200, 60), b = new(1280, 100, 200, 60);
        var plan = MotionPlan.Create([Path(false, (0, a), (4, b))], W, H);

        var region = Assert.Single(plan.Regions).Rect;
        var mask = Assert.Single(plan.MaskBoxes(0, 60));   // 실제 자리 1140..1340 → 화면 안은 1140..1280
        Assert.Equal(new PixelRect(1140 - 2 - region.X, 100 - 2 - region.Y, 140 + 2, 64), mask);
        mask = Assert.Single(plan.MaskBoxes(0, 119));      // 마지막으로 걸치는 프레임: 1278..1280
        Assert.Equal(new PixelRect(1278 - 2 - region.X, 100 - 2 - region.Y, 2 + 2, 64), mask);
        Assert.Equal([mask], plan.MaskBoxes(0, 300));
    }

    [Fact]
    public void Sliding_Out_Of_The_Frame_Keeps_The_Mask_On_The_Part_Still_Inside()
    {
        // 오른쪽으로 나가는 워터마크: 4초에는 20px만 화면에 남는다
        PixelRect a = new(1000, 100, 200, 60), b = new(1260, 100, 200, 60);
        var plan = MotionPlan.Create([Path(false, (0, a), (4, b))], W, H);

        var region = Assert.Single(plan.Regions).Rect;
        var mask = Assert.Single(plan.MaskBoxes(0, 60));   // 실제 자리 1130..1330 → 화면 안은 1130..1280
        Assert.Equal(new PixelRect(1130 - 2 - region.X, 100 - 2 - region.Y, 150 + 2, 64), mask);
        mask = Assert.Single(plan.MaskBoxes(0, 120));
        Assert.Equal(new PixelRect(1260 - 2 - region.X, 100 - 2 - region.Y, 20 + 2, 64), mask);
    }

    [Fact]
    public void Fixed_Box_Elsewhere_Keeps_Its_Single_Mask()
    {
        var logo = new PixelRect(1040, 40, 190, 56);
        var plan = MotionPlan.Create([BoxPath.Fixed(logo), Path(false, (0, new PixelRect(100, 500, 80, 30)), (4, new PixelRect(300, 600, 80, 30)))], W, H);

        Assert.Equal(2, plan.Regions.Count);
        Assert.True(plan.AnyMoves);
        Assert.False(plan.Moves(0));    // 위쪽(고정 로고) 영역
        Assert.True(plan.Moves(1));
        Assert.Same(plan.Regions[0].MaskBoxes, plan.MaskBoxes(0, 60));
        Assert.Single(plan.MaskBoxes(1, 60));
    }

    [Fact]
    public void Fixed_Box_Inside_A_Moving_Region_Is_Masked_On_Every_Frame()
    {
        PixelRect logo = new(200, 200, 60, 20), a = new(100, 100, 80, 30), b = new(400, 250, 80, 30);
        var plan = MotionPlan.Create([BoxPath.Fixed(logo), Path(true, (0, a), (2, b))], W, H);

        Assert.Single(plan.Regions);
        Assert.Equal(2, plan.MaskBoxes(0, 0).Count);
        Assert.Equal(2, plan.MaskBoxes(0, 200).Count);
    }
}

public class FrameGridSourceFrameTests
{
    private const double Fps = 30000.0 / 1001;

    [Fact]
    public void From_The_Beginning_Frames_Count_Up_From_Zero()
    {
        for (int i = 0; i < 5; i++) Assert.Equal(i, FrameGrid.SourceFrame(null, i, Fps));
    }

    [Fact]
    public void Start_Between_Frames_Repeats_The_First_Frame_Once()
    {
        // 1.98초 = 59.34프레임: 60번이 0.66칸에 놓여 1번 칸으로 가고, 0번 칸은 60번을 한 번 더 쓴다
        Assert.Equal([60L, 60L, 61L, 62L], Enumerable.Range(0, 4).Select(i => FrameGrid.SourceFrame(1.98, i, Fps)));
        // 59.7프레임에서 시작: 60번이 0.3칸 → 0번 칸
        Assert.Equal([60L, 61L, 62L], Enumerable.Range(0, 3).Select(i => FrameGrid.SourceFrame(59.7 / Fps, i, Fps)));
    }

    [Fact]
    public void Start_Made_For_A_Frame_Counts_Up_From_That_Frame()
    {
        Assert.Equal([1949L, 1950L, 1951L], Enumerable.Range(0, 3).Select(i => FrameGrid.SourceFrame(FrameGrid.SeekSeconds(1949, Fps), i, Fps)));
        Assert.Equal([90L, 91L], Enumerable.Range(0, 2).Select(i => FrameGrid.SourceFrame(3.0, i, 30)));
    }
}

public class MovingDelogoFilterTests
{
    private static readonly PixelRect A = new(100, 50, 80, 30), B = new(400, 250, 80, 30);

    private static BoxPath Path(bool jump, params (double Seconds, PixelRect Rect)[] keys) =>
        BoxPath.From(new BoxTrack { Keys = [.. keys.Select(k => new BoxKey(k.Seconds, k.Rect))], Jump = jump }, 30);

    [Fact]
    public void Fixed_Box_Is_Written_As_Plain_Numbers()
    {
        Assert.Equal("delogo=x=10:y=20:w=30:h=40,delogo=x=1:y=2:w=3:h=4",
            FfmpegCommands.DelogoFilter([BoxPath.Fixed(new PixelRect(10, 20, 30, 40)), BoxPath.Fixed(new PixelRect(1, 2, 3, 4))], 30, 12.5));
    }

    [Fact]
    public void Sliding_Box_Is_Written_As_Per_Frame_Expressions()
    {
        const string f = "0*st(0,max(0,ceil(t*30+0-0.5)))", u = "clip((ld(0)-0)/120,0,1)";
        Assert.Equal(
            $"delogo=x='floor(0.5+{f}+100+300*{u})':y='floor(0.5+{f}+50+200*{u})'" +
            $":w='floor(0.5+{f}+180+300*{u})-floor(0.5+100+300*{u})':h='floor(0.5+{f}+80+200*{u})-floor(0.5+50+200*{u})'",
            FfmpegCommands.DelogoFilter([Path(false, (0, A), (4, B))], 30, null));
    }

    [Fact]
    public void Jumping_Box_Steps_At_The_Key_Frame_And_Skips_Edges_That_Stay()
    {
        const string f = "0*st(0,max(0,ceil(t*30+0-0.5)))", step = "230*gte(ld(0),120)";
        Assert.Equal(
            $"delogo=x='floor(0.5+{f}+300-{step})':y='floor(0.5+{f}+100)'" +
            $":w='floor(0.5+{f}+380-{step})-floor(0.5+300-{step})':h='floor(0.5+{f}+130)-floor(0.5+100)'",
            FfmpegCommands.DelogoFilter([Path(true, (0, new PixelRect(300, 100, 80, 30)), (4, new PixelRect(70, 100, 80, 30)))], 30, null));
    }

    [Fact]
    public void Box_Sliding_In_From_Outside_Is_Cut_On_Every_Frame()
    {
        // 화면 640x360. 왼쪽 밖에 걸친 자리에서 들어온다 — 자리는 자르지 않고 잇고, 식에서 프레임마다 자른다.
        var path = Path(false, (0, new PixelRect(-60, 100, 100, 40)), (4, new PixelRect(300, 100, 100, 40)));
        var plan = Assert.Single(DelogoPlanner.Plan([path], 640, 360));

        const string f = "0*st(0,max(0,ceil(t*30+0-0.5)))", u = "360*clip((ld(0)-0)/120,0,1)";
        Assert.Equal(
            $"delogo=x='max(1,floor(0.5+{f}+-60+{u}))':y='max(1,floor(0.5+{f}+100))'" +
            $":w='min(639,floor(0.5+{f}+40+{u}))-max(1,floor(0.5+-60+{u}))':h='min(359,floor(0.5+{f}+140))-max(1,floor(0.5+100))'",
            FfmpegCommands.DelogoFilter([plan], 30, null));
    }

    [Fact]
    public void Box_That_Only_Sticks_Out_Is_Still_Plain_Numbers()
    {
        var plan = DelogoPlanner.Plan([BoxPath.Fixed(new PixelRect(-10, -5, 50, 40)), Path(false, (0, new PixelRect(600, 300, 100, 100)), (4, new PixelRect(600, 300, 100, 100)))], 640, 360);

        Assert.Equal("delogo=x=1:y=1:w=39:h=34,delogo=x=600:y=300:w=39:h=59", FfmpegCommands.DelogoFilter(plan, 30, null));
    }

    [Fact]
    public void Many_Places_Are_Summed_In_Nested_Groups()
    {
        // ffmpeg은 괄호 없이 90항쯤 이어 더한 식을 읽지 못한다
        var keys = Enumerable.Range(0, 200).Select(i => ((double)i, new PixelRect(10 + i * 2 + i % 2, 20, 30, 40))).ToArray();
        string filter = FfmpegCommands.DelogoFilter([Path(true, keys)], 30, null);

        string x = System.Text.RegularExpressions.Regex.Replace(filter.Split(':')[0], @"gte\(ld\(0\),\d+\)", "#");
        Assert.Contains(")+(", x);
        Assert.Equal(199, x.Count(c => c == '#'));
        // 괄호 한 겹 안에서 이어 더한 항은 16개까지
        foreach (string run in x.Split('(', ')'))
            Assert.True(run.Count(c => c == '#') <= 16, run);
    }

    [Fact]
    public void Frame_Number_Accounts_For_The_Range_Start()
    {
        string filter = FfmpegCommands.DelogoFilter([Path(false, (0, A), (4, B))], 30, 2.5);
        Assert.Contains("st(0,max(75,ceil(t*30+75-0.5)))", filter);

        // 프레임 사이에서 시작 (59.34프레임): 첫 프레임은 60번
        filter = FfmpegCommands.DelogoFilter([Path(false, (0, A), (4, B))], 30000.0 / 1001, 1.98);
        Assert.Contains("st(0,max(60,ceil(t*29.97002997002997+59.34", filter);
    }
}

public class MovingAlphaTests
{
    /// <summary>창을 쓰지 않고 영역 전체를 계산한 알파.</summary>
    private static byte[] Whole(int w, int h, IReadOnlyList<PixelRect> boxes, int dilation, double ratio)
    {
        var mask = MaskBuilder.BuildMask(w, h, boxes);
        int inner = MaskBuilder.InnerRadius(dilation, ratio);
        var solid = new byte[w * h];
        ImageOps.Dilate(mask, solid, w, h, inner);
        var wide = new byte[w * h];
        ImageOps.Dilate(mask, wide, w, h, inner + MaskBuilder.FeatherExtra);
        var soft = new byte[w * h];
        ImageOps.GaussianBlur(wide, soft, w, h, MaskBuilder.FeatherSigma);
        for (int i = 0; i < solid.Length; i++)
            if (soft[i] > solid[i]) solid[i] = soft[i];
        return solid;
    }

    public static TheoryData<int[][], double> Cases => new()
    {
        { [[150, 120, 60, 30]], 1.0 },                       // 한가운데
        { [[0, 0, 40, 20]], 1.0 },                           // 왼쪽 위 구석
        { [[370, 285, 30, 15]], 1.0 },                       // 오른쪽 아래 구석
        { [[5, 140, 30, 20], [330, 10, 50, 25]], 1.0 },      // 멀리 떨어진 둘
        { [[150, 120, 60, 30]], 0.5 },                       // 줄여서 처리 (알파가 넓다)
        { [[-20, 100, 40, 30], [390, 290, 40, 40]], 0.62 },  // 영역 밖으로 걸친 것
        { [[0, 0, 400, 300]], 1.0 },                         // 영역 전체
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Alpha_Computed_In_A_Window_Equals_The_Whole_Computation(int[][] rects, double ratio)
    {
        const int w = 400, h = 300;
        PixelRect[] boxes = [.. rects.Select(r => new PixelRect(r[0], r[1], r[2], r[3]))];

        var alpha = MaskBuilder.BuildAlpha(w, h, boxes, 4, ratio);

        Assert.Equal(Whole(w, h, boxes, 4, ratio), alpha);
        Assert.Contains((byte)255, alpha);
    }

    [Fact]
    public void No_Boxes_Means_Fully_Transparent()
    {
        Assert.All(MaskBuilder.BuildAlpha(64, 48, [], 4), v => Assert.Equal(0, v));
        Assert.All(MaskBuilder.BuildAlpha(64, 48, [new PixelRect(200, 200, 10, 10)], 4), v => Assert.Equal(0, v));
        Assert.Null(new MovingAlpha(64, 48, 4, 1.0).For([]));
    }

    [Fact]
    public void Same_Place_Reuses_The_Same_Alpha()
    {
        var moving = new MovingAlpha(200, 120, 4, 1.0);
        PixelRect[] here = [new(50, 40, 30, 20)];

        var first = moving.For(here);
        Assert.NotNull(first);
        Assert.Same(first, moving.For([new PixelRect(50, 40, 30, 20)]));
        Assert.Equal(MaskBuilder.BuildAlpha(200, 120, here, 4), first);

        // 자리가 계속 바뀌어도 (쌓아 둔 것을 비우고 나서도) 값은 맞다
        for (int x = 0; x < 40; x++) Assert.NotNull(moving.For([new PixelRect(x, 10, 30, 20)]));
        Assert.Equal(first, moving.For(here));
    }
}

public class MovingJobKeyTests
{
    [Fact]
    public void Job_Key_Changes_With_The_Moving_Boxes()
    {
        PixelRect a = new(100, 50, 80, 30), b = new(400, 250, 80, 30);
        var track = new BoxTrack { Keys = [new BoxKey(0, a), new BoxKey(4, b)] };
        var request = new JobRequest { InputPath = @"C:\없는\a.mp4", OutputPath = @"C:\없는\b.mp4", Boxes = [], Tracks = [track] };
        string key = JobManifest.ComputeKey(request);

        Assert.Equal(key, JobManifest.ComputeKey(request with { Tracks = [new BoxTrack { Keys = [new BoxKey(0, a), new BoxKey(4, b)] }] }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Tracks = [] }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Tracks = [track with { Jump = true }] }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Tracks = [new BoxTrack { Keys = [new BoxKey(0, a), new BoxKey(4.5, b)] }] }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Tracks = [new BoxTrack { Keys = [new BoxKey(0, a), new BoxKey(4, b.Offset(1, 0))] }] }));
        Assert.NotEqual(key, JobManifest.ComputeKey(request with { Boxes = [a], Tracks = [] }));
    }

    [Fact]
    public void Request_Paths_Hold_Fixed_Boxes_First_And_Skip_Empty_Tracks()
    {
        PixelRect a = new(100, 50, 80, 30), b = new(400, 250, 80, 30);
        var request = new JobRequest
        {
            InputPath = "a.mp4", OutputPath = "b.mp4", Boxes = [a],
            Tracks = [new BoxTrack { Keys = [new BoxKey(0, a), new BoxKey(4, b)] }, new BoxTrack { Keys = [] }],
        };

        var paths = request.Paths(30);

        Assert.Equal(2, paths.Count);
        Assert.False(paths[0].Moves);
        Assert.Equal([0L, 120L], paths[1].Frames);
    }
}

public class DescribeBoxesTests
{
    private static readonly MediaInfo Info = new()
    {
        Path = @"C:\영상\원본.mp4",
        VideoStreamIndex = 0,
        Width = 640,
        Height = 360,
        CodedWidth = 640,
        CodedHeight = 360,
        Fps = new Rational(30, 1),
        DurationSeconds = 8,
        ColorSpace = null,
        ColorRange = null,
        AudioCodecs = [],
    };

    private static readonly PixelRect Logo = new(300, 100, 100, 40), Away = new(5000, 100, 100, 40);

    private static List<string> Describe(CleanMode mode, IReadOnlyList<PixelRect> boxes, params BoxTrack[] tracks)
    {
        var lines = new List<string>();
        var request = new JobRequest
        {
            InputPath = Info.Path,
            OutputPath = @"C:\영상\결과.mp4",
            Boxes = boxes,
            Tracks = tracks,
            Options = new JobOptions { Mode = mode },
        };
        JobRunner.DescribeBoxes(request, Info, new DelegateJobLog((level, message) => lines.Add($"{level}: {message}")));
        return lines;
    }

    private static BoxTrack Track(bool jump, params (double Seconds, PixelRect Rect)[] keys) =>
        new() { Keys = [.. keys.Select(k => new BoxKey(k.Seconds, k.Rect))], Jump = jump };

    [Fact]
    public void Says_How_Each_Moving_Box_Is_Followed()
    {
        Assert.Equal(["Info: 움직이는 박스: 위치 2곳 (0:00, 0:04), 사이를 직선으로 이어 감"],
            Describe(CleanMode.Delogo, [], Track(false, (0, Logo), (4, Logo.Offset(50, 0)))));
        Assert.Equal(["Info: 움직이는 박스: 위치 2곳 (0:00, 1:05.5), 자리를 건너뛰며 옮김"],
            Describe(CleanMode.ProPainter, [], Track(true, (65.5, Logo.Offset(50, 0)), (0, Logo))));
        // 자리가 하나거나, 같은 프레임에 적은 것뿐이면 움직이지 않는다 (뒤에 적은 것을 쓴다)
        Assert.Equal(["Info: 움직이는 박스: 위치 1곳 (0:02), 한 자리에 고정"], Describe(CleanMode.Delogo, [], Track(false, (2, Logo))));
        Assert.Equal(["Info: 움직이는 박스: 위치 1곳 (0:01), 한 자리에 고정"],
            Describe(CleanMode.Delogo, [], Track(false, (0.99, Logo), (1, Logo.Offset(50, 0)))));
        // 많으면 앞의 여덟 곳만 적는다
        var many = Track(true, [.. Enumerable.Range(0, 10).Select(i => ((double)i, Logo.Offset(i, 0)))]);
        Assert.Equal(["Info: 움직이는 박스: 위치 10곳 (0:00, 0:01, 0:02, 0:03, 0:04, 0:05, 0:06, 0:07, …), 자리를 건너뛰며 옮김"],
            Describe(CleanMode.Delogo, [], many));
    }

    [Fact]
    public void Warns_About_What_Is_Left_Out()
    {
        Assert.Equal(["Warning: 박스 (5000,100) 100x40는 화면(640x360) 밖이라 처리하지 않습니다."], Describe(CleanMode.Delogo, [Logo, Away]));
        // delogo는 가장자리 1픽셀을 쓰지 못한다
        var corner = new PixelRect(0, 0, 1, 360);
        Assert.Single(Describe(CleanMode.Delogo, [corner]));
        Assert.Empty(Describe(CleanMode.ProPainter, [corner]));

        // 건너뛰는 박스는 밖에 적은 자리를 쓰지 않는다
        Assert.Equal(
            ["Warning: 움직이는 박스의 0:04 위치 (5000,100) 100x40는 화면(640x360) 밖이라 쓰지 않습니다.", "Info: 움직이는 박스: 위치 1곳 (0:00), 한 자리에 고정"],
            Describe(CleanMode.Delogo, [], Track(true, (0, Logo), (4, Away))));
        // 직선으로 가는 박스는 밖의 자리도 가는 방향으로 쓴다
        Assert.Equal(["Info: 움직이는 박스: 위치 2곳 (0:00, 0:04), 사이를 직선으로 이어 감"], Describe(CleanMode.Delogo, [], Track(false, (0, Logo), (4, Away))));
        // 한 번도 걸치지 않으면 뺀다 — "위치 0곳" 같은 줄은 적지 않는다
        Assert.Equal(["Warning: 움직이는 박스가 화면(640x360)에 한 번도 걸치지 않아 처리하지 않습니다."],
            Describe(CleanMode.Delogo, [], Track(false, (0, Away), (4, Away.Offset(0, 100)))));
        Assert.Equal(["Warning: 움직이는 박스에 쓸 수 있는 위치가 없어 처리하지 않습니다."], Describe(CleanMode.Delogo, [], Track(false, (-1, Logo))));
    }

    [Fact]
    public void Says_Nothing_When_Nothing_Is_Removed() =>
        Assert.Empty(Describe(CleanMode.None, [Away], Track(false, (0, Logo), (4, Away))));
}

/// <summary>움직이는 박스로 실제 영상을 처리해 본다. 원본의 워터마크는 제자리에 있고, 박스만 옮겨 다닌다 — 박스가 떠난 프레임에는 워터마크가 남아야 한다.</summary>
[Collection("pipeline")]
public class MovingPipelineTests(SourceFixture fixture)
{
    private const int W = SourceFixture.Width, H = SourceFixture.Height;
    private static readonly PixelRect Box = TestTools.Watermark, Far = new(100, 500, 190, 56);

    /// <summary>3초(90번 프레임)에 워터마크 자리에서 먼 곳으로 건너뛰는 박스.</summary>
    private static readonly BoxTrack Hop = new() { Keys = [new BoxKey(0, Box), new BoxKey(3, Far)], Jump = true };

    private const int HopFrame = 90;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private JobRequest Request(string outputName, JobOptions options, params BoxTrack[] tracks) => new()
    {
        InputPath = fixture.Source,
        OutputPath = Path.Combine(fixture.Dir, outputName),
        Boxes = [],
        Tracks = tracks,
        Options = options,
    };

    private static JobRunner Runner(IToolRunner runner, List<string>? log = null) =>
        new(TestTools.MockSettings(), runner, new DelegateJobLog((level, message) =>
        {
            if (log is null) return;
            lock (log) log.Add($"{level}: {message}");
        }));

    private async Task<double> DiffAsync(string a, int frameA, string b, int frameB, PixelRect rect)
    {
        var first = await TestTools.DecodeRgbFrameAsync(a, frameA, fixture.Dir, Ct);
        var second = await TestTools.DecodeRgbFrameAsync(b, frameB, fixture.Dir, Ct);
        return TestTools.MeanAbsDiff(first, second, W, rect);
    }

    /// <summary>결과의 <paramref name="frame"/>번 프레임에서 워터마크가 지워졌는지 / 그대로인지. <paramref name="sourceFrame"/>은 그에 해당하는 원본 프레임.</summary>
    private async Task AssertWatermarkAsync(string output, int frame, int sourceFrame, bool removed)
    {
        if (removed)
        {
            double diff = await DiffAsync(fixture.Source, sourceFrame, output, frame, Box.Inflate(-8));
            Assert.True(diff > 8, $"프레임 {frame}: 워터마크 자리가 바뀌지 않았습니다 (평균 오차 {diff:0.00}).");
        }
        else
        {
            double diff = await DiffAsync(fixture.Plain, sourceFrame, output, frame, Box);
            Assert.True(diff < 4, $"프레임 {frame}: 박스가 떠난 뒤인데 워터마크 자리가 바뀌었습니다 (평균 오차 {diff:0.00}).");
        }
    }

    private async Task AssertOutsideUntouchedAsync(string output, params PixelRect[] changed)
    {
        bool Outside(int x, int y)
        {
            foreach (var r in changed)
                if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom) return false;
            return true;
        }

        string luma = await TestTools.DecodeLumaAsync(output, Path.Combine(fixture.Dir, Path.GetFileNameWithoutExtension(output) + ".y"), Ct);
        var (plain, _) = TestTools.Psnr(fixture.SourceLuma, fixture.PlainLuma, W, H, Outside);
        var (ours, frames) = TestTools.Psnr(fixture.SourceLuma, luma, W, H, Outside);
        File.Delete(luma);

        Assert.Equal(fixture.Frames, frames);
        Assert.True(ours >= plain - 0.1, $"박스 밖 PSNR이 떨어졌습니다: 결과 {ours:0.00} dB, 단순 재인코딩 {plain:0.00} dB");
    }

    [Fact]
    public async Task Delogo_Follows_A_Jumping_Box_To_The_Frame()
    {
        TestTools.RequireFfmpeg();
        var request = Request("건너뛰는 박스 delogo.mp4", new JobOptions { Mode = CleanMode.Delogo }, Hop);
        var log = new List<string>();

        await Runner(new RecordingRunner(), log).RunAsync(request, null, Ct);

        Assert.Equal(fixture.Frames, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        await AssertWatermarkAsync(request.OutputPath, 30, 30, removed: true);
        await AssertWatermarkAsync(request.OutputPath, HopFrame - 1, HopFrame - 1, removed: true);
        await AssertWatermarkAsync(request.OutputPath, HopFrame, HopFrame, removed: false);
        await AssertWatermarkAsync(request.OutputPath, 150, 150, removed: false);
        await AssertOutsideUntouchedAsync(request.OutputPath, Box.Inflate(8), Far.Inflate(8));
        Assert.Contains(log, line => line.Contains("움직이는 박스: 위치 2곳"));
    }

    [Fact]
    public async Task Delogo_Follows_A_Sliding_Box()
    {
        TestTools.RequireFfmpeg();
        // 워터마크 자리에서 출발해 4초 동안 아래로 300px 내려간다 → 처음에는 지워지고, 나중에는 그대로 남는다
        var slide = new BoxTrack { Keys = [new BoxKey(0, Box), new BoxKey(4, Box.Offset(0, 300))] };
        var request = Request("미끄러지는 박스 delogo.mp4", new JobOptions { Mode = CleanMode.Delogo }, slide);

        await Runner(new RecordingRunner()).RunAsync(request, null, Ct);

        await AssertWatermarkAsync(request.OutputPath, 0, 0, removed: true);
        await AssertWatermarkAsync(request.OutputPath, 60, 60, removed: false);    // 150px 내려갔다
        await AssertWatermarkAsync(request.OutputPath, 150, 150, removed: false);
    }

    [Fact]
    public async Task Delogo_Follows_A_Box_Sliding_Off_The_Frame()
    {
        TestTools.RequireFfmpeg();
        // 워터마크 자리에서 출발해 2초 뒤에는 화면 오른쪽 밖에 있다. 밖의 자리를 빼 버리면 박스가 제자리에 머물러 끝까지 지운다.
        var slide = new BoxTrack { Keys = [new BoxKey(0, Box), new BoxKey(2, new PixelRect(W + 40, Box.Y, Box.Width, Box.Height))] };
        var request = Request("화면 밖으로 나가는 박스 delogo.mp4", new JobOptions { Mode = CleanMode.Delogo, DurationSeconds = 4 }, slide);

        await Runner(new RecordingRunner()).RunAsync(request, null, Ct);

        Assert.Equal(120, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        await AssertWatermarkAsync(request.OutputPath, 0, 0, removed: true);
        await AssertWatermarkAsync(request.OutputPath, 50, 50, removed: false);    // 박스의 왼쪽 변이 워터마크를 지나갔다
        await AssertWatermarkAsync(request.OutputPath, 110, 110, removed: false);  // 화면 밖
    }

    [Fact]
    public async Task Delogo_Counts_Frames_From_The_Range_Start()
    {
        TestTools.RequireFfmpeg();
        // 1.98초 = 59.34프레임 → 결과의 0번이 원본의 60번. 박스는 원본 90번에서 건너뛴다 → 결과의 30번.
        var request = Request("건너뛰는 박스 delogo 구간.mp4", new JobOptions { Mode = CleanMode.Delogo, StartSeconds = 1.98, DurationSeconds = 2 }, Hop);

        await Runner(new RecordingRunner()).RunAsync(request, null, Ct);

        await AssertWatermarkAsync(request.OutputPath, 29, 89, removed: true);
        await AssertWatermarkAsync(request.OutputPath, 30, 90, removed: false);
    }

    [Fact]
    public async Task Long_Delogo_Filter_Goes_Through_A_File()
    {
        TestTools.RequireFfmpeg();
        // 위치를 80곳 적으면 필터가 명령줄에 적기에는 길다
        var keys = Enumerable.Range(0, 80).Select(i => new BoxKey(i * 0.05, i < 40 ? Box : Far.Offset(i * 3, i))).ToList();
        var request = Request("위치가 많은 박스.mp4", new JobOptions { Mode = CleanMode.Delogo, DurationSeconds = 3 }, new BoxTrack { Keys = keys, Jump = true });
        var runner = new RecordingRunner();

        await Runner(runner).RunAsync(request, null, Ct);

        var call = Assert.Single(runner.Invocations, i => i.Arguments.Contains("-/vf"));
        string file = call.Arguments[call.Arguments.ToList().IndexOf("-/vf") + 1];
        Assert.False(File.Exists(file), "필터 파일이 남았습니다.");
        Assert.DoesNotContain(runner.Invocations, i => i.Arguments.Contains("-vf"));
        // 41번째 위치(2초 = 60번 프레임)부터 다른 데로 간다
        await AssertWatermarkAsync(request.OutputPath, 59, 59, removed: true);
        await AssertWatermarkAsync(request.OutputPath, 60, 60, removed: false);
    }

    [Fact]
    public async Task Delogo_Takes_A_Different_Place_On_Every_Frame()
    {
        TestTools.RequireFfmpeg();
        // 프레임마다 자리를 적은 박스 (150곳, 변마다 149번 바뀐다). 2초까지는 워터마크 위에서 1px씩 떨고, 그 뒤로는 먼 곳에 있다.
        var keys = Enumerable.Range(0, 150)
            .Select(i => new BoxKey(i / 30.0, i < 60 ? Box.Offset(i % 2, i % 2) : Far.Offset(i, i % 2)))
            .ToList();
        var request = Request("프레임마다 다른 자리.mp4", new JobOptions { Mode = CleanMode.Delogo, DurationSeconds = 5 }, new BoxTrack { Keys = keys });
        var runner = new RecordingRunner();

        await Runner(runner).RunAsync(request, null, Ct);

        Assert.Contains(runner.Invocations, i => i.Arguments.Contains("-/vf"));
        Assert.Equal(150, await TestTools.CountFramesAsync(request.OutputPath, Ct));
        await AssertWatermarkAsync(request.OutputPath, 30, 30, removed: true);
        await AssertWatermarkAsync(request.OutputPath, 59, 59, removed: true);
        await AssertWatermarkAsync(request.OutputPath, 60, 60, removed: false);
        await AssertWatermarkAsync(request.OutputPath, 140, 140, removed: false);
    }

    [Fact]
    public async Task Delogo_Covers_The_Whole_Watermark_While_The_Box_Slides_In_From_Outside()
    {
        TestTools.RequireFfmpeg();
        // 박스가 오른쪽 밖에 걸친 자리(40px만 화면 안)에서 2초 동안 400px 왼쪽으로 간다 → 1초(30번 프레임)에 워터마크와 딱 겹친다.
        // 걸친 자리를 먼저 자르고 이으면 그때 박스의 오른쪽 변이 75px 모자라 워터마크 오른쪽이 남는다.
        var slide = new BoxTrack { Keys = [new BoxKey(0, Box.Offset(200, 0)), new BoxKey(2, Box.Offset(-200, 0))] };
        var request = Request("밖에서 들어오는 박스 delogo.mp4", new JobOptions { Mode = CleanMode.Delogo, DurationSeconds = 3 }, slide);

        await Runner(new RecordingRunner()).RunAsync(request, null, Ct);

        var right = new PixelRect(Box.Right - 60, Box.Y + 8, 52, Box.Height - 16);
        double diff = await DiffAsync(fixture.Source, 30, request.OutputPath, 30, right);
        Assert.True(diff > 8, $"워터마크 오른쪽이 남았습니다 (평균 오차 {diff:0.00}).");
        // 첫 프레임에는 화면 안에 든 40px만 지운다 — 워터마크는 그대로다
        await AssertWatermarkAsync(request.OutputPath, 0, 0, removed: false);
    }

    [Fact]
    public async Task ProPainter_Masks_Follow_A_Jumping_Box_And_Skip_Chunks_Without_It()
    {
        TestTools.RequireFfmpegAndPython();
        var options = new JobOptions { ChunkLength = 40, Overlap = 5 };
        var request = Request("건너뛰는 박스 복원.mp4", options, Hop);
        var runner = new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "inpaint" });
        var log = new List<string>();

        var result = await Runner(runner, log).RunAsync(request, null, Ct);

        Assert.Equal(fixture.Frames, result.FrameCount);
        Assert.Equal(fixture.Frames, await TestTools.CountFramesAsync(request.OutputPath, Ct));

        // 영역 둘(워터마크 자리, 옮겨 간 자리) × 조각. 박스가 없는 조각은 복원하지 않는다.
        var chunks = ChunkPlanner.Plan(fixture.Frames, 40, 5);
        int expected = chunks.Count(c => c.Start < HopFrame) + chunks.Count(c => c.End > HopFrame);
        Assert.True(expected < chunks.Count * 2);
        Assert.Equal(expected, runner.ProPainterCalls.Count);
        Assert.All(runner.ProPainterCalls, call =>
        {
            string mask = call.Arguments[call.Arguments.ToList().IndexOf("-m") + 1];
            Assert.False(mask.EndsWith(".png", StringComparison.OrdinalIgnoreCase), mask);   // 프레임별 마스크 폴더
            Assert.True(mask.All(char.IsAscii) && !Path.IsPathRooted(mask), mask);
        });
        Assert.Contains(log, line => line.Contains("건너뛰었습니다"));
        Assert.Contains(log, line => line.Contains("움직이는 워터마크"));

        await AssertWatermarkAsync(request.OutputPath, 20, 20, removed: true);
        await AssertWatermarkAsync(request.OutputPath, HopFrame - 1, HopFrame - 1, removed: true);
        await AssertWatermarkAsync(request.OutputPath, HopFrame, HopFrame, removed: false);
        await AssertWatermarkAsync(request.OutputPath, 170, 170, removed: false);
        int reach = MaskBuilder.AlphaReach(2, 4) + 2;
        await AssertOutsideUntouchedAsync(request.OutputPath, Box.Inflate(reach), Far.Inflate(reach));

        Assert.False(Directory.Exists(Path.Combine(TestTools.MockProPainterDir, JobRunner.ProPainterScratch)));
    }

    [Fact]
    public async Task ProPainter_Counts_Frames_From_The_Range_Start()
    {
        TestTools.RequireFfmpegAndPython();
        // 1.98초 = 59.34프레임: 결과의 0번·1번이 원본의 60번, 그 뒤로 한 장씩 → 원본 90번(박스가 건너뛰는 프레임)은 결과의 31번.
        var options = new JobOptions { StartSeconds = 1.98, DurationSeconds = 2, ChunkLength = 25, Overlap = 5 };
        var request = Request("건너뛰는 박스 복원 구간.mp4", options, Hop);

        await Runner(new RecordingRunner(new() { ["NEKOVUP_MOCK_MODE"] = "inpaint" })).RunAsync(request, null, Ct);

        await AssertWatermarkAsync(request.OutputPath, 30, 89, removed: true);
        await AssertWatermarkAsync(request.OutputPath, 31, 90, removed: false);
    }
}
