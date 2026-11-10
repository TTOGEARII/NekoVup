using System.Diagnostics;
using System.Text;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Settings;

namespace NekoVup.Core.Runtime;

/// <summary>
/// 프로그램을 제거할 때 함께 지울 것들 (사용자 요청 "삭제할 때 관련 프로그램들도 전부 삭제"):
/// 자동으로 준비한 ProPainter 환경(<c>NekoVup-runtime</c>, 약 6 GB), 끝나지 않은 작업의 중간 파일(<c>.nekovup</c>), 설정·로그·모델 폴더(%AppData%\NekoVup).
/// 설치 폴더(current\tools의 FFmpeg·Video2X 포함)는 Velopack이 지운다.
/// <para>
/// 사용자가 직접 설치해 경로만 적어 둔 ProPainter·Video2X는 **절대 지우지 않는다** — 우리가 만든 폴더는 표시 파일로 가려내고,
/// 그 안에서도 우리가 만든 것(<see cref="ProPainterSetup.OwnedDirectories"/>)만 지운다.
/// </para>
/// </summary>
public static class UninstallCleanup
{
    /// <summary>지울 runtime 폴더 후보: 설정이 가리키는 것 + 모든 고정 드라이브의 <c>X:\NekoVup-runtime</c>. 우리가 만든 것만.</summary>
    public static IReadOnlyList<string> FindRuntimeRoots(AppSettings settings)
    {
        var candidates = new List<string>();
        if (ProPainterSetup.RootOf(settings) is { } configured) candidates.Add(configured);
        candidates.AddRange(ProPainterSetup.CandidateRoots());
        return candidates
            .Where(IsOurRuntime)
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>우리가 만든 runtime인지 (사용자가 직접 깐 ProPainter에는 표시 파일이 없다).</summary>
    public static bool IsOurRuntime(string root) => ProPainterSetup.IsOurRuntime(root);

    /// <summary>
    /// 끝나지 않은 작업이 남긴 것: 기록해 둔 <c>.nekovup</c> 폴더들과, 사용자가 직접 깐 ProPainter 폴더 안의 임시 폴더(<c>.nekovup-tmp</c>).
    /// </summary>
    public static IReadOnlyList<string> FindWorkFolders(AppSettings settings, string? registryPath = null)
    {
        var folders = WorkFolders.Load(registryPath).Where(WorkFolders.IsWorkFolder).ToList();
        // 상대 경로는 받지 않는다 — 제거 프로그램의 작업 폴더를 기준으로 풀려 엉뚱한 곳을 가리킨다.
        if (!string.IsNullOrWhiteSpace(settings.ProPainterDir) && Path.IsPathFullyQualified(settings.ProPainterDir))
        {
            try
            {
                string scratch = Path.Combine(Path.GetFullPath(settings.ProPainterDir), JobRunner.ProPainterScratch);
                if (Directory.Exists(scratch)) folders.Add(scratch);
            }
            catch (ArgumentException) { }
            catch (IOException) { }
        }
        return folders.Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 제거 때 부른다. 설정·로그 폴더는 바로 지우고, 큰 폴더(runtime, 작업 폴더)는 따로 띄운 명령 창이 지운다 —
    /// 제거 프로그램이 앱의 뒷정리를 오래 기다려 주지 않으므로 6 GB를 이 안에서 지우면 중간에 끊긴다.
    /// </summary>
    public static void Run(Action<string>? log = null)
    {
        // 한 단계가 뜻밖의 오류로 실패해도 나머지는 지운다 (여기서 오류가 새면 제거 프로그램이 부른 뒷정리가 통째로 끊긴다).
        AppSettings settings;
        try
        {
            settings = SettingsStore.Load();
        }
        catch (Exception ex)
        {
            // 설정을 못 읽어도 고정 위치(X:\NekoVup-runtime)와 기록해 둔 작업 폴더는 찾아서 지운다.
            log?.Invoke($"설정 읽기 실패: {ex.GetType().Name}: {ex.Message}");
            settings = new AppSettings();
        }
        Stage("runtime", log, () =>
        {
            foreach (var root in FindRuntimeRoots(settings))
            {
                bool scheduled = DeleteLater(
                    ProPainterSetup.OwnedDirectories.Select(d => Path.Combine(root, d)),
                    ProPainterSetup.OwnedFiles.Select(f => Path.Combine(root, f)),
                    removeIfEmpty: root);
                log?.Invoke((scheduled ? "runtime 삭제 예약: " : "runtime 삭제를 예약하지 못함: ") + root);
            }
        });
        Stage("작업 폴더", log, () =>
        {
            foreach (var folder in FindWorkFolders(settings))
                log?.Invoke((DeleteLater([folder]) ? "작업 폴더 삭제 예약: " : "작업 폴더 삭제를 예약하지 못함: ") + folder);
        });

        Delete(SettingsStore.DataDirectory, directory: true, log);
        // %LocalAppData%\NekoVup은 설치 폴더이기도 하다 — 그 안의 옛 설정 파일만 지운다 (나머지는 제거 프로그램이 지운다).
        Delete(SettingsStore.LegacyPath, directory: false, log);
    }

    private static void Stage(string name, Action<string>? log, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log?.Invoke($"{name} 정리 실패: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Delete(string path, bool directory, Action<string>? log)
    {
        try
        {
            if (directory)
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, recursive: true);
            }
            else
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
            }
            log?.Invoke("삭제: " + path);
        }
        catch (IOException ex) { log?.Invoke($"삭제 실패 {path}: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { log?.Invoke($"삭제 실패 {path}: {ex.Message}"); }
    }

    /// <summary>되풀이 횟수 × 약 3초 동안 다시 시도한다 (백신 검사나 막 끝나는 프로세스가 파일을 잠깐 붙잡고 있을 수 있다).</summary>
    private const int Attempts = 10;

    /// <summary>
    /// 폴더(통째로)와 파일을 지우는 명령 창을 창 없이 따로 띄운다. 잠깐 기다렸다가(파일 잠금이 풀리도록) 지우고, 남아 있으면 몇 번 더 시도한다.
    /// <paramref name="removeIfEmpty"/>는 마지막에 **비어 있을 때만** 지운다. 띄웠으면 true (지울 것이 없거나 띄우지 못했으면 false).
    /// </summary>
    public static bool DeleteLater(IEnumerable<string> directories, IEnumerable<string>? files = null, string? removeIfEmpty = null)
    {
        if (BuildDeleteCommand(directories, files ?? [], removeIfEmpty) is not { } plan) return false;
        // 이름만 쓰면 작업 폴더나 PATH 앞쪽의 다른 cmd.exe가 먼저 잡힐 수 있다.
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
            // /d: 레지스트리의 자동 실행 명령을 건너뛴다. /s: 바깥 따옴표 한 쌍만 벗긴다. /v:on: 경로를 !이름!으로 읽는다.
            Arguments = "/d /s /v:on /c \"" + plan.Command + "\"",
        };
        foreach (var (name, value) in plan.Variables) start.Environment[name] = value;
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>cmd /v:on /c</c>에 넘길 한 줄과 그 줄이 읽는 환경 변수. 경로는 명령 줄에 적지 않고 환경 변수로 넘겨 <c>!이름!</c>으로 읽는다 —
    /// 명령 줄에 적으면 cmd가 경로 안의 <c>%이름%</c>·<c>%i</c>를 다른 글자로 바꿔 엉뚱한 폴더를 가리키게 된다 (<c>!이름!</c>으로 읽은 값은 다시 풀이하지 않는다).
    /// 지연은 <c>ping</c>으로 — <c>timeout</c>은 콘솔 입력이 없는 곳(제거 프로그램이 띄운 프로세스)에서 바로 오류를 내고 끝난다.
    /// 경로는 전부 절대 경로여야 한다(상대 경로·드라이브 바로 아래는 버린다). 지울 것이 없으면 null.
    /// </summary>
    public static DeletePlan? BuildDeleteCommand(IEnumerable<string> directories, IEnumerable<string> files, string? removeIfEmpty)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        foreach (string dir in directories.Where(IsSafeTarget)) dirs.Add(Variable(dir));
        var fileList = new List<string>();
        foreach (string file in files.Where(IsSafeTarget)) fileList.Add(Variable(file));
        if (dirs.Count == 0 && fileList.Count == 0) return null;

        // ping은 전체 경로로 부른다 — 이름만 쓰면 작업 폴더(임시 폴더)나 PATH 앞쪽의 다른 ping이 먼저 잡힐 수 있고, 못 찾으면 기다림 없이 헛돈다.
        var body = new StringBuilder(Variable(Path.Combine(Environment.SystemDirectory, "ping.exe"))).Append(" -n 4 127.0.0.1 >nul");
        foreach (string dir in dirs) body.Append(" & rmdir /s /q ").Append(dir).Append(" 2>nul");
        if (fileList.Count > 0) body.Append(" & del /f /q ").AppendJoin(' ', fileList).Append(" 2>nul");
        if (removeIfEmpty is not null && IsSafeTarget(removeIfEmpty)) body.Append(" & rmdir ").Append(Variable(removeIfEmpty)).Append(" 2>nul");
        // 다 지워졌으면 끝낸다. 남았으면 다음 바퀴에서 다시.
        body.Append(" & ");
        foreach (string target in dirs.Concat(fileList)) body.Append("if not exist ").Append(target).Append(' ');
        body.Append("exit");
        return new DeletePlan($"for /l %i in (1,1,{Attempts}) do ({body})", variables);

        // 경로를 환경 변수에 담고, 명령 줄에 적을 표기("!이름!")를 돌려준다.
        string Variable(string path)
        {
            string name = $"NEKOVUP_RM_{variables.Count}";
            variables[name] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return $"\"!{name}!\"";
        }
    }

    /// <summary>지워도 되는 경로: 절대 경로이고, 드라이브 바로 아래가 아니고, 따옴표가 없다 (따옴표는 명령 줄의 따옴표 짝을 깬다).</summary>
    private static bool IsSafeTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        if (path.Contains('"')) return false;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return !full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>따로 띄울 삭제 명령: 명령 줄 한 줄과, 그 줄이 <c>!이름!</c>으로 읽는 환경 변수(이름 → 경로).</summary>
public sealed record DeletePlan(string Command, IReadOnlyDictionary<string, string> Variables);
