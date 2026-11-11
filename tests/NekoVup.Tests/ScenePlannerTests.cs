using NekoVup.Core.Chunks;
using NekoVup.Core.Pipeline;

namespace NekoVup.Tests;

/// <summary>현재 장면 빠른 미리보기: 어느 프레임을 처리하고, 그 가운데 어디가 실제 렌더링과 같은지.</summary>
public class ScenePlannerTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.0, 30)]
    [InlineData(1.001, 31)]   // 프레임 사이의 시각은 바로 뒤 프레임
    [InlineData(0.9999999, 30)] // 반올림 오차는 같은 프레임으로 본다
    public void Time_Maps_To_The_First_Frame_At_Or_After_It(double seconds, long expected) =>
        Assert.Equal(expected, FrameGrid.FrameAt(seconds, 30));

    [Theory]
    [InlineData(30.0)]
    [InlineData(29.97)]
    [InlineData(23.976)]
    [InlineData(60.0)]
    public void Seek_And_Length_Select_Exactly_The_Planned_Frames(double fps)
    {
        foreach (long first in new long[] { 0, 1, 7, 100, 12345 })
        {
            foreach (long count in new long[] { 1, 24, 100 })
            {
                double start = FrameGrid.SeekSeconds(first, fps) ?? 0;
                double end = start + FrameGrid.LengthSeconds(first, count, fps);
                // ffmpeg는 시작 시각이거나 그 뒤의 첫 프레임부터, 끝 시각 앞의 프레임까지 담는다.
                long firstKept = (long)Math.Ceiling(start * fps - 1e-9);
                long endKept = (long)Math.Ceiling(end * fps - 1e-9);
                Assert.Equal(first, firstKept);
                Assert.Equal(count, endKept - firstKept);
                // 프레임 경계에서 1/4 프레임은 떨어져 있어 시각을 여섯 자리로 적어도 프레임이 바뀌지 않는다.
                Assert.True(first == 0 || Math.Abs(start * fps - Math.Round(start * fps)) > 0.2);
            }
        }
        Assert.Null(FrameGrid.SeekSeconds(0, fps));
    }

    [Fact]
    public void Picking_A_Frame_Aims_Between_It_And_The_One_Before()
    {
        Assert.Equal(0, FrameGrid.PickSeconds(0, 30));
        Assert.Equal(9.5 / 30, FrameGrid.PickSeconds(10, 30), 9);
        // 그 시각 뒤의 첫 프레임이 고른 프레임이다
        Assert.Equal(10, FrameGrid.FrameAt(FrameGrid.PickSeconds(10, 30), 30));
    }

    [Fact]
    public void Plain_Modes_Cut_The_Scene_Around_The_Playhead()
    {
        var job = new FrameSpan(0, 900);
        var plan = ScenePlanner.Plan(450, job, 30, proPainter: false, oldVideo: false, 100, 10);
        Assert.Equal(new FrameSpan(435, 30), plan.File);
        Assert.Equal(plan.File, plan.Exact);

        // 처음·끝에서는 영상 안으로 당긴다
        Assert.Equal(new FrameSpan(0, 30), ScenePlanner.Plan(3, job, 30, false, false, 100, 10).File);
        Assert.Equal(new FrameSpan(870, 30), ScenePlanner.Plan(899, job, 30, false, false, 100, 10).File);
        // 영상이 장면보다 짧으면 전부
        Assert.Equal(new FrameSpan(0, 12), ScenePlanner.Plan(5, new FrameSpan(0, 12), 30, false, false, 100, 10).File);
    }

    [Fact]
    public void Plain_Scene_Stays_Inside_A_Range_Job()
    {
        var job = new FrameSpan(300, 60);
        Assert.Equal(new FrameSpan(300, 30), ScenePlanner.Plan(302, job, 30, false, false, 100, 10).File);
        Assert.Equal(new FrameSpan(330, 30), ScenePlanner.Plan(359, job, 30, false, false, 100, 10).File);
    }

    [Theory]
    [InlineData(1000, 100, 10)]
    [InlineData(195, 100, 10)]  // 짧은 꼬리가 앞 조각에 붙는 길이
    [InlineData(205, 100, 10)]
    [InlineData(50, 100, 10)]   // 조각 하나
    [InlineData(333, 40, 20)]
    [InlineData(260, 60, 0)]    // 겹침 없음
    public void ProPainter_Scene_Reproduces_The_Jobs_Chunks(int total, int chunkLength, int overlap)
    {
        const long offset = 500; // 구간 작업: 조각은 구간의 처음부터 센다
        var job = new FrameSpan(offset, total);
        var chunks = ChunkPlanner.Plan(total, chunkLength, overlap);

        for (int p = 0; p < total; p++)
        {
            var plan = ScenePlanner.Plan(offset + p, job, 30, proPainter: true, oldVideo: false, chunkLength, overlap);
            Assert.True(plan.File.Contains(offset + p));
            Assert.True(plan.Exact.Contains(offset + p), $"재생 헤드 {p}는 실제와 같게 나오는 부분에 있어야 한다");
            Assert.True(plan.Exact.First >= plan.File.First && plan.Exact.End <= plan.File.End);

            // 잘라 낸 구간을 다시 나누면 실제 작업의 조각과 같은 조각이 나온다.
            int shift = (int)(plan.File.First - offset);
            var cut = ChunkPlanner.Plan((int)plan.File.Count, chunkLength, overlap)
                .Select(c => (c.Start + shift, c.End + shift)).ToList();
            var real = chunks.Where(c => c.Start >= shift && c.End <= shift + plan.File.Count)
                .Select(c => (c.Start, c.End)).ToList();
            Assert.Equal(real, cut);

            // 실제와 같다고 한 프레임은 미리보기가 처리하지 않은 조각과 섞이지 않는다.
            for (long frame = plan.Exact.First; frame < plan.Exact.End; frame++)
            {
                foreach (var chunk in chunks.Where(c => c.Contains((int)(frame - offset))))
                    Assert.Contains((chunk.Start, chunk.End), real);
            }
        }
    }

    [Fact]
    public void Old_Video_Scene_Starts_One_Chunk_Early_For_Context()
    {
        var job = new FrameSpan(0, 1000);
        // 재생 헤드 100 → 조각 [96, 120). 앞 조각 [72, 96)부터 처리해 문맥을 물려준다.
        var plan = ScenePlanner.Plan(100, job, 30, proPainter: false, oldVideo: true, 100, 10);
        Assert.Equal(new FrameSpan(72, 48), plan.File);
        Assert.Equal(new FrameSpan(96, 24), plan.Exact);
        // 잘라 낸 구간의 처음이 조각 경계(24의 배수)라 잘라 낸 뒤의 조각도 실제와 같은 자리에서 나뉜다
        Assert.Equal(0, plan.File.First % ScenePlanner.OldVideoChunk);

        // 첫 조각은 물려받을 문맥이 없다
        plan = ScenePlanner.Plan(5, job, 30, false, true, 100, 10);
        Assert.Equal(new FrameSpan(0, 24), plan.File);
        Assert.Equal(new FrameSpan(0, 24), plan.Exact);

        // 마지막 조각은 남은 만큼만
        plan = ScenePlanner.Plan(999, job, 30, false, true, 100, 10);
        Assert.Equal(new FrameSpan(960, 40), plan.File);
        Assert.Equal(new FrameSpan(984, 16), plan.Exact);
    }

    [Fact]
    public void Old_Video_Chunks_Are_Counted_From_The_Start_Of_A_Range_Job()
    {
        var plan = ScenePlanner.Plan(310, new FrameSpan(300, 100), 30, proPainter: false, oldVideo: true, 100, 10);
        Assert.Equal(new FrameSpan(300, 24), plan.File);
        plan = ScenePlanner.Plan(330, new FrameSpan(300, 100), 30, false, true, 100, 10);
        Assert.Equal(new FrameSpan(300, 48), plan.File);
        Assert.Equal(new FrameSpan(324, 24), plan.Exact);
    }

    [Fact]
    public void A_Playhead_Outside_The_Job_Is_Pulled_To_Its_Edge()
    {
        var job = new FrameSpan(300, 60);
        Assert.Equal(new FrameSpan(300, 30), ScenePlanner.Plan(10, job, 30, false, false, 100, 10).File);
        Assert.Equal(new FrameSpan(330, 30), ScenePlanner.Plan(5000, job, 30, false, false, 100, 10).File);
    }

    [Fact]
    public void Frame_Span_Clamps_Into_Itself()
    {
        var span = new FrameSpan(10, 5);
        Assert.Equal(15, span.End);
        Assert.True(span.Contains(10));
        Assert.False(span.Contains(15));
        Assert.Equal(10, span.Clamp(3));
        Assert.Equal(14, span.Clamp(99));
        Assert.Equal(12, span.Clamp(12));
    }
}
