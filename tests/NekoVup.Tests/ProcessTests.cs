using System.Diagnostics;
using NekoVup.Core.Processes;

namespace NekoVup.Tests;

public class ToolRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_Executable_Gives_Clear_Error()
    {
        var ex = await Assert.ThrowsAsync<ToolFailedException>(() =>
            TestTools.Runner.RunAsync(new ToolInvocation("이런-프로그램은-없다.exe", []), null, null, Ct));
        Assert.Contains("이런-프로그램은-없다.exe", ex.Message);
    }

    [Fact]
    public async Task Empty_Executable_Path_Gives_The_Same_Kind_Of_Error()
    {
        // 설정 파일에 "ffprobe": "" 처럼 빈 경로가 적혀 있을 때.
        await Assert.ThrowsAsync<ToolFailedException>(() => TestTools.Runner.RunAsync(new ToolInvocation("", []), null, null, Ct));
        await Assert.ThrowsAsync<ToolFailedException>(() => TestTools.Runner.CaptureBytesAsync(new ToolInvocation("", []), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Failing_Output_Callback_Stops_The_Tool_And_Is_Reported(bool onStandardError)
    {
        // 디스크가 가득 차 로그를 못 쓸 때가 이렇다. 읽기를 그만두면 도구가 가득 찬 파이프에 막혀 영영 끝나지 않는다.
        // (1,0,2)는 끝나지 않는 반복이다 — 도구를 멈춰 주지 않으면 이 테스트는 시간 초과로 실패한다.
        var invocation = new ToolInvocation("cmd.exe", ["/c", "for /l %i in (1,0,2) do @echo line %i" + (onStandardError ? " 1>&2" : "")]);
        int seen = 0;
        void Sink(string line)
        {
            if (Interlocked.Increment(ref seen) == 3) throw new IOException("디스크가 가득 찼습니다");
        }

        var run = TestTools.Runner.RunAsync(invocation, onStandardError ? null : Sink, onStandardError ? Sink : null, Ct);
        var ex = await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.Equal("디스크가 가득 찼습니다", ex.Message);
        Assert.Equal(3, seen); // 실패한 뒤로는 콜백을 다시 부르지 않는다
    }

    [Fact]
    public async Task Captures_Output_Lines_And_Exit_Code()
    {
        var lines = new List<string>();
        var result = await TestTools.Runner.RunAsync(
            new ToolInvocation("cmd.exe", ["/c", "echo first& echo second& exit 7"]),
            line => { lock (lines) lines.Add(line.Trim()); }, null, Ct);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(["first", "second"], lines);
    }

    [Fact]
    public async Task RunChecked_Throws_With_Error_Tail()
    {
        var ex = await Assert.ThrowsAsync<ToolFailedException>(() =>
            TestTools.Runner.RunCheckedAsync(new ToolInvocation("cmd.exe", ["/c", "echo something broke 1>&2& exit 3"]), null, null, Ct));
        Assert.Equal(3, ex.ExitCode);
        Assert.Contains("something broke", ex.Message);
    }

    [Fact]
    public async Task Arguments_With_Spaces_Quotes_And_Korean_Arrive_Intact()
    {
        TestTools.RequireFfmpegAndPython();
        string[] tricky = ["한글 경로\\새 폴더\\영상 (1).mp4", "a \"quoted\" word", "끝에 역슬래시\\", "", "a&b|c>d", "100%"];

        var invocation = new ToolInvocation("python", ["-c", "import sys; [print(a) for a in sys.argv[1:]]", .. tricky])
        {
            Environment = new Dictionary<string, string> { ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8" },
        };
        string output = await TestTools.Runner.RunCaptureAsync(invocation, Ct);

        Assert.Equal(tricky, output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public async Task Environment_And_Working_Directory_Are_Applied()
    {
        string dir = TestTools.NewTempDir("작업 폴더");
        try
        {
            var invocation = new ToolInvocation("cmd.exe", ["/c", "echo %NEKOVUP_TEST_VALUE%& cd"])
            {
                WorkingDirectory = dir,
                Environment = new Dictionary<string, string> { ["NEKOVUP_TEST_VALUE"] = "hello" },
            };
            var lines = new List<string>();
            await TestTools.Runner.RunCheckedAsync(invocation, line => { lock (lines) lines.Add(line.Trim()); }, null, Ct);
            Assert.Equal("hello", lines[0]);
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Cancel_Kills_The_Whole_Process_Tree()
    {
        TestTools.RequireFfmpegAndPython();
        // 부모 파이썬이 자식 파이썬을 띄우고 둘 다 오래 잠든다. 취소하면 둘 다 사라져야 한다.
        const string script =
            "import subprocess, sys, os, time\n" +
            "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])\n" +
            "print(os.getpid(), child.pid, flush=True)\n" +
            "time.sleep(120)\n";

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pids = new TaskCompletionSource<int[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var run = TestTools.Runner.RunAsync(new ToolInvocation("python", ["-c", script]),
            line => pids.TrySetResult([.. line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)]), null, cancel.Token);

        var ids = await pids.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.All(ids, id => Assert.True(TestTools.IsAlive(id)));

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "취소가 바로 듣지 않았습니다.");
        for (int i = 0; i < 50 && ids.Any(TestTools.IsAlive); i++) await Task.Delay(100, Ct);
        Assert.All(ids, id => Assert.False(TestTools.IsAlive(id), $"프로세스 {id}가 남아 있습니다."));
    }

    [Fact]
    public async Task Already_Cancelled_Token_Does_Not_Start_Anything()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TestTools.Runner.RunAsync(new ToolInvocation("cmd.exe", ["/c", "echo hi"]), null, null, cancel.Token));
    }

    [Fact]
    public void Display_Text_Quotes_Only_When_Needed()
    {
        var invocation = new ToolInvocation("ffmpeg", ["-i", @"C:\내 영상\a b.mp4", "-vf", "fps=30"]);
        Assert.Equal("ffmpeg -i \"C:\\내 영상\\a b.mp4\" -vf fps=30", invocation.DisplayText);
    }
}

public class FileLinkerTests
{
    [Fact]
    public void Creates_Hard_Link_Sharing_Content()
    {
        string dir = TestTools.NewTempDir("링크");
        try
        {
            string target = Path.Combine(dir, "원본 프레임.png");
            string link = Path.Combine(dir, "조각", "000001.png");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.WriteAllText(target, "frame data");

            bool linked = FileLinker.LinkOrCopy(target, link);

            Assert.Equal("frame data", File.ReadAllText(link));
            Assert.True(linked, "임시 폴더(NTFS)에서는 하드링크가 만들어져야 합니다.");

            // 링크를 지워도 원본은 남는다
            File.Delete(link);
            Assert.True(File.Exists(target));

            // 이미 있는 자리에 다시 만들어도 된다
            File.WriteAllText(link, "stale");
            FileLinker.LinkOrCopy(target, link);
            Assert.Equal("frame data", File.ReadAllText(link));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Same_File_Is_Recognised_However_It_Is_Spelled()
    {
        string dir = TestTools.NewTempDir("같은 파일");
        string junction = Path.Combine(dir, "정션");
        try
        {
            string folder = Path.Combine(dir, "원본 폴더");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "영상.mkv"), other = Path.Combine(folder, "다른 영상.mkv");
            File.WriteAllText(file, "a");
            File.WriteAllText(other, "a");
            TestTools.MakeJunction(junction, folder);

            Assert.True(FileLinker.SameFile(file, file.ToUpperInvariant()));
            Assert.True(FileLinker.SameFile(file, Path.Combine(junction, "영상.mkv")));   // 다른 폴더 이름을 거친 경로
            Assert.True(FileLinker.SameFile(file, @"\\?\" + file));                       // 접두사만 다른 경로
            Assert.True(FileLinker.SameFile(file, Path.Combine(folder, "..", "원본 폴더", "영상.mkv")));

            Assert.False(FileLinker.SameFile(file, other));                              // 내용이 같아도 다른 파일
            Assert.False(FileLinker.SameFile(file, Path.Combine(junction, "다른 영상.mkv")));
            Assert.False(FileLinker.SameFile(file, Path.Combine(folder, "없는 파일.mkv"))); // 아직 없는 출력
            Assert.False(FileLinker.SameFile(Path.Combine(folder, "없음 1.mkv"), Path.Combine(folder, "없음 2.mkv")));
            Assert.False(FileLinker.SameFile(file, folder));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction); // 정션만 지운다
            TestTools.DeleteDir(dir);
        }
    }
}
