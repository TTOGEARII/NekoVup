using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Processes;
using NekoVup.Core.Settings;

namespace NekoVup.Core.Runtime;

/// <summary>설치 진행 상황. <paramref name="Fraction"/>은 전체 0~1.</summary>
public readonly record struct SetupProgress(string Step, double Fraction, string? Detail = null);

/// <summary>
/// ProPainter(워터마크 복원)를 쓸 수 있게 파이썬 환경을 통째로 준비한다 — 사용자 요청 "설치 파일 하나만 설치하면 바로 쓸 수 있게".
/// PyTorch(약 2.5 GB)는 GitHub 릴리스 한 파일 한도(2 GB)를 넘어 설치 파일에 담을 수 없으므로, 프로그램이 처음 실행될 때 여기서 내려받는다.
/// <code>
/// {root}\python\      python.org 임베더블 3.11.9 + pip + torch 2.5.1(cu121) + ProPainter가 쓰는 패키지
/// {root}\ProPainter\  소스(고정 커밋) + weights\(가중치 3개)
/// {root}\propainter.ok  끝났다는 표시 (내용: <see cref="Version"/>)
/// </code>
/// 판은 전부 고정돼 있고 이 PC에서 실제로 돌아간 조합(D:\nekoVup-tools)과 같다. 판을 올리면 <see cref="Version"/>도 올려서 다시 준비하게 한다.
/// </summary>
public sealed partial class ProPainterSetup(IToolRunner runner, HttpClient http, IJobLog log)
{
    /// <summary>준비된 환경의 판. 아래 구성 요소를 바꾸면 올린다.</summary>
    public const string Version = "1";

    public const string MarkerFileName = "propainter.ok";

    /// <summary>준비를 시작할 때 만들고 끝나면 지우는 표시 파일. 준비가 끊긴 폴더도 제거 때 우리 것으로 알아보려고 둔다.</summary>
    public const string SetupMarkerFileName = "propainter.setup";

    /// <summary>
    /// 작업이 이 환경의 파이썬을 쓰는 동안 열어 두는 파일. 작업들은 함께 열 수 있고(읽기 공유), 준비는 혼자 열어야 시작한다 —
    /// 다른 창에서 다시 준비하면 쓰던 파이썬을 지워 작업도 준비도 함께 실패한다.
    /// </summary>
    public const string InUseFileName = "propainter.inuse";

    /// <summary>
    /// runtime 폴더 바로 아래에 우리가 만드는 것 전부. 제거 때는 이것들만 지운다 (폴더에 다른 것이 섞여 있어도 건드리지 않게).
    /// 내려받기 폴더는 흔한 이름("downloads")을 쓰지 않는다 — 명령줄 --dir로 준 폴더에 사용자가 같은 이름의 폴더를 두면 제거 때 함께 지워진다.
    /// </summary>
    public static readonly string[] OwnedDirectories = ["python", "ProPainter", "nekovup-downloads"];
    public static readonly string[] OwnedFiles = [MarkerFileName, SetupMarkerFileName, InUseFileName];

    /// <summary>필요한 디스크 공간 어림: 내려받기·pip 캐시 2.7 GB + pip 임시 풀기 2.5 GB + 설치된 파일 약 5 GB (전부 root 드라이브에).</summary>
    public const long RequiredBytes = 10L * 1024 * 1024 * 1024;

    public static readonly Uri PythonUrl = new("https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip");
    public const string PythonSha256 = "009D6BF7E3B2DDCA3D784FA09F90FE54336D5B60F0E0F305C37F400BF83CFD3B";

    public static readonly Uri PipUrl = new("https://files.pythonhosted.org/packages/07/25/ee8c38ae72d66b844ce8eb9711ba905dba21ca1a8a0c960057c41cd23337/pip-26.3-py3-none-any.whl");
    public const string PipSha256 = "f3b60b20ffaadd4415ed7957212a38ac63fa4c5fa375bd0f4a797777940fad13";

    public const string TorchIndex = "https://download.pytorch.org/whl/cu121";
    public static readonly string[] TorchPackages = ["torch==2.5.1+cu121", "torchvision==0.20.1+cu121"];

    /// <summary>ProPainter requirements.txt의 패키지를 이 PC에서 돌아간 판으로 고정한 것.</summary>
    public static readonly string[] Packages =
    [
        "av==18.1.0", "addict==2.4.0", "einops==0.8.2", "future==1.0.0", "numpy==2.4.6", "scipy==1.17.1",
        "opencv-python==5.0.0.93", "matplotlib==3.11.2", "scikit-image==0.26.0", "imageio-ffmpeg==0.8.1",
        "pyyaml==6.0.3", "requests==2.34.2", "timm==1.0.31", "yapf==0.43.0",
    ];

    public const string ProPainterCommit = "326782529a63d997cab80ab21439b92ed422b3d3";
    public static readonly Uri ProPainterUrl = new($"https://github.com/sczhou/ProPainter/archive/{ProPainterCommit}.zip");

    public static readonly (string Name, Uri Url, string Sha256)[] Weights =
    [
        ("ProPainter.pth", new("https://github.com/sczhou/ProPainter/releases/download/v0.1.0/ProPainter.pth"),
            "12C070C4B48F374C91D8A2A17851140B85C159621080989F9E191BBC18BD6591"),
        ("recurrent_flow_completion.pth", new("https://github.com/sczhou/ProPainter/releases/download/v0.1.0/recurrent_flow_completion.pth"),
            "22939A1A7900DA878DBE1CCD011D646B1BFB30B8290039D8FF0E0C2FEFBFD283"),
        ("raft-things.pth", new("https://github.com/sczhou/ProPainter/releases/download/v0.1.0/raft-things.pth"),
            "FCFA4125D6418F4DE95D84AEC20A3C5F4E205101715A79F193243C186AC9A7E1"),
    ];

    // ───────────────────────── 위치 ─────────────────────────

    /// <summary>
    /// 기본 설치 위치: 여유가 가장 큰 고정 드라이브의 <c>X:\NekoVup-runtime</c> (NekoManga의 runtime과 같은 방식).
    /// 설치 프로그램 폴더(%LocalAppData%\NekoVup) 아래에 두면 Setup.exe로 다시 설치할 때 통째로 지워져(실측) 6 GB를 다시 받게 되고,
    /// 경로에 영문 외 글자(한글 사용자 이름)가 들어가면 OpenCV가 파일을 못 읽는다. 드라이브 바로 아래는 둘 다 피한다.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            try
            {
                var best = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .OrderByDescending(d => d.AvailableFreeSpace)
                    .FirstOrDefault();
                if (best is not null) return Path.Combine(best.RootDirectory.FullName, RootFolderName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", RootFolderName);
        }
    }

    public static string PythonDir(string root) => Path.Combine(root, "python");
    public static string PythonExe(string root) => Path.Combine(PythonDir(root), "python.exe");
    public static string ProPainterDir(string root) => Path.Combine(root, "ProPainter");
    public static string DownloadDir(string root) => Path.Combine(root, "nekovup-downloads");

    /// <summary>
    /// 작업이 이 환경의 파이썬을 쓰기 시작할 때 잡는다 (끝나면 Dispose). 다른 창에서 준비하고 있으면 <see cref="JobException"/>.
    /// 폴더가 없거나 쓸 수 없으면 IOException·UnauthorizedAccessException이 그대로 나간다.
    /// </summary>
    public static FileStream HoldForUse(string root)
    {
        try
        {
            return new FileStream(Path.Combine(root, InUseFileName), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            throw new JobException("다른 창에서 ProPainter 환경을 준비하고 있습니다. 준비가 끝난 뒤에 시작하세요.");
        }
    }

    /// <summary>준비를 시작할 때 잡는다 — 이 환경을 쓰는 작업이 다른 창에 있으면 아무것도 지우기 전에 <see cref="JobException"/>.</summary>
    private static FileStream HoldExclusively(string root)
    {
        try
        {
            return new FileStream(Path.Combine(root, InUseFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            throw new JobException("다른 창에서 이 ProPainter 환경을 쓰거나 준비하고 있습니다. 그 작업이 끝난 뒤에 다시 준비하세요.");
        }
    }

    /// <summary>이 판으로 준비가 끝나 있는지 (표시 파일 + 핵심 파일 존재).</summary>
    public static bool IsInstalled(string root)
    {
        try
        {
            string marker = Path.Combine(root, MarkerFileName);
            return File.Exists(marker) && File.ReadAllText(marker).Trim() == Version
                && File.Exists(PythonExe(root))
                && File.Exists(Path.Combine(ProPainterDir(root), JobRunner.ProPainterScript))
                && Weights.All(w => File.Exists(Path.Combine(ProPainterDir(root), "weights", w.Name)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// 설정이 가리키는 ProPainter를 쓸 수 있는지 — <see cref="JobRunner"/>가 실행을 받아 주는 조건과 같다
    /// (파이썬은 "python"처럼 PATH 이름이어도 되므로 파일 존재를 따지지 않고, ProPainter 폴더에 스크립트가 있어야 한다).
    /// </summary>
    public static bool IsConfigured(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Python)
        && !string.IsNullOrWhiteSpace(settings.ProPainterDir) && File.Exists(Path.Combine(settings.ProPainterDir, JobRunner.ProPainterScript));

    /// <summary>
    /// 설정의 파이썬·ProPainter가 있는 드라이브가 지금 없으면(뽑아 둔 외장 드라이브, 끊긴 네트워크 드라이브) 그 드라이브 이름, 아니면 null.
    /// 이때 다시 준비하라고 하면 다른 드라이브에 3 GB를 또 받게 된다 — 드라이브를 연결하라고 알려야 한다.
    /// </summary>
    public static string? MissingDrive(AppSettings settings)
    {
        foreach (string? path in (string?[])[settings.Python, settings.ProPainterDir])
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
            if (Path.GetPathRoot(path) is { Length: > 0 } drive && !Directory.Exists(drive)) return drive;
        }
        return null;
    }

    /// <summary>사용자가 직접 적어 둔 ProPainter 설정인지 (비어 있지 않고 우리가 준비한 runtime을 가리키지도 않는다). 이런 설정은 자동 준비가 덮어쓰지 않는다.</summary>
    public static bool IsUserConfigured(AppSettings settings) =>
        (!string.IsNullOrWhiteSpace(settings.Python) || !string.IsNullOrWhiteSpace(settings.ProPainterDir))
        && RootOf(settings) is null;

    /// <summary>설정의 파이썬 경로가 우리 runtime(<c>{root}\python\python.exe</c>) 안에 있으면 그 root. 표시 파일이 없어도(준비가 끊긴 상태) 구조가 맞으면 돌려준다.</summary>
    public static string? RootOf(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Python)) return null;
        try
        {
            string full = Path.GetFullPath(settings.Python);
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, ".."));
            // 다시 준비하다 끊긴 폴더에는 "끝남" 표시가 없고 "준비 중" 표시만 있다 — 그것도 우리 것이다 (안 그러면 제거 때 수 GB가 남는다).
            bool ours = !IsDriveRoot(root) && full.Equals(PythonExe(root), StringComparison.OrdinalIgnoreCase)
                && (OwnedFiles.Any(f => File.Exists(Path.Combine(root, f))) || Path.GetFileName(root).Equals(RootFolderName, StringComparison.OrdinalIgnoreCase));
            return ours ? root : null;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>드라이브 바로 아래(<c>D:\</c>)인지. 거기는 준비하지도, 우리 폴더로 여기지도 않는다 — 제거 때 <c>D:\python</c>·<c>D:\downloads</c>를 지우게 되면 안 된다.</summary>
    private static bool IsDriveRoot(string path)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        return Path.GetPathRoot(path) is { } drive && trimmed.Equals(Path.TrimEndingDirectorySeparator(drive), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 우리가 만든 runtime인지: 표시 파일(끝남 <see cref="MarkerFileName"/> 또는 준비 중 <see cref="SetupMarkerFileName"/>)이 있거나,
    /// 우리 이름(<see cref="RootFolderName"/>)의 폴더에 우리가 만든 것만 들어 있어야 한다 (준비가 끊겨 표시 파일이 없는 것도 포함).
    /// 사용자가 직접 깐 ProPainter venv는 어느 쪽도 아니다.
    /// </summary>
    public static bool IsOurRuntime(string root)
    {
        try
        {
            if (!Directory.Exists(root) || IsDriveRoot(Path.GetFullPath(root))) return false;
            if (OwnedFiles.Any(f => File.Exists(Path.Combine(root, f)))) return true;
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Equals(RootFolderName, StringComparison.OrdinalIgnoreCase)
                && Directory.EnumerateFileSystemEntries(root).Any() && HasOnlyOwnedEntries(root);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>폴더 바로 아래에 우리가 만드는 것(<see cref="OwnedDirectories"/>, <see cref="OwnedFiles"/>) 말고는 없는지. 빈 폴더·없는 폴더도 참.</summary>
    public static bool HasOnlyOwnedEntries(string root)
    {
        if (!Directory.Exists(root)) return true;
        return Directory.EnumerateFileSystemEntries(root).All(entry =>
        {
            string name = Path.GetFileName(entry);
            return Directory.Exists(entry)
                ? OwnedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)
                : OwnedFiles.Contains(name, StringComparer.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// 준비하거나 쓸 root를 정한다: 설정이 가리키는 우리 runtime → 이미 있는 고정 드라이브의 <c>X:\NekoVup-runtime</c> → <see cref="DefaultRoot"/>.
    /// (여유 공간이 가장 큰 드라이브는 날마다 바뀔 수 있어, 그때마다 새 곳에 6 GB를 또 만들지 않게.)
    /// 이미 있는 폴더는 우리가 만들던 것(<see cref="IsOurRuntime"/>)만 고른다 — 빈 폴더만 남은 드라이브에 묶이지 않게.
    /// </summary>
    public static string ResolveRoot(AppSettings settings)
    {
        if (RootOf(settings) is { } configured && Directory.Exists(configured)) return configured;
        foreach (var root in CandidateRoots())
            if (IsOurRuntime(root)) return root;
        return DefaultRoot;
    }

    /// <summary>모든 고정 드라이브의 <c>X:\NekoVup-runtime</c>.</summary>
    public static IEnumerable<string> CandidateRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, RootFolderName));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return roots;
    }

    public const string RootFolderName = "NekoVup-runtime";

    /// <summary>준비된 환경을 설정에 적는다.</summary>
    public static void Apply(AppSettings settings, string root)
    {
        settings.Python = PythonExe(root);
        settings.ProPainterDir = ProPainterDir(root);
    }

    // ───────────────────────── 설치 ─────────────────────────

    private static readonly (string Step, double Weight)[] Steps =
    [
        ("파이썬", 2), ("pip", 1), ("PyTorch", 60), ("패키지", 15), ("ProPainter", 2), ("가중치", 15), ("확인", 5),
    ];

    /// <summary>
    /// 준비해도 되는 폴더인지 확인한다. 준비하면서 root\python·ProPainter·downloads를 지우고 새로 만들므로,
    /// 다른 것이 들어 있는 폴더(드라이브 바로 아래, 사용자의 도구 폴더)를 받으면 거기 있던 같은 이름의 폴더가 날아간다.
    /// 그래서 없는 폴더·빈 폴더·우리가 만든 폴더·우리 이름(<see cref="RootFolderName"/>)의 폴더만 받는다.
    /// (우리 이름의 폴더는 사용자가 다른 파일을 넣어 두었더라도 받는다 — 제거 때 그 파일 때문에 폴더가 남는데, 다시 설치했을 때 준비가 막히면 안 된다.)
    /// </summary>
    public static void CheckTarget(string root)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(root);
        if (IsDriveRoot(root))
            throw new JobException($"드라이브 바로 아래에는 준비할 수 없습니다. 폴더를 지정하세요 (예: {Path.Combine(root, RootFolderName)}).");
        if (Path.GetFileName(trimmed).Equals(RootFolderName, StringComparison.OrdinalIgnoreCase) || IsOurRuntime(root)) return;
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new JobException($"다른 파일이 들어 있는 폴더입니다. 비어 있는 폴더를 지정하세요: {root}");
    }

    /// <summary>ERROR_SHARING_VIOLATION: 다른 프로세스가 파일을 열어 두었다.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    public async Task InstallAsync(string root, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        root = Path.GetFullPath(root);
        CheckTarget(root);

        // 공간 확인은 폴더를 만들기 전에: 모자라서 실패했는데 빈 폴더가 남으면 다음부터 그 드라이브만 고르게 된다.
        CheckDiskSpace(root);
        Directory.CreateDirectory(root);
        // 다른 창의 작업이 이 환경의 파이썬을 쓰고 있으면 표시 파일 하나 건드리기 전에 멈춘다.
        using var inUse = HoldExclusively(root);

        // 준비 중 표시부터: 여기서 끊겨도 제거할 때 우리 폴더로 알아보고 지운다. 끝나면 "끝남" 표시로 바꾼다.
        // 준비하는 동안 열어 둔다 — 창을 하나 더 띄워 같은 폴더를 또 준비하면 서로의 파일을 지워 둘 다 실패한다.
        string setupMarker = Path.Combine(root, SetupMarkerFileName);
        FileStream setupLock;
        try
        {
            setupLock = new FileStream(setupMarker, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            // 다른 이유(디스크가 꽉 참 등)의 오류는 그 내용 그대로 알린다.
            throw new JobException("다른 창에서 이미 ProPainter를 준비하고 있습니다. 끝날 때까지 기다리세요.");
        }
        try
        {
            setupLock.Write(System.Text.Encoding.ASCII.GetBytes(Version));
            setupLock.Flush();
            await InstallCoreAsync(root, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await setupLock.DisposeAsync().ConfigureAwait(false);
        }
        TryDeleteFile(setupMarker);
        log.Info("ProPainter 준비 끝");
    }

    private async Task InstallCoreAsync(string root, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        // 다시 준비하는 동안 끊기면 "끝난 것"으로 보이지 않게 표시 파일부터 지운다. 맨 끝에 다시 쓴다.
        TryDeleteFile(Path.Combine(root, MarkerFileName));
        string downloads = DownloadDir(root);
        // 지난번에 끊긴 pip이 풀다 만 임시 파일(수 GB)이 남아 있을 수 있다. 받아 둔 파일·pip 캐시는 다시 쓰므로 남긴다.
        TryDeleteDirectory(Path.Combine(downloads, "tmp"));
        Directory.CreateDirectory(downloads);
        var report = new Reporter(progress);
        log.Info($"ProPainter 준비 시작: {root}");

        // 1. 파이썬
        report.Begin("파이썬");
        string pythonZip = await DownloadAsync(PythonUrl, Path.Combine(downloads, "python-3.11.9-embed-amd64.zip"), PythonSha256, report, cancellationToken).ConfigureAwait(false);
        string pythonDir = PythonDir(root);
        TryDeleteDirectory(pythonDir);
        ExtractOrDiscard(pythonZip, () => ZipFile.ExtractToDirectory(pythonZip, pythonDir, overwriteFiles: true)); // 지우다 남은 파일이 있어도 덮어쓴다
        EnableSitePackages(pythonDir);
        string python = PythonExe(root);

        // 2. pip (임베더블 파이썬에는 pip이 없다 — 휠 안의 pip으로 자기 자신을 설치한다.)
        //    `python 휠\pip install …`로 부르면 pip이 "자기를 고치려면 -m pip으로"라며 거부하고(argv[0]이 'pip'), ._pth가 있는 파이썬은 PYTHONPATH를 무시한다.
        //    그래서 sys.path에 휠을 얹고 argv[0]을 다른 이름으로 바꿔 pip 모듈을 직접 돌린다.
        report.Begin("pip");
        string pipWheel = await DownloadAsync(PipUrl, Path.Combine(downloads, Path.GetFileName(PipUrl.LocalPath)), PipSha256, report, cancellationToken).ConfigureAwait(false);
        await PythonAsync(python, ["-c", PipBootstrap, pipWheel], report, cancellationToken).ConfigureAwait(false);

        // 3. PyTorch (가장 크다). pip의 캐시·임시 폴더를 root 드라이브로 돌린다 — 기본값(C:의 %LocalAppData%\pip, %TEMP%)이면
        //    C:가 꽉 차서 D:를 고른 PC에서 중간에 죽는다. 끝나면 downloads와 함께 지워진다.
        var pipEnvironment = new Dictionary<string, string>(PythonEnvironment)
        {
            ["TMP"] = Path.Combine(downloads, "tmp"),
            ["TEMP"] = Path.Combine(downloads, "tmp"),
        };
        Directory.CreateDirectory(pipEnvironment["TMP"]);
        // --isolated: 이 PC의 pip 설정(pip.ini, PIP_* 환경 변수 — 다른 저장소 주소, 가상 환경 강제 등)을 따르지 않는다. 그래서 캐시 폴더도 인자로 준다.
        // --progress-bar raw: 출력이 파이프일 때 막대 대신 "Progress N of M" 줄을 찍는다 (막대 모드는 파이프에서 마지막 한 줄만 나온다).
        string[] pipInstall =
        [
            "-m", "pip", "install", "--isolated", "--disable-pip-version-check", "--no-warn-script-location",
            "--progress-bar", "raw", "--cache-dir", Path.Combine(downloads, "pip-cache"),
        ];
        report.Begin("PyTorch");
        await PythonAsync(python, [.. pipInstall, "--index-url", TorchIndex, .. TorchPackages], report, cancellationToken, pipEnvironment).ConfigureAwait(false);

        // 4. 나머지 패키지
        report.Begin("패키지");
        await PythonAsync(python, [.. pipInstall, .. Packages], report, cancellationToken, pipEnvironment).ConfigureAwait(false);

        // 5. ProPainter 소스
        report.Begin("ProPainter");
        string sourceZip = await DownloadAsync(ProPainterUrl, Path.Combine(downloads, $"ProPainter-{ProPainterCommit[..10]}.zip"), "", report, cancellationToken).ConfigureAwait(false);
        string dir = ProPainterDir(root);
        TryDeleteDirectory(dir);
        ExtractOrDiscard(sourceZip, () => ExtractStrippingRoot(sourceZip, dir));
        if (!File.Exists(Path.Combine(dir, JobRunner.ProPainterScript)))
            throw new JobException($"ProPainter 소스에 {JobRunner.ProPainterScript}가 없습니다.");

        // 6. 가중치
        report.Begin("가중치");
        string weights = Path.Combine(dir, "weights");
        Directory.CreateDirectory(weights);
        for (int i = 0; i < Weights.Length; i++)
        {
            var (name, url, sha) = Weights[i];
            report.Range(i, Weights.Length);
            await DownloadAsync(url, Path.Combine(weights, name), sha, report, cancellationToken).ConfigureAwait(false);
        }

        // 7. 확인
        report.Begin("확인");
        string check = await runner.RunCaptureAsync(new ToolInvocation(python,
            ["-c", "import torch, cv2, torchvision; print(torch.__version__, torch.cuda.is_available())"])
        { Environment = PythonEnvironment }, cancellationToken).ConfigureAwait(false);
        log.Info("확인: torch " + check.Trim());
        if (!check.Contains("2.5.1", StringComparison.Ordinal)) throw new JobException("설치된 torch 판이 예상과 다릅니다: " + check.Trim());
        if (!check.Contains("True", StringComparison.Ordinal)) log.Warning("CUDA를 쓸 수 없습니다 (NVIDIA 드라이버 확인). ProPainter는 GPU 없이 매우 느립니다.");

        TryDeleteDirectory(downloads);
        File.WriteAllText(Path.Combine(root, MarkerFileName), Version);
        report.Done();
    }

    /// <summary>휠 하나로 pip을 설치하는 한 줄짜리 파이썬 (인자: 휠 경로).</summary>
    public const string PipBootstrap =
        "import sys, runpy; w = sys.argv[1]; sys.path.insert(0, w); sys.argv = ['pip-bootstrap.py', 'install', '--isolated', '--disable-pip-version-check', '--no-warn-script-location', w]; runpy.run_module('pip', run_name='__main__')";

    /// <summary>파이썬 출력이 깨지지 않게.</summary>
    public static readonly Dictionary<string, string> PythonEnvironment = new() { ["PYTHONUTF8"] = "1", ["PYTHONIOENCODING"] = "utf-8" };

    /// <summary>ProPainter 폴더를 파이썬 폴더 기준 상대 경로로 ._pth에 적는다 (ComfyUI 포터블이 `../ComfyUI`를 적는 것과 같은 방식).</summary>
    public const string ProPainterPthEntry = "../ProPainter";

    /// <summary>
    /// 임베더블 파이썬은 <c>python311._pth</c>에 적힌 경로만 sys.path로 쓴다: site-packages도(<c>#import site</c>), 스크립트가 있는 폴더도 보지 않고
    /// PYTHONPATH도 무시한다. 그래서 <c>import site</c>를 켜고 ProPainter 폴더를 적어 준다 (안 그러면 <c>No module named 'model'</c>).
    /// </summary>
    public static void EnableSitePackages(string pythonDir)
    {
        string pth = Directory.EnumerateFiles(pythonDir, "python*._pth").FirstOrDefault()
            ?? throw new JobException("파이썬 폴더에 ._pth 파일이 없습니다: " + pythonDir);
        File.WriteAllText(pth, PatchPth(File.ReadAllText(pth)));
    }

    public static string PatchPth(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Trim() == "#import site") lines[i] = "import site";
        if (!lines.Any(l => l.Trim() == ProPainterPthEntry))
            lines.Insert(lines.FindIndex(l => l.Trim() == ".") is var dot && dot >= 0 ? dot + 1 : 0, ProPainterPthEntry);
        if (!lines.Any(l => l.Trim() == "import site")) lines.Add("import site");
        return string.Join("\n", lines);
    }

    private async Task PythonAsync(string python, IReadOnlyList<string> args, Reporter report, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        var invocation = new ToolInvocation(python, args) { Environment = environment ?? PythonEnvironment };
        log.Detail("> " + invocation.DisplayText);
        var result = await runner.RunAsync(invocation, line => OnPipLine(line, report), line => OnPipLine(line, report), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new ToolFailedException($"파이썬이 오류 코드 {result.ExitCode}로 끝났습니다.{Environment.NewLine}{result.ErrorTail}", result.ExitCode);
    }

    /// <summary>pip이 지금 내려받는 파일 이름 ("Downloading torch-… (2449.4 MB)"에서).</summary>
    private string _pipItem = "";

    private void OnPipLine(string line, Reporter report)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (TryParsePipProgress(line, out double fraction, out string detail))
        {
            report.Sub(fraction, _pipItem.Length > 0 ? $"{_pipItem} {detail}" : detail);
            return;
        }
        var trimmed = line.Trim();
        if (trimmed.StartsWith("Downloading ", StringComparison.Ordinal))
        {
            // "Downloading torch-2.5.1+cu121-cp311-cp311-win_amd64.whl (2449.4 MB)" → "torch-2.5.1+cu121"
            string name = Uri.UnescapeDataString(trimmed["Downloading ".Length..].Split(' ')[0]); // "%2B" → "+"
            int cp = name.IndexOf("-cp", StringComparison.Ordinal);
            _pipItem = cp > 0 ? name[..cp] : Path.GetFileNameWithoutExtension(name);
        }
        if (trimmed.StartsWith("Downloading ", StringComparison.Ordinal) || trimmed.StartsWith("Installing ", StringComparison.Ordinal)
            || trimmed.StartsWith("Successfully ", StringComparison.Ordinal) || trimmed.StartsWith("ERROR", StringComparison.Ordinal))
        {
            report.Sub(null, trimmed.Length > 90 ? trimmed[..90] : trimmed);
            log.Detail(trimmed);
        }
    }

    /// <summary>
    /// pip 진행 줄에서 비율을 읽는다. <c>--progress-bar raw</c>의 <c>Progress 262144 of 12608406</c>(바이트)와,
    /// 막대 모드의 <c>━━━━╸ 1.2/2.4 GB 10.3 MB/s eta 0:02:00</c> 둘 다 받는다.
    /// </summary>
    public static bool TryParsePipProgress(string line, out double fraction, out string detail)
    {
        fraction = 0; detail = "";
        var raw = PipRawProgress().Match(line);
        if (raw.Success)
        {
            double done = double.Parse(raw.Groups[1].Value, CultureInfo.InvariantCulture);
            double total = double.Parse(raw.Groups[2].Value, CultureInfo.InvariantCulture);
            if (total <= 0) return false;
            fraction = Math.Clamp(done / total, 0, 1);
            detail = string.Create(CultureInfo.InvariantCulture, $"{done / 1048576.0:0}/{total / 1048576.0:0} MB");
            return true;
        }
        var m = PipProgress().Match(line);
        if (!m.Success) return false;
        double a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        double b = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (b <= 0) return false;
        fraction = Math.Clamp(a / b, 0, 1);
        detail = m.Value.Trim();
        return true;
    }

    [GeneratedRegex(@"^\s*Progress\s+(\d+)\s+of\s+(\d+)\s*$")]
    private static partial Regex PipRawProgress();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)/(\d+(?:\.\d+)?)\s*(kB|MB|GB)(?:\s+\S+/s)?(?:\s+eta\s+\S+)?")]
    private static partial Regex PipProgress();

    // ───────────────────────── 내려받기 ─────────────────────────

    /// <summary>이미 받아 둔 파일이 해시까지 맞으면 그대로 쓴다. 해시가 비어 있으면 크기만 본다.</summary>
    private async Task<string> DownloadAsync(Uri url, string path, string sha256, Reporter report, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && (sha256.Length == 0 || Matches(path, sha256)))
        {
            log.Detail("이미 있음: " + Path.GetFileName(path));
            return path;
        }
        string partial = path + ".part";
        log.Info("내려받기: " + url);
        var lastReport = DateTime.MinValue;
        try
        {
            await Download.ToFileAsync(http, url, partial, (done, total) =>
            {
                if (total <= 0 || DateTime.UtcNow - lastReport <= TimeSpan.FromMilliseconds(200)) return;
                lastReport = DateTime.UtcNow;
                report.Sub((double)done / total, string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(path)} {done / 1048576.0:0}/{total / 1048576.0:0} MB"));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
        {
            // 받는 도중에 연결이 끊기면 HttpRequestException이 아니라 IOException으로 온다 (디스크가 꽉 찬 것도).
            throw new JobException($"내려받지 못했습니다 ({Path.GetFileName(path)}): {ex.Message}", ex);
        }
        if (sha256.Length > 0 && !Matches(partial, sha256))
        {
            File.Delete(partial);
            throw new JobException($"내려받은 파일이 손상됐습니다 (해시 불일치): {url}");
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    private static bool Matches(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 받아 둔 압축을 푼다. 손상된 압축이면 그 파일을 지우고 알린다 — 남겨 두면 다시 시도할 때마다 "이미 있음"으로 그 파일을 또 써서
    /// 영영 같은 자리에서 실패한다 (ProPainter 소스 zip은 해시로 확인하지 못한다).
    /// </summary>
    public static void ExtractOrDiscard(string zipPath, Action extract)
    {
        try
        {
            extract();
        }
        catch (InvalidDataException ex)
        {
            TryDeleteFile(zipPath);
            throw new JobException($"내려받은 압축 파일이 손상됐습니다 ({Path.GetFileName(zipPath)}). 다시 시도하면 새로 받습니다.", ex);
        }
    }

    /// <summary>GitHub 소스 zip은 <c>ProPainter-{commit}/</c> 폴더 하나로 싸여 있다. 그 껍질을 벗기고 푼다.</summary>
    public static void ExtractStrippingRoot(string zipPath, string target)
    {
        Directory.CreateDirectory(target);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            int slash = entry.FullName.IndexOf('/');
            if (slash < 0 || slash == entry.FullName.Length - 1) continue;
            string relative = entry.FullName[(slash + 1)..];
            if (relative.Length == 0) continue;
            string path = Path.GetFullPath(Path.Combine(target, relative));
            if (!path.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue; // zip slip
            if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private void CheckDiskSpace(string root)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            // 이미 받아 둔 것(끊긴 준비를 잇거나 다시 준비할 때)은 다시 쓰거나 지우고 새로 만드므로 남은 공간에 더해서 본다.
            long needed = RequiredBytes - DirectorySize(root);
            if (drive.AvailableFreeSpace < needed)
                throw new JobException(string.Create(CultureInfo.InvariantCulture,
                    $"ProPainter를 준비하려면 {drive.Name}에 약 {needed / 1073741824.0:0} GB가 필요한데 {drive.AvailableFreeSpace / 1073741824.0:0.0} GB만 남았습니다."));
        }
        catch (ArgumentException) { log.Warning("남은 공간을 확인할 수 없습니다: " + root); }
        catch (IOException) { log.Warning("남은 공간을 확인할 수 없습니다: " + root); }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var all = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            return new DirectoryInfo(directory).EnumerateFiles("*", all).Sum(f => f.Length);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>단계별 비중으로 전체 진행률을 만든다.</summary>
    private sealed class Reporter(IProgress<SetupProgress>? progress)
    {
        private static readonly double Total = Steps.Sum(s => s.Weight);
        private int _index = -1;
        private double _before, _sub, _rangeStart, _rangeSpan = 1;
        private string? _detail;

        public void Begin(string step)
        {
            _index = Array.FindIndex(Steps, s => s.Step == step);
            _before = Steps.Take(_index).Sum(s => s.Weight);
            _detail = null;
            _sub = 0;
            (_rangeStart, _rangeSpan) = (0, 1);
            Report();
        }

        /// <summary>단계 안의 진행(0~1, 현재 범위 안). null이면 설명만 바꾼다. 한 단계 안에서는 뒤로 가지 않는다 (pip이 큰 파일 뒤에 작은 파일을 받을 때 0으로 되돌아오지 않게).</summary>
        public void Sub(double? fraction, string? detail)
        {
            if (detail is not null) _detail = detail;
            if (fraction is { } f) _sub = Math.Max(_sub, _rangeStart + _rangeSpan * Math.Clamp(f, 0, 1));
            Report();
        }

        /// <summary>단계 안에서 여러 파일을 차례로 받을 때: 이후 Sub의 0~1이 i번째 칸에 들어간다.</summary>
        public void Range(int index, int count)
        {
            (_rangeStart, _rangeSpan) = ((double)index / count, 1.0 / count);
            _sub = _rangeStart;
            Report();
        }

        public void Done() => progress?.Report(new SetupProgress("완료", 1));

        private void Report() =>
            progress?.Report(new SetupProgress(Steps[_index].Step, (_before + Steps[_index].Weight * _sub) / Total, _detail));
    }
}
