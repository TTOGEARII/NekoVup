using NekoVup.Core.Pipeline;

namespace NekoVup.Tests;

/// <summary>여러 영상을 차례로 처리하기 전의 확인: 작업끼리 출력이 겹치거나 목록의 원본을 덮어쓰지 않는지.</summary>
public class BatchCheckTests
{
    private static BatchEntry Job(string name, string output) => new(name + ".mp4", @"C:\영상\" + name + ".mp4", output);

    [Fact]
    public void Separate_Outputs_Are_Fine()
    {
        var jobs = new[] { Job("a", @"C:\영상\a [nekoVup].mp4"), Job("b", @"C:\영상\b [nekoVup].mp4") };
        Assert.Null(BatchCheck.FindConflict(jobs, jobs.Select(j => j.InputPath)));
    }

    [Theory]
    [InlineData(@"C:\영상\결과.mp4", @"C:\영상\결과.mp4")]
    [InlineData(@"C:\영상\결과.mp4", @"c:\영상\결과.MP4")]           // 대소문자만 다르다
    [InlineData(@"C:\영상\결과.mp4", @"C:\영상\하위\..\결과.mp4")]    // 돌아서 같은 곳
    public void Two_Jobs_Writing_The_Same_File_Are_Reported(string first, string second)
    {
        var jobs = new[] { Job("a", first), Job("b", second) };
        string? message = BatchCheck.FindConflict(jobs, jobs.Select(j => j.InputPath));
        Assert.NotNull(message);
        Assert.Contains("a.mp4", message);
        Assert.Contains("b.mp4", message);
    }

    [Fact]
    public void Writing_Over_Another_Video_In_The_List_Is_Reported()
    {
        // b는 이번에 처리하지 않지만(이미 끝났다) 목록에 있다 — a의 결과가 b의 원본을 덮어쓰면 안 된다.
        var jobs = new[] { Job("a", @"C:\영상\b.mp4") };
        string? message = BatchCheck.FindConflict(jobs, [@"C:\영상\a.mp4", @"C:\영상\B.mp4"]);
        Assert.NotNull(message);
        Assert.Contains("a.mp4", message);
    }

    [Fact]
    public void The_Same_File_Reached_By_Another_Path_Is_Reported()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nekovup-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "원본.mp4");
            File.WriteAllText(source, "x");
            // 8.3 이름이나 \\?\ 접두사처럼 글자가 다른 경로 — 파일이 있으면 같은 파일인지로 본다.
            var jobs = new[] { new BatchEntry("다른.mp4", Path.Combine(folder, "다른.mp4"), @"\\?\" + source) };
            Assert.NotNull(BatchCheck.FindConflict(jobs, [source]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Empty_Paths_Never_Match()
    {
        var jobs = new[] { Job("a", ""), Job("b", "") };
        Assert.Null(BatchCheck.FindConflict(jobs, [""]));
    }

    [Fact]
    public void MakeUnique_Keeps_A_Free_Name()
    {
        Assert.Equal(@"C:\영상\a [nekoVup].mp4", BatchCheck.MakeUnique(@"C:\영상\a [nekoVup].mp4", [@"C:\영상\b [nekoVup].mp4"]));
    }

    [Fact]
    public void MakeUnique_Numbers_A_Taken_Name()
    {
        // 같은 폴더의 a.mp4와 a.avi는 기본 출력 이름이 같다.
        // 대소문자만 다른 것도 같은 파일이다. 돌려주는 경로는 넘겨받은 글자 그대로 쓴다.
        string[] taken = [@"C:\영상\a [nekoVup].mp4"];
        Assert.Equal(@"c:\영상\A [nekoVup] (2).mp4", BatchCheck.MakeUnique(@"c:\영상\A [nekoVup].mp4", taken));

        string[] more = [@"C:\영상\a [nekoVup].mp4", @"C:\영상\a [nekoVup] (2).mp4"];
        Assert.Equal(@"C:\영상\a [nekoVup] (3).mp4", BatchCheck.MakeUnique(@"C:\영상\a [nekoVup].mp4", more));
    }
}
