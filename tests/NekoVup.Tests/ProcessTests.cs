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
}
