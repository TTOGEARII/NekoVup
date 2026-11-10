using NekoVup.Core.Settings;

namespace NekoVup.Core.Runtime;

/// <summary>
/// 작업 폴더(<c>출력 폴더\.nekovup</c>, 출력 폴더가 OneDrive 안이면 <c>X:\NekoVup-work</c>)를 만든 곳을 적어 둔다. 작업이 실패하거나 중간에 꺼지면 이어 하려고 중간 파일을 남기는데,
/// 프로그램을 제거할 때 그것까지 지우려면 어디에 만들었는지 알아야 한다 (<see cref="UninstallCleanup"/>).
/// </summary>
public static class WorkFolders
{
    public const string FolderName = ".nekovup";

    /// <summary>출력 폴더가 OneDrive 안일 때 대신 쓰는 작업 폴더 (고정 드라이브 바로 아래, <c>X:\NekoVup-runtime</c> 옆).</summary>
    public const string FallbackFolderName = "NekoVup-work";

    /// <summary>
    /// 출력 폴더가 동기화 폴더(<see cref="SyncFolders"/>) 안일 때 작업 폴더들을 둘 곳 — 고정 드라이브 바로 아래의 <see cref="FallbackFolderName"/>.
    /// 이 작업의 폴더가 이미 있는 드라이브(끊긴 작업 이어 하기) → 그 폴더가 이미 있는 드라이브 → ProPainter 환경이 있는 드라이브 → 남은 공간이 가장 큰 드라이브 순.
    /// 남은 공간만 보면 날마다 고르는 드라이브가 바뀌어 끊긴 작업을 찾지 못한다.
    /// </summary>
    public static string FallbackRoot(string? jobFolderName = null) => FallbackRoot(jobFolderName, FixedDriveRoots(), FreeSpace);

    internal static string FallbackRoot(string? jobFolderName, IReadOnlyList<string> driveRoots, Func<string, long> freeSpace)
    {
        string At(string drive) => Path.Combine(drive, FallbackFolderName);
        return (jobFolderName is { Length: > 0 } ? driveRoots.Select(At).FirstOrDefault(root => Directory.Exists(Path.Combine(root, jobFolderName))) : null)
            ?? driveRoots.Select(At).FirstOrDefault(Directory.Exists)
            ?? driveRoots.Where(drive => Directory.Exists(Path.Combine(drive, ProPainterSetup.RootFolderName))).Select(At).FirstOrDefault()
            ?? driveRoots.OrderByDescending(freeSpace).Select(At).FirstOrDefault()
            ?? At(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
    }

    private static List<string> FixedDriveRoots()
    {
        try
        {
            return [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static long FreeSpace(string drive)
    {
        try
        {
            return new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    public static string DefaultRegistryPath { get; } = Path.Combine(SettingsStore.DataDirectory, "workdirs.txt");

    /// <summary>
    /// 기록 파일. null이면 적지 않는다 — 테스트와 개발 도구가 사용자 폴더(%AppData%)에 손대지 않게, 프로그램(창·명령줄)이 시작할 때 켠다.
    /// </summary>
    public static string? RegistryPath { get; set; }

    private static readonly Lock Gate = new();

    /// <summary>
    /// 작업 폴더의 부모(<c>…\.nekovup</c>)를 기록한다. 이미 사라진 폴더의 줄은 이때 걸러 낸다. 실패해도 작업은 계속한다.
    /// 드라이브째 안 보이는 줄(뽑아 둔 외장 드라이브)은 남긴다 — 다시 꽂으면 거기에 중간 파일이 그대로 있다.
    /// </summary>
    public static void Remember(string folder, string? registryPath = null)
    {
        if ((registryPath ?? RegistryPath) is not { } path) return;
        try
        {
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            lock (Gate)
            {
                var lines = Load(path).Where(l => Directory.Exists(l) || (Path.GetPathRoot(l) is { Length: > 0 } drive && !Directory.Exists(drive))).ToList();
                if (!lines.Contains(folder, StringComparer.OrdinalIgnoreCase)) lines.Add(folder);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, lines);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
    }

    public static IReadOnlyList<string> Load(string? path = null)
    {
        try
        {
            path ??= DefaultRegistryPath;
            if (!File.Exists(path)) return [];
            return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// 지워도 되는 작업 폴더인지: 이름이 <c>.nekovup</c>(또는 <see cref="FallbackFolderName"/>)이고 바로 아래에 폴더만 있다
    /// (우리는 그 안에 작업마다 폴더 하나씩만 만든다). 기록 파일이 손상되거나 누가 고쳐 놓아도 엉뚱한 폴더를 지우지 않게 한 번 더 본다.
    /// </summary>
    public static bool IsWorkFolder(string folder)
    {
        try
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
            return Path.IsPathFullyQualified(folder)
                && (name.Equals(FolderName, StringComparison.OrdinalIgnoreCase) || name.Equals(FallbackFolderName, StringComparison.OrdinalIgnoreCase))
                && Directory.Exists(folder)
                && !Directory.EnumerateFiles(folder).Any();
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
