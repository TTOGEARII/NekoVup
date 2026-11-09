using NekoVup.Core.Chunks;
using NekoVup.Core.Regions;

namespace NekoVup.Tests;

public class RegionPlannerTests
{
    private static readonly PixelRect Watermark = new(1040, 40, 190, 56);

    [Fact]
    public void Pads_Clamps_And_Aligns()
    {
        var region = Assert.Single(RegionPlanner.Plan([Watermark], 1280, 720));

        // 여백 96: x 944..1326 → 프레임(1280)에서 잘림, y 0(-56 잘림)..192
        Assert.Equal(new PixelRect(944, 0, 336, 192), region.Rect);
        Assert.Equal(0, region.Rect.Width % 8);
        Assert.Equal(0, region.Rect.Height % 8);
        Assert.Equal(0, region.Rect.X % 2);
        Assert.Equal(0, region.Rect.Y % 2);
        Assert.True(region.Rect.Right <= 1280 && region.Rect.Bottom <= 720);
        Assert.Equal(1.0, region.ResizeRatio);

        // 마스크 박스: 영역 기준 좌표, 사방 2px 키움
        Assert.Equal(new PixelRect(1040 - 944 - 2, 40 - 2, 194, 60), Assert.Single(region.MaskBoxes));
    }

    [Theory]
    [InlineData(101, 33, 50, 30)]
    [InlineData(3, 5, 17, 9)]
    [InlineData(600, 300, 79, 121)]
    [InlineData(0, 0, 1279, 719)]
    public void Region_Always_Contains_Box_And_Is_Aligned(int x, int y, int w, int h)
    {
        var box = new PixelRect(x, y, w, h);
        var region = Assert.Single(RegionPlanner.Plan([box], 1280, 720, new RegionOptions { Pad = 13, MaxArea = long.MaxValue }));

        Assert.True(region.Rect.X <= box.X && region.Rect.Y <= box.Y && region.Rect.Right >= box.Right && region.Rect.Bottom >= box.Bottom);
        Assert.Equal(0, region.Rect.Width % 8);
        Assert.Equal(0, region.Rect.Height % 8);
        Assert.Equal(0, region.Rect.X % 2);
        Assert.Equal(0, region.Rect.Y % 2);
        Assert.True(region.Rect.X >= 0 && region.Rect.Y >= 0 && region.Rect.Right <= 1280 && region.Rect.Bottom <= 720);
    }

    [Fact]
    public void Aligned_Size_Does_Not_Leave_Frame_When_Frame_Is_Not_Multiple_Of_8()
    {
        // 1366x768: 오른쪽 끝에 붙은 박스 → 8의 배수로 올리면 프레임을 넘으므로 왼쪽으로 민다
        var region = Assert.Single(RegionPlanner.Plan([new PixelRect(1300, 700, 60, 60)], 1366, 768, new RegionOptions { Pad = 10 }));
        Assert.True(region.Rect.Right <= 1366);
        Assert.True(region.Rect.Bottom <= 768);
        Assert.Equal(0, region.Rect.Width % 8);
        Assert.Equal(0, region.Rect.X % 2);
        Assert.True(region.Rect.X <= 1300 && region.Rect.Right >= 1360);
    }

    [Fact]
    public void Overlapping_Regions_Merge_And_Separate_Regions_Stay()
    {
        var a = new PixelRect(100, 100, 50, 30);
        var b = new PixelRect(260, 110, 50, 30);   // 여백 96을 붙이면 a와 겹친다
        var c = new PixelRect(1000, 600, 50, 30);  // 멀리 떨어져 있다
        var regions = RegionPlanner.Plan([a, b, c], 1280, 720);

        Assert.Equal(2, regions.Count);
        Assert.Equal(2, regions[0].Boxes.Count);
        Assert.Equal(2, regions[0].MaskBoxes.Count);
        Assert.Single(regions[1].Boxes);
        Assert.False(regions[0].Rect.Intersects(regions[1].Rect));
        Assert.True(regions[0].Rect.X <= a.X - 96 && regions[0].Rect.Right >= b.Right + 96);
    }

    [Fact]
    public void Chain_Of_Merges_Collapses_To_One()
    {
        // a-b, b-c가 겹치고 a-c는 직접 겹치지 않는 경우에도 하나로 합쳐져야 한다
        var regions = RegionPlanner.Plan(
            [new PixelRect(100, 100, 20, 20), new PixelRect(180, 100, 20, 20), new PixelRect(260, 100, 20, 20)],
            1280, 720, new RegionOptions { Pad = 32 });
        Assert.Equal(3, Assert.Single(regions).Boxes.Count);
    }

    [Fact]
    public void Large_Region_Gets_Resize_Ratio()
    {
        var region = Assert.Single(RegionPlanner.Plan([new PixelRect(200, 200, 600, 300)], 1280, 720));
        Assert.True(region.Rect.Area > 300_000);
        Assert.True(region.NeedsResize);
        Assert.InRange(region.ResizeRatio, 0.5, 0.99);
        // 내림했으므로 줄인 면적은 상한을 넘지 않는다
        Assert.True(region.Rect.Area * region.ResizeRatio * region.ResizeRatio <= 300_000);
        Assert.Equal(Math.Sqrt(300_000.0 / region.Rect.Area), region.ResizeRatio, 3);
    }

    [Fact]
    public void Boxes_Outside_Frame_Are_Rejected()
    {
        Assert.Throws<ArgumentException>(() => RegionPlanner.Plan([new PixelRect(2000, 2000, 10, 10)], 1280, 720));
        Assert.Throws<ArgumentException>(() => RegionPlanner.Plan([], 1280, 720));
    }

    [Fact]
    public void Delogo_Keeps_One_Pixel_From_Every_Edge()
    {
        var box = Assert.Single(DelogoPlanner.Plan([new PixelRect(0, 0, 1280, 100)], 1280, 720));
        Assert.Equal(new PixelRect(1, 1, 1278, 99), box);

        var inside = Assert.Single(DelogoPlanner.Plan([Watermark], 1280, 720));
        Assert.Equal(Watermark, inside);
    }

    [Theory]
    [InlineData("10,20,30,40", 10, 20, 30, 40)]
    [InlineData(" 1040, 40 ,190,56 ", 1040, 40, 190, 56)]
    public void PixelRect_Parses(string text, int x, int y, int w, int h)
    {
        Assert.True(PixelRect.TryParse(text, out var rect));
        Assert.Equal(new PixelRect(x, y, w, h), rect);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    public void PixelRect_Rejects_Bad_Text(string text) => Assert.False(PixelRect.TryParse(text, out _));
}

public class ChunkPlannerTests
{
    [Fact]
    public void Short_Video_Is_One_Chunk()
    {
        Assert.Equal([new ChunkRange(0, 0, 100)], ChunkPlanner.Plan(100));
        Assert.Equal([new ChunkRange(0, 0, 7)], ChunkPlanner.Plan(7));
    }

    [Fact]
    public void Chunks_Overlap_By_Ten()
    {
        var chunks = ChunkPlanner.Plan(250);
        Assert.Equal([new ChunkRange(0, 0, 100), new ChunkRange(1, 90, 190), new ChunkRange(2, 180, 250)], chunks);
    }

    [Theory]
    [InlineData(195, 2, 105)] // 마지막 조각 15장 < 20 → 앞 조각에 붙는다
    [InlineData(101, 1, 101)]
    [InlineData(199, 2, 109)] // 19장 → 붙는다
    [InlineData(210, 3, 30)]  // 30장 → 따로 둔다
    [InlineData(200, 3, 20)]  // 정확히 20장 → 따로 둔다
    public void Short_Tail_Is_Merged_Into_Previous(int frames, int expectedCount, int expectedLastLength)
    {
        var chunks = ChunkPlanner.Plan(frames);
        Assert.Equal(expectedCount, chunks.Count);
        Assert.Equal(expectedLastLength, chunks[^1].Length);
        Assert.Equal(frames, chunks[^1].End);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(180)]
    [InlineData(1234)]
    [InlineData(5394)]
    public void Every_Frame_Is_Covered_And_Neighbours_Overlap(int frames)
    {
        var chunks = ChunkPlanner.Plan(frames);
        Assert.Equal(0, chunks[0].Start);
        Assert.Equal(frames, chunks[^1].End);
        for (int i = 0; i < chunks.Count; i++)
        {
            Assert.Equal(i, chunks[i].Index);
            Assert.True(chunks[i].Length >= Math.Min(frames, 20));
            if (i > 0) Assert.Equal(10, chunks[i - 1].End - chunks[i].Start);
        }
    }

    [Fact]
    public void Custom_Chunk_Size()
    {
        var chunks = ChunkPlanner.Plan(180, chunkLength: 60, overlap: 10);
        Assert.Equal([new ChunkRange(0, 0, 60), new ChunkRange(1, 50, 110), new ChunkRange(2, 100, 160), new ChunkRange(3, 150, 180)], chunks);
    }

    [Fact]
    public void Rejects_Bad_Options()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlanner.Plan(0));
        Assert.Throws<ArgumentException>(() => ChunkPlanner.Plan(100, chunkLength: 10, overlap: 6));
    }
}

public class CrossfadeTests
{
    [Fact]
    public void Single_Chunk_Frame_Has_Full_Weight()
    {
        var chunks = ChunkPlanner.Plan(250);
        var (chunk, weight) = Assert.Single(Crossfade.Weights(chunks, 50));
        Assert.Equal(0, chunk.Index);
        Assert.Equal(1f, weight);
    }

    [Fact]
    public void Overlap_Fades_Linearly_From_Old_To_New()
    {
        var chunks = ChunkPlanner.Plan(250); // 조각 0 = [0,100), 조각 1 = [90,190)

        // 겹침 첫 프레임(90): 앞 조각 10, 뒤 조각 1
        var first = Crossfade.Weights(chunks, 90);
        Assert.Equal(2, first.Count);
        Assert.Equal(10f / 11, first[0].Weight, 5);
        Assert.Equal(1f / 11, first[1].Weight, 5);

        // 겹침 마지막 프레임(99): 앞 조각 1, 뒤 조각 10
        var last = Crossfade.Weights(chunks, 99);
        Assert.Equal(1f / 11, last[0].Weight, 5);
        Assert.Equal(10f / 11, last[1].Weight, 5);

        float previous = 1f;
        for (int frame = 90; frame < 100; frame++)
        {
            var w = Crossfade.Weights(chunks, frame);
            Assert.Equal(1f, w.Sum(x => x.Weight), 5);
            Assert.True(w[0].Weight < previous); // 앞 조각 비중은 계속 줄어든다
            previous = w[0].Weight;
        }
    }

    [Fact]
    public void Raw_Weight_Is_Distance_To_Nearest_Chunk_Edge()
    {
        var chunk = new ChunkRange(1, 90, 190);
        Assert.Equal(0, Crossfade.RawWeight(chunk, 89));
        Assert.Equal(1, Crossfade.RawWeight(chunk, 90));
        Assert.Equal(50, Crossfade.RawWeight(chunk, 139));
        Assert.Equal(50, Crossfade.RawWeight(chunk, 140));
        Assert.Equal(1, Crossfade.RawWeight(chunk, 189));
        Assert.Equal(0, Crossfade.RawWeight(chunk, 190));
    }

    [Fact]
    public void Weights_Sum_To_One_For_Every_Frame()
    {
        foreach (int frames in new[] { 1, 100, 195, 250, 1000 })
        {
            var chunks = ChunkPlanner.Plan(frames);
            for (int frame = 0; frame < frames; frame++)
                Assert.Equal(1f, Crossfade.Weights(chunks, frame).Sum(w => w.Weight), 4);
        }
    }
}
