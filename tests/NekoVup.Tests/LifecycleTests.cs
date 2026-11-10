using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;
using NekoVup.Core.Regions;
using NekoVup.Core.Runtime;
using NekoVup.Core.Settings;

namespace NekoVup.Tests;

/// <summary>설치부터 제거까지: 준비가 끊긴 폴더를 알아보는지, 남의 폴더를 건드리지 않는지, 제거 때 우리가 만든 것만 지우는지.</summary>
public class LifecycleTests
{
    private static ProPainterSetup NewSetup(HttpClient http) => new(TestTools.Runner, http, NullJobLog.Instance);

    [Fact]
    public void Interrupted_Setup_Is_Still_Recognised_As_Ours()
    {
        string dir = TestTools.NewTempDir("끊긴 준비");
        try
        {
            // 준비 중 표시만 있는 폴더 (이름이 달라도 — 명령줄에서 --dir로 준 곳)
            string marked = Path.Combine(dir, "custom");
            Directory.CreateDirectory(ProPainterSetup.DownloadDir(marked));
            File.WriteAllText(Path.Combine(marked, ProPainterSetup.SetupMarkerFileName), ProPainterSetup.Version);
            Assert.True(ProPainterSetup.IsOurRuntime(marked));
            Assert.False(ProPainterSetup.IsInstalled(marked));
            // 설정이 그 폴더의 파이썬을 가리키면(다시 준비하다 끊긴 것) 설정에서도 우리 것으로 알아본다 — 제거 때 찾아서 지우려면.
            var interrupted = new AppSettings { Python = ProPainterSetup.PythonExe(marked), ProPainterDir = ProPainterSetup.ProPainterDir(marked) };
            Assert.Equal(Path.GetFullPath(marked), ProPainterSetup.RootOf(interrupted));
            Assert.False(ProPainterSetup.IsUserConfigured(interrupted));
            Assert.Contains(Path.GetFullPath(marked), UninstallCleanup.FindRuntimeRoots(interrupted), StringComparer.OrdinalIgnoreCase);

            // 드라이브 바로 아래는 표시 파일이 있든 없든 우리 것이 아니다 (제거 때 X:\python, X:\downloads를 지우면 안 된다)
            string drive = Path.GetPathRoot(Path.GetFullPath(dir))!;
            Assert.False(ProPainterSetup.IsOurRuntime(drive));
            Assert.Null(ProPainterSetup.RootOf(new AppSettings { Python = Path.Combine(drive, "python", "python.exe") }));

            // 표시 파일이 없어도 우리 이름의 폴더에 우리가 만든 것만 있으면 우리 것 (옛 판이 받다 만 폴더)
            string named = Path.Combine(dir, ProPainterSetup.RootFolderName);
            Directory.CreateDirectory(ProPainterSetup.DownloadDir(named));
            Assert.True(ProPainterSetup.IsOurRuntime(named));

            // 다른 것이 섞여 있으면 아니다
            File.WriteAllText(Path.Combine(named, "내 파일.txt"), "");
            Assert.False(ProPainterSetup.IsOurRuntime(named));
            Assert.False(ProPainterSetup.HasOnlyOwnedEntries(named));

            // 빈 폴더, 다른 이름의 폴더는 아니다
            string empty = Path.Combine(dir, "empty", ProPainterSetup.RootFolderName);
            Directory.CreateDirectory(empty);
            Assert.False(ProPainterSetup.IsOurRuntime(empty));
            Assert.True(ProPainterSetup.HasOnlyOwnedEntries(empty));
            string other = Path.Combine(dir, "other");
            Directory.CreateDirectory(ProPainterSetup.DownloadDir(other));
            Assert.False(ProPainterSetup.IsOurRuntime(other));
            Assert.False(ProPainterSetup.IsOurRuntime(Path.Combine(dir, "없는 폴더")));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Setup_Refuses_Drive_Roots_And_Folders_With_Other_Files()
    {
        string dir = TestTools.NewTempDir("남의 폴더");
        try
        {
            using var http = new HttpClient(new NoNetworkHandler());
            var setup = NewSetup(http);
            var ct = TestContext.Current.CancellationToken;

            // 드라이브 바로 아래: 준비하면 X:\python, X:\ProPainter를 지우게 된다
            var ex = await Assert.ThrowsAsync<JobException>(() => setup.InstallAsync(Path.GetPathRoot(Environment.SystemDirectory)!, null, ct));
            Assert.Contains("드라이브", ex.Message);

            // 사용자의 도구 폴더: 같은 이름(python, ProPainter)의 폴더가 있어도 지워지면 안 된다
            string tools = Path.Combine(dir, "tools");
            Directory.CreateDirectory(Path.Combine(tools, "ProPainter", "weights"));
            File.WriteAllText(Path.Combine(tools, "ProPainter", "weights", "mine.pth"), "x");
            ex = await Assert.ThrowsAsync<JobException>(() => setup.InstallAsync(tools, null, ct));
            Assert.Contains("다른 파일", ex.Message);
            Directory.CreateDirectory(Path.Combine(tools, "video2x"));
            ex = await Assert.ThrowsAsync<JobException>(() => setup.InstallAsync(tools, null, ct));
            Assert.Contains("다른 파일", ex.Message);
            Assert.True(File.Exists(Path.Combine(tools, "ProPainter", "weights", "mine.pth")));
            Assert.False(File.Exists(Path.Combine(tools, ProPainterSetup.SetupMarkerFileName)));

            // 우리 이름의 폴더는 사용자가 파일을 넣어 두었어도 받는다: 제거하면 그 파일 때문에 폴더가 남는데, 다시 설치했을 때 준비가 막히면 안 된다
            string ours = Path.Combine(dir, ProPainterSetup.RootFolderName);
            Directory.CreateDirectory(ours);
            File.WriteAllText(Path.Combine(ours, "메모.txt"), "x");
            Assert.False(ProPainterSetup.IsOurRuntime(ours));
            ProPainterSetup.CheckTarget(ours);
            ProPainterSetup.CheckTarget(Path.Combine(dir, "아직 없는 폴더"));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Second_Setup_On_The_Same_Folder_Is_Refused_Without_Touching_It()
    {
        string dir = TestTools.NewTempDir("겹친 준비");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            Assert.SkipUnless(drive.AvailableFreeSpace > ProPainterSetup.RequiredBytes, "남은 공간이 모자라 공간 확인에서 먼저 멈춥니다.");

            string root = Path.Combine(dir, ProPainterSetup.RootFolderName);
            Directory.CreateDirectory(ProPainterSetup.PythonDir(root));
            File.WriteAllText(ProPainterSetup.PythonExe(root), "쓰는 중");
            File.WriteAllText(Path.Combine(root, ProPainterSetup.MarkerFileName), ProPainterSetup.Version);

            // 다른 창이 준비 중: 표시 파일을 열어 두고 있다
            using (new FileStream(Path.Combine(root, ProPainterSetup.SetupMarkerFileName), FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                using var http = new HttpClient(new NoNetworkHandler());
                var ex = await Assert.ThrowsAsync<JobException>(() => NewSetup(http).InstallAsync(root, null, TestContext.Current.CancellationToken));
                Assert.Contains("다른 창", ex.Message);
            }
            Assert.True(File.Exists(ProPainterSetup.PythonExe(root)));
            Assert.True(File.Exists(Path.Combine(root, ProPainterSetup.MarkerFileName))); // 끝남 표시도 그대로
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Setup_Waits_For_Jobs_Using_The_Runtime_And_Jobs_Wait_For_Setup()
    {
        string dir = TestTools.NewTempDir("쓰는 중 준비");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            Assert.SkipUnless(drive.AvailableFreeSpace > ProPainterSetup.RequiredBytes, "남은 공간이 모자라 공간 확인에서 먼저 멈춥니다.");

            string root = Path.Combine(dir, ProPainterSetup.RootFolderName);
            Directory.CreateDirectory(ProPainterSetup.PythonDir(root));
            File.WriteAllText(ProPainterSetup.PythonExe(root), "쓰는 중");
            File.WriteAllText(Path.Combine(root, ProPainterSetup.MarkerFileName), ProPainterSetup.Version);

            // 다른 창 두 곳에서 이 파이썬으로 작업 중이다 (작업끼리는 함께 잡을 수 있다). 다시 준비하면 쓰던 파이썬을 지운다.
            using (ProPainterSetup.HoldForUse(root))
            using (ProPainterSetup.HoldForUse(root))
            {
                using var http = new HttpClient(new NoNetworkHandler());
                var ex = await Assert.ThrowsAsync<JobException>(() => NewSetup(http).InstallAsync(root, null, TestContext.Current.CancellationToken));
                Assert.Contains("쓰거나 준비하고", ex.Message);
            }
            Assert.True(File.Exists(ProPainterSetup.PythonExe(root)));
            Assert.True(File.Exists(Path.Combine(root, ProPainterSetup.MarkerFileName)));
            Assert.False(File.Exists(Path.Combine(root, ProPainterSetup.SetupMarkerFileName)));

            // 준비가 잡고 있는 동안에는 작업이 시작하지 못한다
            using (new FileStream(Path.Combine(root, ProPainterSetup.InUseFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.Contains("준비하고 있습니다", Assert.Throws<JobException>(() => ProPainterSetup.HoldForUse(root)).Message);
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Job_Refuses_A_Runtime_Whose_Setup_Did_Not_Finish()
    {
        // 다시 준비하다 중지·실패하면 파이썬이 지워진 채 설정은 그대로 남는다. 예전에는 영상 전체를 추출한 뒤 첫 조각에서야 실패했다.
        string dir = TestTools.NewTempDir("끝나지 않은 준비");
        try
        {
            string root = Path.Combine(dir, ProPainterSetup.RootFolderName);
            Directory.CreateDirectory(ProPainterSetup.PythonDir(root));
            File.WriteAllText(Path.Combine(root, ProPainterSetup.SetupMarkerFileName), ProPainterSetup.Version);
            var settings = new AppSettings { Python = ProPainterSetup.PythonExe(root), ProPainterDir = ProPainterSetup.ProPainterDir(root) };
            var runner = new RecordingRunner();
            var request = new JobRequest
            {
                InputPath = Path.Combine(dir, "입력.mp4"),
                OutputPath = Path.Combine(dir, "출력.mp4"),
                Boxes = [new PixelRect(10, 10, 40, 20)],
                Options = new JobOptions(),
            };

            var ex = await Assert.ThrowsAsync<JobException>(() =>
                new JobRunner(settings, runner, NullJobLog.Instance).RunAsync(request, null, TestContext.Current.CancellationToken));

            Assert.Contains("준비가 끝나지 않았습니다", ex.Message);
            Assert.DoesNotContain('\n', ex.Message); // 앱 알림 줄은 첫 줄만 보인다 — 할 일까지 한 줄에
            Assert.Empty(runner.Invocations);

            // 준비는 끝났는데 드라이브를 뽑아 둔 것: 다시 준비하라고 하면 다른 드라이브에 3 GB를 또 받는다 — 연결하라고 알린다
            string missing = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").First(d => !Directory.Exists(d));
            string unplugged = Path.Combine(missing, ProPainterSetup.RootFolderName);
            var away = new AppSettings { Python = ProPainterSetup.PythonExe(unplugged), ProPainterDir = ProPainterSetup.ProPainterDir(unplugged) };
            Assert.Equal(missing, ProPainterSetup.MissingDrive(away));
            Assert.Null(ProPainterSetup.MissingDrive(settings));
            Assert.Null(ProPainterSetup.MissingDrive(new AppSettings { Python = "python" }));
            ex = await Assert.ThrowsAsync<JobException>(() =>
                new JobRunner(away, runner, NullJobLog.Instance).RunAsync(request, null, TestContext.Current.CancellationToken));
            Assert.Contains("연결한 뒤", ex.Message);
            // 명령줄 setup --dir로 다른 이름을 준 환경도 (드라이브가 없으면 우리 것인지 알 수 없지만) 같은 안내
            string custom = Path.Combine(missing, "pp");
            var customAway = new AppSettings { Python = ProPainterSetup.PythonExe(custom), ProPainterDir = ProPainterSetup.ProPainterDir(custom) };
            Assert.Null(ProPainterSetup.RootOf(customAway));
            ex = await Assert.ThrowsAsync<JobException>(() =>
                new JobRunner(customAway, runner, NullJobLog.Instance).RunAsync(request, null, TestContext.Current.CancellationToken));
            Assert.Contains("연결한 뒤", ex.Message);
            Assert.Empty(runner.Invocations);
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Resolve_Root_Skips_Folders_That_Are_Not_Ours()
    {
        // 설정이 가리키는 곳이 없으면 고정 드라이브의 X:\NekoVup-runtime 가운데 우리가 만든 것만 고른다 — 고른 것은 반드시 우리 것이거나 기본 위치다.
        string root = ProPainterSetup.ResolveRoot(new AppSettings());
        Assert.True(ProPainterSetup.IsOurRuntime(root) || root == ProPainterSetup.DefaultRoot);
    }

    [Fact]
    public void Delete_Command_Only_Takes_Absolute_Paths_Below_A_Drive()
    {
        string drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Null(UninstallCleanup.BuildDeleteCommand([], [], null));
        Assert.Null(UninstallCleanup.BuildDeleteCommand([drive, drive.TrimEnd('\\'), "relative", "", "C:\\a\"b"], [], drive));

        var plan = UninstallCleanup.BuildDeleteCommand(
            [@"D:\NekoVup-runtime\python\", drive, @"D:\50%\x!y!"], [@"D:\NekoVup-runtime\propainter.ok"], @"D:\NekoVup-runtime");
        Assert.NotNull(plan);
        // 경로는 명령 줄에 적지 않는다 (cmd가 경로 안의 %이름%·%i를 바꿔 버린다) — 환경 변수로 넘기고 "!이름!"으로 읽는다.
        Assert.DoesNotContain(":\\", plan.Command);
        Assert.StartsWith("for /l %i in (1,1,", plan.Command); // 잠긴 파일이 풀릴 때까지 다시 시도
        Assert.DoesNotContain("%", plan.Command["for /l %i".Length..]);
        string Name(string path) => $"\"!{plan.Variables.Single(v => v.Value == path).Key}!\"";

        Assert.Contains($"rmdir /s /q {Name(@"D:\NekoVup-runtime\python")} ", plan.Command);
        Assert.Contains($"rmdir /s /q {Name(@"D:\50%\x!y!")} ", plan.Command); // %가 든 경로도 그대로 지운다
        Assert.Contains($"del /f /q {Name(@"D:\NekoVup-runtime\propainter.ok")} ", plan.Command);
        Assert.Contains($"& rmdir {Name(@"D:\NekoVup-runtime")} ", plan.Command); // 통째로가 아니라 비어 있을 때만
        Assert.DoesNotContain($"rmdir /s /q {Name(@"D:\NekoVup-runtime")}", plan.Command);
        Assert.DoesNotContain(plan.Variables.Values, v => v.Length <= 3); // 드라이브 바로 아래는 어디에도 없다
        // timeout은 콘솔 없는 곳에서 바로 끝난다. ping은 전체 경로로 — 임시 폴더나 PATH의 다른 ping이 잡히지 않게.
        Assert.Contains($"{Name(Path.Combine(Environment.SystemDirectory, "ping.exe"))} -n 4 ", plan.Command);
    }

    [Fact]
    public async Task Delete_Later_Takes_Names_The_Command_Line_Would_Rewrite()
    {
        string dir = TestTools.NewTempDir("제거 이름 100%i (괄호)");
        try
        {
            // cmd가 명령 줄에서 바꿔 버리는 글자들: %i(for 변수), %TEMP%, !이름!, ^, &
            string[] targets = ["100%increase 50%i", "bang!x!y", "caret^and&amp", "quote'single", "percent%TEMP%end"];
            // 바뀐 이름이 가리키게 되는 폴더들 — 건드리면 안 된다
            string[] decoys = ["1001ncrease 501", "bangy", "caretand", "percentend", "keep"];
            foreach (string name in targets.Concat(decoys))
            {
                Directory.CreateDirectory(Path.Combine(dir, name, "sub"));
                File.WriteAllText(Path.Combine(dir, name, "sub", "file.txt"), "x");
            }
            string file = Path.Combine(dir, "file 100%i!a!.ok");
            File.WriteAllText(file, "x");

            Assert.True(UninstallCleanup.DeleteLater(targets.Select(t => Path.Combine(dir, t)), [file]));

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && (File.Exists(file) || targets.Any(t => Directory.Exists(Path.Combine(dir, t)))))
                await Task.Delay(250, TestContext.Current.CancellationToken);

            Assert.Equal(decoys.Order(StringComparer.Ordinal), Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.All(decoys, d => Assert.True(File.Exists(Path.Combine(dir, d, "sub", "file.txt"))));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Delete_Later_Removes_Only_What_We_Made()
    {
        string dir = TestTools.NewTempDir("제거 실제 (괄호)");
        try
        {
            // 다른 파일이 섞인 폴더: 우리가 만든 것만 지우고 폴더와 그 파일은 남긴다
            string mixed = Path.Combine(dir, "mixed");
            MakeRuntime(mixed);
            Directory.CreateDirectory(Path.Combine(mixed, "video2x"));
            File.WriteAllText(Path.Combine(mixed, "내 메모.txt"), "남아야 한다");
            // 명령줄 --dir로 준 폴더에 사용자가 나중에 둔 흔한 이름의 폴더 (예전에는 우리 내려받기 폴더 이름과 같아 함께 지워졌다)
            Directory.CreateDirectory(Path.Combine(mixed, "downloads"));
            File.WriteAllText(Path.Combine(mixed, "downloads", "모델.bin"), "남아야 한다");
            // 우리 것만 있는 폴더: 폴더까지 사라진다
            string clean = Path.Combine(dir, ProPainterSetup.RootFolderName);
            MakeRuntime(clean);

            foreach (string root in (string[])[mixed, clean])
                UninstallCleanup.DeleteLater(
                    ProPainterSetup.OwnedDirectories.Select(d => Path.Combine(root, d)),
                    ProPainterSetup.OwnedFiles.Select(f => Path.Combine(root, f)),
                    removeIfEmpty: root);

            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && (Directory.Exists(clean) || Directory.Exists(Path.Combine(mixed, "python"))))
                await Task.Delay(250, TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(clean));
            Assert.Equal(["downloads", "video2x", "내 메모.txt"], Directory.EnumerateFileSystemEntries(mixed).Select(Path.GetFileName).Order());
            Assert.True(File.Exists(Path.Combine(mixed, "downloads", "모델.bin")));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }

        static void MakeRuntime(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, "python", "Lib"));
            File.WriteAllText(Path.Combine(root, "python", "python.exe"), "");
            File.WriteAllText(Path.Combine(root, "python", "Lib", "a.py"), "");
            Directory.CreateDirectory(Path.Combine(root, "ProPainter", "weights"));
            Directory.CreateDirectory(Path.Combine(ProPainterSetup.DownloadDir(root), "tmp"));
            File.WriteAllText(Path.Combine(root, ProPainterSetup.MarkerFileName), ProPainterSetup.Version);
            File.WriteAllText(Path.Combine(root, ProPainterSetup.SetupMarkerFileName), ProPainterSetup.Version);
        }
    }

    [Fact]
    public void Fallback_Work_Root_Prefers_Where_The_Job_Already_Is()
    {
        // 출력 폴더가 OneDrive 안일 때 쓰는 X:\NekoVup-work. 남은 공간만 보면 날마다 바뀌어 끊긴 작업을 이어 하지 못한다.
        string dir = TestTools.NewTempDir("작업 폴더 고르기");
        try
        {
            string c = Path.Combine(dir, "C") + @"\", d = Path.Combine(dir, "D") + @"\", e = Path.Combine(dir, "E") + @"\";
            string[] drives = [c, d, e];
            foreach (string drive in drives) Directory.CreateDirectory(drive);
            long Free(string drive) => drive == e ? 300 : drive == d ? 200 : 100;
            string At(string drive) => Path.Combine(drive, WorkFolders.FallbackFolderName);

            Assert.Equal(At(e), WorkFolders.FallbackRoot("a-1", drives, Free)); // 아무것도 없으면 남은 공간이 가장 큰 곳
            Directory.CreateDirectory(Path.Combine(d, ProPainterSetup.RootFolderName));
            Assert.Equal(At(d), WorkFolders.FallbackRoot("a-1", drives, Free)); // ProPainter 환경이 있는 드라이브
            Directory.CreateDirectory(At(c));
            Assert.Equal(At(c), WorkFolders.FallbackRoot("a-1", drives, Free)); // 이미 쓰고 있는 NekoVup-work
            Directory.CreateDirectory(Path.Combine(At(e), "a-1"));
            Assert.Equal(At(e), WorkFolders.FallbackRoot("a-1", drives, Free)); // 이 작업의 폴더가 있는 곳이 먼저 (이어 하기)
            Assert.Equal(At(c), WorkFolders.FallbackRoot(null, drives, Free));

            // 제거 때 지워도 되는 작업 폴더로 알아본다 (안에 작업 폴더만 있다). 파일이 섞이면 아니다.
            Assert.True(WorkFolders.IsWorkFolder(At(e)));
            File.WriteAllText(Path.Combine(At(e), "내 파일.txt"), "");
            Assert.False(WorkFolders.IsWorkFolder(At(e)));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Work_Folders_Are_Recorded_And_Checked_Before_Removal()
    {
        string dir = TestTools.NewTempDir("작업 폴더");
        try
        {
            string registry = Path.Combine(dir, "data", "workdirs.txt");
            Assert.Empty(WorkFolders.Load(registry));

            // 끝나지 못한 작업의 폴더
            string work = Path.Combine(dir, "출력", WorkFolders.FolderName);
            Directory.CreateDirectory(Path.Combine(work, "clip-0123456789"));
            File.WriteAllText(Path.Combine(work, "clip-0123456789", "job.log"), "");
            WorkFolders.Remember(work + Path.DirectorySeparatorChar, registry);
            WorkFolders.Remember(work, registry); // 같은 폴더는 한 번만
            Assert.Equal([work], WorkFolders.Load(registry));

            // 이미 사라진 폴더는 다음에 적을 때 걸러 낸다
            string gone = Path.Combine(dir, "gone", WorkFolders.FolderName);
            Directory.CreateDirectory(gone);
            WorkFolders.Remember(gone, registry);
            Directory.Delete(gone);
            string other = Path.Combine(dir, "other", WorkFolders.FolderName);
            Directory.CreateDirectory(other);
            WorkFolders.Remember(other, registry);
            Assert.Equal([work, other], WorkFolders.Load(registry));

            // 기록 파일에 엉뚱한 줄이 있어도 .nekovup 폴더(안에 폴더만 있는)만 지운다
            string videos = Path.Combine(dir, "출력");
            string withFile = Path.Combine(dir, "files", WorkFolders.FolderName);
            Directory.CreateDirectory(withFile);
            File.WriteAllText(Path.Combine(withFile, "누군가의 파일.txt"), "");
            File.AppendAllLines(registry, [videos, withFile, "relative\\.nekovup", Path.GetPathRoot(dir)!]);

            // 사용자가 직접 깐 ProPainter 폴더 안의 임시 폴더도 지운다 (폴더 자체는 아니다)
            string propainter = Path.Combine(dir, "ProPainter");
            Directory.CreateDirectory(Path.Combine(propainter, JobRunner.ProPainterScratch, "0123456789"));

            var found = UninstallCleanup.FindWorkFolders(new AppSettings { ProPainterDir = propainter }, registry);
            Assert.Equal([work, other, Path.Combine(propainter, JobRunner.ProPainterScratch)], found);
            Assert.Equal([work, other], UninstallCleanup.FindWorkFolders(new AppSettings(), registry));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Recording_Is_Off_Unless_The_Program_Turns_It_On()
    {
        // 테스트·개발 도구는 사용자 폴더(%AppData%)에 적지 않는다
        Assert.Null(WorkFolders.RegistryPath);
    }

    [Fact]
    public async Task Stalled_Download_Fails_Instead_Of_Hanging()
    {
        string dir = TestTools.NewTempDir("멈춘 내려받기");
        var before = Download.StallTimeout;
        try
        {
            Download.StallTimeout = TimeSpan.FromMilliseconds(300);
            using var http = new HttpClient(new StallingHandler());
            var ct = TestContext.Current.CancellationToken;
            // 응답이 오다가 멈춘다: "취소"가 아니라 시간 초과로 알린다
            await Assert.ThrowsAsync<TimeoutException>(() => Download.ToFileAsync(http, new Uri("http://stall.invalid/file"), Path.Combine(dir, "a.part"), null, ct));

            // 사용자가 취소하면 취소로 끝난다
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Download.StallTimeout = TimeSpan.FromSeconds(30);
            cancel.CancelAfter(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download.ToFileAsync(http, new Uri("http://stall.invalid/file"), Path.Combine(dir, "b.part"), null, cancel.Token));
        }
        finally
        {
            Download.StallTimeout = before;
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Connection_Lost_Mid_Download_Is_Reported_As_A_Setup_Failure()
    {
        string dir = TestTools.NewTempDir("끊긴 내려받기");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            Assert.SkipUnless(drive.AvailableFreeSpace > ProPainterSetup.RequiredBytes, "남은 공간이 모자라 공간 확인에서 먼저 멈춥니다.");

            // 받는 도중에 끊기면 HttpRequestException이 아니라 IOException이 온다 — 그것도 "내려받지 못했습니다"로 알려야 한다
            string root = Path.Combine(dir, ProPainterSetup.RootFolderName);
            using var http = new HttpClient(new StallingHandler(breakInstead: true));
            var ex = await Assert.ThrowsAsync<JobException>(() => NewSetup(http).InstallAsync(root, null, TestContext.Current.CancellationToken));
            Assert.Contains("내려받지 못했습니다", ex.Message);
            Assert.IsType<IOException>(ex.InnerException);
            Assert.True(ProPainterSetup.IsOurRuntime(root)); // 받다 만 폴더는 다음에 이어서 쓴다
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Damaged_Archive_Is_Thrown_Away_So_The_Next_Try_Downloads_Again()
    {
        string dir = TestTools.NewTempDir("손상된 압축");
        try
        {
            string zip = Path.Combine(dir, "ProPainter.zip");
            File.WriteAllText(zip, "받다가 잘린 파일");
            var ex = Assert.Throws<JobException>(() => ProPainterSetup.ExtractOrDiscard(zip, () => ProPainterSetup.ExtractStrippingRoot(zip, Path.Combine(dir, "out"))));
            Assert.Contains("손상", ex.Message);
            Assert.False(File.Exists(zip)); // 남아 있으면 다음에도 "이미 받음"으로 이 파일을 또 쓴다

            // 멀쩡한 압축은 그대로 둔다
            string good = Path.Combine(dir, "good.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(good, System.IO.Compression.ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("ProPainter-abc/inference_propainter.py").Open());
                writer.Write("print('hi')");
            }
            ProPainterSetup.ExtractOrDiscard(good, () => ProPainterSetup.ExtractStrippingRoot(good, Path.Combine(dir, "out")));
            Assert.True(File.Exists(good));
            Assert.True(File.Exists(Path.Combine(dir, "out", "inference_propainter.py")));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Work_Folders_On_An_Unplugged_Drive_Stay_On_Record()
    {
        string? missingDrive = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => $"{(char)c}:\\").LastOrDefault(d => !Directory.Exists(d));
        Assert.SkipWhen(missingDrive is null, "비어 있는 드라이브 문자가 없습니다.");

        string dir = TestTools.NewTempDir("뽑힌 드라이브");
        try
        {
            string registry = Path.Combine(dir, "data", "workdirs.txt");
            string unplugged = Path.Combine(missingDrive!, "영상", WorkFolders.FolderName);
            string gone = Path.Combine(dir, "gone", WorkFolders.FolderName);
            Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
            File.WriteAllLines(registry, [unplugged, gone]);

            string work = Path.Combine(dir, "출력", WorkFolders.FolderName);
            Directory.CreateDirectory(work);
            WorkFolders.Remember(work, registry);

            // 드라이브가 있는데 폴더가 없으면 지운 것이고, 드라이브째 없으면 뽑아 둔 것이다
            Assert.Equal([unplugged, work], WorkFolders.Load(registry));
            Assert.Equal([work], UninstallCleanup.FindWorkFolders(new AppSettings(), registry)); // 지금 없는 것은 지울 목록에 넣지 않는다
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    /// <summary>내려받기 전에 거절해야 하는 테스트용: 거절이 빠져도 진짜 네트워크로 나가 6 GB를 받기 시작하지 않게 한다.</summary>
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("이 테스트는 내려받기까지 가면 안 됩니다: " + request.RequestUri);
    }

    /// <summary>머리와 첫 조각만 주고 그 뒤로는 아무것도 보내지 않는 서버. <paramref name="breakInstead"/>면 멈추는 대신 연결이 끊긴다.</summary>
    private sealed class StallingHandler(bool breakInstead = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(breakInstead)) });
    }

    private sealed class StallingStream(bool breakInstead) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = 1;
                return 1;
            }
            if (breakInstead) throw new IOException("연결이 끊겼습니다.");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}

/// <summary>업데이트 뒤 다시 시작을 지켜보는 스크립트. 실제 cmd로 돌려 본다 — 프로그램을 띄우는 자리에는 파일을 남기는 명령을 넣는다.</summary>
public class RestartWatchdogTests
{
    private static readonly WatchdogTiming Fast = new(ApplyTimeoutSeconds: 3, SettleSeconds: 0, FirstWaitSeconds: 1, MaxLaunches: 3);

    /// <summary>떠 있을 리 없는 프로세스 이름.</summary>
    private const string Absent = "NekoVupAbsent.exe";

    /// <summary>설치 폴더의 버전 파일(sq.version)과 같은 꼴.</summary>
    private static string VersionFile(string version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
          <metadata>
            <id>NekoVup</id>
            <version>{version}</version>
            <mainExe>NekoVup.exe</mainExe>
          </metadata>
        </package>
        """;

    /// <summary>
    /// 감시 스크립트를 폴더에 쓰고 프로그램과 같은 방식(cmd /d /c .\이름, 작업 폴더 = 스크립트가 있는 곳)으로 끝까지 돌린다.
    /// 띄울 때마다 launch-N.txt가 남는다 (N은 스크립트가 세는 횟수).
    /// </summary>
    private static async Task<string[]> RunAsync(string dir, string targetVersion, string imageName, WatchdogTiming timing)
    {
        string launch = $"echo x>\"{RestartWatchdog.Escape(dir)}\\launch-%tries%.txt\"";
        File.WriteAllText(Path.Combine(dir, "watch.cmd"), RestartWatchdog.BuildScript(Path.Combine(dir, "sq.version"), targetVersion, imageName, launch, timing));
        await TestTools.Runner.RunAsync(
            new ToolInvocation(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", @".\watch.cmd"]) { WorkingDirectory = dir },
            null, null, TestContext.Current.CancellationToken);
        return [.. Directory.GetFiles(dir, "launch-*.txt").Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Launches_A_Limited_Number_Of_Times_With_Growing_Waits()
    {
        string dir = TestTools.NewTempDir("감시 100%i (괄호)");
        try
        {
            string versionFile = Path.Combine(dir, "sq.version");
            File.WriteAllText(versionFile, VersionFile("1.0.1"));
            // 적용이 끝나 버전 파일이 바뀌는 것을 흉내 낸다
            var apply = Task.Run(async () =>
            {
                await Task.Delay(1200, TestContext.Current.CancellationToken);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        File.WriteAllText(versionFile, VersionFile("1.0.2"));
                        return;
                    }
                    catch (IOException) when (attempt < 100)
                    {
                        await Task.Delay(20, TestContext.Current.CancellationToken); // 스크립트가 마침 읽고 있다
                    }
                }
            }, TestContext.Current.CancellationToken);

            string[] launches = await RunAsync(dir, "1.0.2", Absent, Fast with { ApplyTimeoutSeconds = 30 });
            await apply;

            // 떠 있지 않으니 띄우고, 그래도 안 뜨면 다시 띄우되 정해진 횟수에서 멈춘다
            Assert.Equal(["launch-0.txt", "launch-1.txt", "launch-2.txt"], launches.Select(Path.GetFileName));
            // 다시 띄우기까지 기다리는 시간은 1초, 2초로 늘어난다 — 뜨자마자 죽는 프로그램을 쉬지 않고 다시 띄우지 않는다
            var times = launches.Select(File.GetLastWriteTimeUtc).ToArray();
            Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(0.7), $"첫 간격: {times[1] - times[0]}");
            Assert.True(times[2] - times[1] >= TimeSpan.FromSeconds(1.7), $"둘째 간격: {times[2] - times[1]}");
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Leaves_A_Running_Program_Alone()
    {
        string running = TestTools.NewTempDir("감시 실행 중");
        string absent = TestTools.NewTempDir("감시 꺼짐");
        try
        {
            foreach (string dir in (string[])[running, absent]) File.WriteAllText(Path.Combine(dir, "sq.version"), VersionFile("1.0.2"));
            // 스크립트를 돌리는 cmd 자신이 떠 있으므로 cmd.exe는 "실행 중"이다
            var whenRunning = RunAsync(running, "1.0.2", "cmd.exe", Fast);
            var whenAbsent = RunAsync(absent, "1.0.2", Absent, Fast with { MaxLaunches = 1 });

            Assert.Empty(await whenRunning);
            Assert.Single(await whenAbsent);
        }
        finally
        {
            TestTools.DeleteDir(running);
            TestTools.DeleteDir(absent);
        }
    }

    [Fact]
    public async Task Does_Not_Launch_Until_Exactly_The_Target_Version_Is_Applied()
    {
        // (깔려 있는 버전, 적용될 버전). 앞부분만 같은 것은 "적용됨"이 아니다 — 적용하는 중에 프로그램을 띄우면 바꾸고 있는 파일을 붙잡는다.
        (string? Installed, string Target)[] cases =
        [
            ("1.0.2-beta.1", "1.0.2"), // 시험판에서 정식판으로
            ("1.0.10", "1.0.1"),
            (null, "1.0.2"), // 버전 파일이 없다
        ];
        string[] dirs = [.. cases.Select((_, i) => TestTools.NewTempDir($"감시 미적용 {i}"))];
        try
        {
            var runs = cases.Select((c, i) =>
            {
                if (c.Installed is { } installed) File.WriteAllText(Path.Combine(dirs[i], "sq.version"), VersionFile(installed));
                return RunAsync(dirs[i], c.Target, Absent, Fast);
            }).ToArray();

            Assert.All(await Task.WhenAll(runs), launches => Assert.Empty(launches));
        }
        finally
        {
            foreach (string dir in dirs) TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Script_Keeps_Paths_Literal_And_Uses_The_Real_Waits()
    {
        string script = RestartWatchdog.BuildScript(@"C:\Users\100%user\AppData\Local\NekoVup\current\sq.version", "1.0.2", "NekoVup.exe",
            RestartWatchdog.LaunchCommand(@"C:\Users\100%user\AppData\Local\NekoVup\NekoVup.exe"));

        // 줄 끝은 모두 CRLF — LF뿐이면 goto가 표지를 못 찾을 때가 있다
        Assert.DoesNotContain('\n', script.Replace("\r\n", ""));
        // 경로의 %는 두 번 적혀 글자 그대로 읽힌다
        Assert.Contains(@" ""C:\Users\100%%user\AppData\Local\NekoVup\current\sq.version"" ", script);
        Assert.Contains(@"start """" ""C:\Users\100%%user\AppData\Local\NekoVup\NekoVup.exe""", script);
        Assert.Contains(@"/c:""<version>1.0.2</version>""", script);
        Assert.Equal("<version>1.0.2-beta.1+build.5</version>", RestartWatchdog.VersionMarker("1.0.2-beta.1+build.5"));
        // 3분 동안 적용을 기다리고, 적용 4초 뒤부터 10초·20초…로 늘려 가며 여섯 번까지 띄운다
        Assert.Contains("if %waited% geq 180 goto end", script);
        Assert.Contains("-n 5 127.0.0.1", script);
        Assert.Contains("set wait=10\r\n", script);
        Assert.Contains("if %tries% geq 6 goto end", script);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0.2 ")]
    [InlineData("1.0.2\" & calc & \"")]
    [InlineData("1.0.2%TEMP%")]
    [InlineData("1.0.2'")]
    [InlineData("1.0.2<")]
    public void Version_With_Characters_No_Version_Has_Is_Not_Put_Into_A_Script(string version)
    {
        Assert.Throws<ArgumentException>(() => RestartWatchdog.VersionMarker(version));
        Assert.Throws<ArgumentException>(() => RestartWatchdog.BuildScript(@"C:\x\sq.version", version, "NekoVup.exe", "rem"));
    }
}
