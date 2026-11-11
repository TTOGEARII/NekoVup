using System.Diagnostics;
using System.Text;
using NekoVup.Core.Imaging;
using NekoVup.Core.Processes;
using NekoVup.Core.Runtime;

namespace NekoVup.Tests;

public class UpdateSplashTests
{
    private static UpdateSplashOptions Options(string dir, int oldPid, string imageName) => new()
    {
        ImagePath = Path.Combine(dir, "splash.png"),
        VersionFile = Path.Combine(dir, "sq.version"),
        TargetVersion = "1.1.0",
        FromVersion = "1.0.2",
        ImageName = imageName,
        OldProcessId = oldPid,
        LogFile = Path.Combine(dir, "update.log"),
        TimeoutSeconds = 60,
        SlowHintSeconds = 1000,
        DoneCloseSeconds = 1,
        PollMilliseconds = 200,
    };

    [Fact]
    public void Script_Quotes_Paths_And_Carries_Marker_Process_And_Versions()
    {
        string script = UpdateSplash.BuildScript(Options(@"C:\O'Neil’s\임시", 1234, "NekoVup"));

        // 작은따옴표·굽은 따옴표는 두 번 — 안 그러면 스크립트가 깨져 창이 안 뜬다
        Assert.Contains(@"'C:\O''Neil’’s\임시\splash.png'", script);
        Assert.Contains(@"'C:\O''Neil’’s\임시\sq.version'", script);
        Assert.Contains("$marker = '<version>1.1.0</version>'", script);
        Assert.Contains("$oldPid = 1234", script);
        Assert.Contains("$imageName = 'NekoVup'", script);
        Assert.Contains("v1.0.2  →  v1.1.0", script);
        // 그림을 눌러 닫던 것은 없앴다 — 적용 중에 창이 사라지면 사용자가 바로 가기로 프로그램을 열어 오류를 본다
        Assert.DoesNotContain("$box.Add_Click", script);
        // 새 버전이 "열린" 것은 창이 있는 프로세스로 본다 (옛 프로세스와 안내 창 자신은 뺀다)
        Assert.Contains("$_.Id -ne $oldPid -and $_.Id -ne $PID -and $_.MainWindowHandle -ne 0", script);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Neko Vup")]
    [InlineData("a'b")]
    public void Process_Name_With_Characters_A_Name_Cannot_Have_Is_Refused(string imageName)
    {
        Assert.Throws<ArgumentException>(() => UpdateSplash.BuildScript(Options(@"C:\x", 1, imageName)));
    }

    /// <summary>
    /// 안내 창을 실제로 띄워 단계를 따라가는지 본다: 옛 프로그램(ping)이 끝나고 → 버전 파일에 새 버전이 적히고 →
    /// 새 프로그램(창이 있는 프로세스)이 뜨면 "끝"으로 바뀌고 스스로 닫힌다. 새 프로그램은 powershell.exe를 다른 이름으로 복사해 창 하나를 띄운 것이다.
    /// </summary>
    [Fact]
    public async Task Window_Follows_Closing_Applying_Launching_Done_And_Closes_Itself()
    {
        Assert.SkipUnless(Environment.UserInteractive, "창을 띄울 수 있는 세션이 아닙니다.");
        var ct = TestContext.Current.CancellationToken;
        string dir = TestTools.NewTempDir("안내 창 O'Neil’s ทดสอบ");
        Process? fakeApp = null;
        try
        {
            PngCodec.Write(Path.Combine(dir, "splash.png"), new byte[16 * 10 * 3], 16, 10, 3);
            string versionFile = Path.Combine(dir, "sq.version");
            File.WriteAllText(versionFile, "<version>1.0.2</version>");

            // 닫히는 중인 옛 프로그램 흉내: 5초쯤 뒤에 끝나는 ping
            string system = Environment.SystemDirectory;
            using var old = Process.Start(new ProcessStartInfo(Path.Combine(system, "ping.exe"), "-n 6 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!;

            const string FakeName = "NekoVupFakeApp";
            string fakeExe = Path.Combine(dir, FakeName + ".exe");
            string powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
            File.Copy(powershell, fakeExe);

            string script = Path.Combine(dir, "splash.ps1");
            File.WriteAllText(script, UpdateSplash.BuildScript(Options(dir, old.Id, FakeName)), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var splash = TestTools.Runner.RunAsync(
                new ToolInvocation(powershell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-File", script]) { WorkingDirectory = dir },
                null, null, ct);

            await Task.Delay(7000, ct); // ping이 끝남: 닫는 중 → 적용 중
            File.WriteAllText(versionFile, "<version>1.1.0</version>");
            await Task.Delay(1500, ct); // 적용됨: 다시 여는 중
            fakeApp = Process.Start(new ProcessStartInfo(fakeExe)
            {
                UseShellExecute = false,
                ArgumentList = { "-NoProfile", "-STA", "-Command", "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; $f.Text = 'fake'; [void]$f.ShowDialog()" },
            });
            await splash; // 새 프로그램의 창을 보고 "끝" → 1초 뒤 스스로 닫힌다

            string log = File.ReadAllText(Path.Combine(dir, "update.log"));
            int closing = log.IndexOf(UpdateSplash.Closing, StringComparison.Ordinal);
            int applying = log.IndexOf(UpdateSplash.Applying, StringComparison.Ordinal);
            int launching = log.IndexOf(UpdateSplash.Launching, StringComparison.Ordinal);
            int done = log.IndexOf(UpdateSplash.Done, StringComparison.Ordinal);
            Assert.True(closing >= 0 && applying > closing && launching > applying && done > launching, log);
            Assert.Contains($"닫힘 ({UpdateSplash.Done})", log);
            Assert.DoesNotContain(UpdateSplash.Timeout, log);
            Assert.DoesNotContain(UpdateSplash.Slow, log);
        }
        finally
        {
            try
            {
                if (fakeApp is { HasExited: false }) fakeApp.Kill();
            }
            catch (InvalidOperationException) { }
            fakeApp?.Dispose();
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Window_Reports_Timeout_When_Nothing_Happens_And_Stays_Open_Until_Closed()
    {
        Assert.SkipUnless(Environment.UserInteractive, "창을 띄울 수 있는 세션이 아닙니다.");
        var ct = TestContext.Current.CancellationToken;
        string dir = TestTools.NewTempDir("안내 창 시간 초과");
        try
        {
            PngCodec.Write(Path.Combine(dir, "splash.png"), new byte[16 * 10 * 3], 16, 10, 3);
            // 옛 프로그램은 이미 없고(그 번호의 프로세스가 없다), 버전 파일은 끝내 바뀌지 않는다
            var options = Options(dir, 1, "NekoVupAbsent") with { TimeoutSeconds = 2 };
            string script = Path.Combine(dir, "splash.ps1");
            File.WriteAllText(script, UpdateSplash.BuildScript(options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var splash = TestTools.Runner.RunAsync(
                new ToolInvocation(powershell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-File", script]) { WorkingDirectory = dir },
                null, null, cancel.Token);
            await Task.Delay(6000, ct);

            string log = File.ReadAllText(Path.Combine(dir, "update.log"));
            Assert.Contains(UpdateSplash.Applying, log);
            Assert.Contains(UpdateSplash.Timeout, log);
            // 시간이 지나도 스스로 닫히지 않는다 — 안내가 남아 있어야 사용자가 무엇을 할지 안다
            Assert.DoesNotContain("닫힘", log);
            Assert.False(splash.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => splash);
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }
}
