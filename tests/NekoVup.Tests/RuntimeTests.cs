using System.IO.Compression;
using NekoVup.Core.Runtime;
using NekoVup.Core.Settings;

namespace NekoVup.Tests;

/// <summary>ProPainter 자동 준비 가운데 네트워크 없이 확인할 수 있는 부분. 실제 내려받기는 `nekovup setup`으로 손수 돌려 본다.</summary>
public class ProPainterSetupTests
{
    [Fact]
    public void Pth_Patch_Enables_Site_Packages_Once()
    {
        const string original = "python311.zip\n.\n\n# Uncomment to run site.main() automatically\n#import site\n";
        string patched = ProPainterSetup.PatchPth(original);

        Assert.Contains("\nimport site", patched);
        Assert.DoesNotContain("#import site", patched);
        Assert.Equal(["python311.zip", ".", ProPainterSetup.ProPainterPthEntry], patched.Split('\n').Take(3)); // 스크립트 폴더는 ._pth에 적어야 보인다
        Assert.Equal(patched, ProPainterSetup.PatchPth(patched)); // 두 번 해도 그대로
        Assert.EndsWith("import site", ProPainterSetup.PatchPth("python311.zip\n.\n")); // 줄이 아예 없으면 덧붙인다
    }

    [Theory]
    [InlineData("     ━━━━━━━━━━━━━━━━╸                 1.2/2.4 GB 10.3 MB/s eta 0:02:00", 0.5, true)]
    [InlineData("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ 24.5/24.5 MB 12.1 MB/s eta 0:00:00", 1.0, true)]
    [InlineData("  Downloading torch-2.5.1+cu121-cp311-cp311-win_amd64.whl (2449.4 MB)", 0, false)]
    [InlineData("Successfully installed torch-2.5.1+cu121", 0, false)]
    public void Pip_Progress_Lines_Give_A_Fraction(string line, double expected, bool parsed)
    {
        Assert.Equal(parsed, ProPainterSetup.TryParsePipProgress(line, out double fraction, out _));
        if (parsed) Assert.Equal(expected, fraction, 2);
    }

    [Fact]
    public void Source_Zip_Is_Extracted_Without_Its_Root_Folder()
    {
        string dir = TestTools.NewTempDir("소스 압축");
        try
        {
            string zip = Path.Combine(dir, "src.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Write(archive, "ProPainter-3267825/", "");
                Write(archive, "ProPainter-3267825/inference_propainter.py", "print('hi')");
                Write(archive, "ProPainter-3267825/model/__init__.py", "");
                Write(archive, "ProPainter-3267825/../escape.txt", "no"); // zip slip은 버린다
            }

            string target = Path.Combine(dir, "ProPainter");
            ProPainterSetup.ExtractStrippingRoot(zip, target);

            Assert.True(File.Exists(Path.Combine(target, "inference_propainter.py")));
            Assert.True(File.Exists(Path.Combine(target, "model", "__init__.py")));
            Assert.False(File.Exists(Path.Combine(dir, "escape.txt")));
            Assert.False(Directory.Exists(Path.Combine(target, "ProPainter-3267825")));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }

        static void Write(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name);
            if (name.EndsWith('/')) return;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
    }

    [Fact]
    public void Installed_Means_Marker_Of_This_Version_Plus_Core_Files()
    {
        string root = TestTools.NewTempDir("런타임");
        try
        {
            Assert.False(ProPainterSetup.IsInstalled(root));

            Directory.CreateDirectory(ProPainterSetup.PythonDir(root));
            File.WriteAllText(ProPainterSetup.PythonExe(root), "");
            string pp = ProPainterSetup.ProPainterDir(root);
            Directory.CreateDirectory(Path.Combine(pp, "weights"));
            File.WriteAllText(Path.Combine(pp, "inference_propainter.py"), "");
            foreach (var w in ProPainterSetup.Weights) File.WriteAllText(Path.Combine(pp, "weights", w.Name), "");
            Assert.False(ProPainterSetup.IsInstalled(root)); // 표시 파일이 없으면 아직

            File.WriteAllText(Path.Combine(root, ProPainterSetup.MarkerFileName), "0");
            Assert.False(ProPainterSetup.IsInstalled(root)); // 옛 판

            File.WriteAllText(Path.Combine(root, ProPainterSetup.MarkerFileName), ProPainterSetup.Version);
            Assert.True(ProPainterSetup.IsInstalled(root));

            var settings = new AppSettings();
            Assert.False(ProPainterSetup.IsConfigured(settings));
            Assert.False(ProPainterSetup.IsUserConfigured(settings));
            ProPainterSetup.Apply(settings, root);
            Assert.True(ProPainterSetup.IsConfigured(settings));
            Assert.False(ProPainterSetup.IsUserConfigured(settings)); // 우리 runtime을 가리킨다
            Assert.Equal(ProPainterSetup.PythonExe(root), settings.Python);
            Assert.Equal(Path.GetFullPath(root), ProPainterSetup.RootOf(settings));
            Assert.Equal(Path.GetFullPath(root), ProPainterSetup.ResolveRoot(settings)); // 있는 runtime을 다시 쓴다
        }
        finally
        {
            TestTools.DeleteDir(root);
        }
    }

    [Fact]
    public void Users_Own_ProPainter_Is_Never_Treated_As_Ours()
    {
        string dir = TestTools.NewTempDir("직접 설치");
        try
        {
            string pp = Path.Combine(dir, "ProPainter");
            Directory.CreateDirectory(Path.Combine(pp, "venv", "Scripts"));
            File.WriteAllText(Path.Combine(pp, "inference_propainter.py"), "");
            string python = Path.Combine(pp, "venv", "Scripts", "python.exe");
            File.WriteAllText(python, "");

            var mine = new AppSettings { Python = python, ProPainterDir = pp };
            Assert.True(ProPainterSetup.IsConfigured(mine));
            Assert.True(ProPainterSetup.IsUserConfigured(mine));
            Assert.Null(ProPainterSetup.RootOf(mine));

            // PATH 이름만 적어도 쓸 수 있는 설정이다 (JobRunner와 같은 기준)
            Assert.True(ProPainterSetup.IsConfigured(new AppSettings { Python = "python", ProPainterDir = pp }));

            // 지금은 없는 경로(뽑아 둔 드라이브) — 직접 적은 것이므로 자동 준비가 덮어쓰면 안 된다
            var unplugged = new AppSettings { Python = @"Q:\ProPainter\venv\Scripts\python.exe", ProPainterDir = @"Q:\ProPainter" };
            Assert.False(ProPainterSetup.IsConfigured(unplugged));
            Assert.True(ProPainterSetup.IsUserConfigured(unplugged));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Raw_Pip_Progress_Is_Parsed()
    {
        Assert.True(ProPainterSetup.TryParsePipProgress("Progress 262144 of 12608406", out double f, out string d));
        Assert.Equal(0.0208, f, 3);
        Assert.Equal("0/12 MB", d);
        Assert.False(ProPainterSetup.TryParsePipProgress("Progress 0 of 0", out _, out _));
    }

    [Fact]
    public void Uninstall_Only_Removes_Runtimes_We_Made()
    {
        string dir = TestTools.NewTempDir("제거");
        try
        {
            // 우리가 만든 runtime (표시 파일 있음)
            string ours = Path.Combine(dir, "ours");
            Directory.CreateDirectory(ProPainterSetup.PythonDir(ours));
            File.WriteAllText(ProPainterSetup.PythonExe(ours), "");
            File.WriteAllText(Path.Combine(ours, ProPainterSetup.MarkerFileName), ProPainterSetup.Version);
            // 사용자가 직접 깐 ProPainter venv (표시 파일 없음)
            string theirs = Path.Combine(dir, "theirs", "venv", "Scripts");
            Directory.CreateDirectory(theirs);
            File.WriteAllText(Path.Combine(theirs, "python.exe"), "");

            Assert.True(UninstallCleanup.IsOurRuntime(ours));
            Assert.False(UninstallCleanup.IsOurRuntime(Path.Combine(dir, "theirs", "venv")));

            var roots = UninstallCleanup.FindRuntimeRoots(new AppSettings { Python = ProPainterSetup.PythonExe(ours) });
            Assert.Contains(Path.GetFullPath(ours), roots, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(roots, r => r.Contains("theirs", StringComparison.OrdinalIgnoreCase));

            roots = UninstallCleanup.FindRuntimeRoots(new AppSettings { Python = Path.Combine(theirs, "python.exe") });
            Assert.DoesNotContain(roots, r => r.StartsWith(dir, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestTools.DeleteDir(dir);
        }
    }

    [Fact]
    public void Default_Root_Is_Ascii_Only()
    {
        // OpenCV가 영문 외 경로를 못 읽으므로 기본 위치는 언제나 영문 경로여야 한다
        Assert.All(ProPainterSetup.DefaultRoot, c => Assert.True(c < 128));
    }
}
