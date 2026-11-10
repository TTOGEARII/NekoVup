using NekoVup.Core.Regions;

namespace NekoVup.Tests;

/// <summary>타임라인 구간 손잡이: 어디를 누르면 잡히는지, 끌면 구간이 어떻게 되는지.</summary>
public class RangeEditingTests
{
    [Theory]
    // 시작 손잡이 [ (x=100): 바깥(왼쪽)으로 3픽셀, 안쪽(오른쪽)으로 꺾인 끝까지 8픽셀
    [InlineData(97, RangeGrip.Start)]
    [InlineData(100, RangeGrip.Start)]
    [InlineData(108, RangeGrip.Start)]
    [InlineData(96, RangeGrip.None)]
    [InlineData(109, RangeGrip.None)]
    // 끝 손잡이 ] (x=300): 안쪽(왼쪽)으로 8픽셀, 바깥(오른쪽)으로 3픽셀
    [InlineData(292, RangeGrip.End)]
    [InlineData(303, RangeGrip.End)]
    [InlineData(291, RangeGrip.None)]
    [InlineData(304, RangeGrip.None)]
    // 구간 가운데와 바깥은 재생 헤드를 옮기는 자리
    [InlineData(200, RangeGrip.None)]
    [InlineData(40, RangeGrip.None)]
    public void Handles_Are_Grabbed_Only_On_Their_Bracket(double x, RangeGrip expected) =>
        Assert.Equal(expected, RangeEditing.HitTest(x, 100, 300));

    [Fact]
    public void Overlapping_Handles_Pick_The_Nearer_One()
    {
        // 아주 짧은 구간(손잡이 사이 2픽셀)에서는 누른 자리에서 가까운 쪽을 잡는다
        Assert.Equal(RangeGrip.Start, RangeEditing.HitTest(99, 100, 102));
        Assert.Equal(RangeGrip.End, RangeEditing.HitTest(103, 100, 102));
        // 길이 0인 구간도 잡을 수 있다 (손잡이가 겹쳐 있다)
        Assert.NotEqual(RangeGrip.None, RangeEditing.HitTest(100, 100, 100));
    }

    [Fact]
    public void Moving_A_Handle_Stays_Inside_The_Video_And_Keeps_The_Minimum_Length()
    {
        Assert.Equal((5.0, 9.0), RangeEditing.Move(RangeGrip.Start, 5, 2, 9, 30, 0.1));
        Assert.Equal((2.0, 20.0), RangeEditing.Move(RangeGrip.End, 20, 2, 9, 30, 0.1));
        // 영상 앞·뒤로 나가지 않는다
        Assert.Equal((0.0, 9.0), RangeEditing.Move(RangeGrip.Start, -3, 2, 9, 30, 0.1));
        Assert.Equal((2.0, 30.0), RangeEditing.Move(RangeGrip.End, 45, 2, 9, 30, 0.1));
        // 시작을 끝 너머로, 끝을 시작 앞으로 끌면 최소 길이에서 멈춘다 (구간이 뒤집히지 않는다)
        var start = RangeEditing.Move(RangeGrip.Start, 12, 2, 9, 30, 0.1);
        Assert.Equal(8.9, start.Start, 9);
        Assert.Equal(9.0, start.End);
        var end = RangeEditing.Move(RangeGrip.End, 1, 2, 9, 30, 0.1);
        Assert.Equal(2.0, end.Start);
        Assert.Equal(2.1, end.End, 9);
        // 손잡이를 잡지 않았으면 그대로
        Assert.Equal((2.0, 9.0), RangeEditing.Move(RangeGrip.None, 20, 2, 9, 30, 0.1));
        // 영상이 최소 길이보다 짧아도 예외 없이 영상 안에 머문다
        var tiny = RangeEditing.Move(RangeGrip.End, 1, 0, 0.05, 0.05, 0.1);
        Assert.Equal(0.05, tiny.End, 9);
    }
}
