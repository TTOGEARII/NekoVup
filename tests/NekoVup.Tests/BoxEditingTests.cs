using NekoVup.Core.Regions;

namespace NekoVup.Tests;

public class BoxEditingTests
{
    private const int W = 1280, H = 720;
    private static readonly PixelRect Box = new(100, 100, 200, 80);

    [Fact]
    public void Screen_To_Video_Scales_Each_Axis_Separately()
    {
        // 720x480(픽셀이 가로로 긴 DVD 영상)을 853x480 크기로 보여 줄 때: 가로만 늘어난다
        var (x, y) = BoxEditing.ToVideo(426.5, 240, 853, 480, 720, 480);
        Assert.Equal(360, x, 6);
        Assert.Equal(240, y, 6);

        // 1280x720을 절반 크기로 보여 줄 때
        (x, y) = BoxEditing.ToVideo(100, 50, 640, 360, 1280, 720);
        Assert.Equal((200d, 100d), (x, y));
    }

    [Fact]
    public void Drawing_In_Any_Direction_Gives_The_Same_Box()
    {
        var expected = new PixelRect(100, 50, 200, 150);
        Assert.Equal(expected, BoxEditing.FromCorners(100, 50, 300, 200, W, H));
        Assert.Equal(expected, BoxEditing.FromCorners(300, 200, 100, 50, W, H));
        Assert.Equal(expected, BoxEditing.FromCorners(300, 50, 100, 200, W, H));
    }

    [Fact]
    public void Drawing_Is_Clipped_To_Frame_And_Never_Smaller_Than_Minimum()
    {
        Assert.Equal(new PixelRect(1200, 0, 80, 100), BoxEditing.FromCorners(1200, -50, 1500, 100, W, H));
        Assert.Equal(new PixelRect(0, 0, W, H), BoxEditing.FromCorners(-10, -10, 5000, 5000, W, H));

        var tiny = BoxEditing.FromCorners(500, 300, 500.4, 300.2, W, H);
        Assert.Equal((BoxEditing.MinSize, BoxEditing.MinSize), (tiny.Width, tiny.Height));
        var corner = BoxEditing.FromCorners(1280, 720, 1280, 720, W, H);
        Assert.Equal(new PixelRect(W - BoxEditing.MinSize, H - BoxEditing.MinSize, BoxEditing.MinSize, BoxEditing.MinSize), corner);
    }

    [Fact]
    public void Move_Keeps_Size_And_Stops_At_Frame_Edges()
    {
        Assert.Equal(new PixelRect(130, 80, 200, 80), BoxEditing.Drag(Box, BoxGrip.Move, 30, -20, W, H));
        Assert.Equal(new PixelRect(0, 0, 200, 80), BoxEditing.Drag(Box, BoxGrip.Move, -5000, -5000, W, H));
        Assert.Equal(new PixelRect(W - 200, H - 80, 200, 80), BoxEditing.Drag(Box, BoxGrip.Move, 5000, 5000, W, H));
    }

    [Theory]
    [InlineData(BoxGrip.Right, 50, 999, 100, 100, 250, 80)]        // 오른쪽 변만 (세로 움직임은 무시)
    [InlineData(BoxGrip.Left, -40, 999, 60, 100, 240, 80)]
    [InlineData(BoxGrip.Top, 999, -30, 100, 70, 200, 110)]
    [InlineData(BoxGrip.Bottom, 999, 20, 100, 100, 200, 100)]
    [InlineData(BoxGrip.TopLeft, 10, 10, 110, 110, 190, 70)]
    [InlineData(BoxGrip.BottomRight, 30, 40, 100, 100, 230, 120)]
    [InlineData(BoxGrip.TopRight, 20, -20, 100, 80, 220, 100)]
    [InlineData(BoxGrip.BottomLeft, -20, 20, 80, 100, 220, 100)]
    public void Resize_Moves_Only_The_Grabbed_Edges(BoxGrip grip, double dx, double dy, int x, int y, int w, int h) =>
        Assert.Equal(new PixelRect(x, y, w, h), BoxEditing.Drag(Box, grip, dx, dy, W, H));

    [Fact]
    public void Resize_Past_The_Opposite_Edge_Flips_Instead_Of_Going_Negative()
    {
        // 오른쪽 변을 왼쪽 변 너머로 끌면 뒤집힌 박스가 된다
        Assert.Equal(new PixelRect(50, 100, 50, 80), BoxEditing.Drag(Box, BoxGrip.Right, -250, 0, W, H));
        // 딱 겹치게 끌어도 최소 크기는 남는다
        Assert.Equal(BoxEditing.MinSize, BoxEditing.Drag(Box, BoxGrip.Right, -200, 0, W, H).Width);
        // 프레임 밖으로는 못 나간다
        Assert.Equal(W, BoxEditing.Drag(Box, BoxGrip.Right, 9999, 0, W, H).Right);
    }

    [Fact]
    public void Hit_Test_Prefers_Handles_Then_Topmost_Box_Then_Draw()
    {
        var other = new PixelRect(250, 120, 100, 100); // Box와 겹친다, 나중에 그려져 위에 있다
        PixelRect[] boxes = [Box, other];

        Assert.Equal((BoxGrip.Draw, -1), BoxEditing.HitTest(boxes, -1, 600, 600, 5, 5));
        Assert.Equal((BoxGrip.Move, 0), BoxEditing.HitTest(boxes, -1, 150, 110, 5, 5));
        Assert.Equal((BoxGrip.Move, 1), BoxEditing.HitTest(boxes, -1, 280, 150, 5, 5));          // 겹친 곳은 위의 박스
        Assert.Equal((BoxGrip.BottomRight, 0), BoxEditing.HitTest(boxes, 0, 302, 178, 5, 5));    // 선택된 박스의 손잡이가 우선
        Assert.Equal((BoxGrip.Move, 1), BoxEditing.HitTest(boxes, 1, 302, 178, 5, 5));           // 선택이 다르면 그 손잡이는 없다
        Assert.Equal((BoxGrip.TopLeft, 0), BoxEditing.HitTest(boxes, 0, 97, 103, 5, 5));         // 박스 바깥쪽 가까이도 잡힌다
        Assert.Equal((BoxGrip.Top, 0), BoxEditing.HitTest(boxes, 0, 200, 100, 5, 5));
        Assert.Equal((BoxGrip.Left, 0), BoxEditing.HitTest(boxes, 0, 100, 140, 5, 5));
        Assert.Equal((BoxGrip.Draw, -1), BoxEditing.HitTest([], 3, 10, 10, 5, 5));               // 잘못된 선택 번호도 안전
    }

    [Fact]
    public void Eight_Handles_Sit_On_Corners_And_Edge_Midpoints()
    {
        var handles = BoxEditing.Handles(Box);
        Assert.Equal(8, handles.Count);
        Assert.Equal(8, handles.Select(h => h.Grip).Distinct().Count());
        Assert.Contains((BoxGrip.TopLeft, 100d, 100d), handles);
        Assert.Contains((BoxGrip.BottomRight, 300d, 180d), handles);
        Assert.Contains((BoxGrip.Top, 200d, 100d), handles);
        Assert.Contains((BoxGrip.Right, 300d, 140d), handles);
    }
}
