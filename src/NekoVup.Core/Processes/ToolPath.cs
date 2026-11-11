namespace NekoVup.Core.Processes;

/// <summary>
/// 이름만 적힌 도구("ffmpeg")를 전체 경로로 바꾼다. Windows는 이름만 주면 현재 폴더에서도 찾는데, 현재 폴더는
/// 영상을 연 폴더일 수 있어 거기 놓인 같은 이름의 파일이 대신 실행될 수 있다. 그래서 프로그램 폴더 → 시스템 폴더 →
/// PATH 순서로만 찾고, 현재 폴더와 PATH의 상대 경로 항목(".", "bin" 등)은 보지 않는다.
/// </summary>
public static class ToolPath
{
    /// <summary>폴더 없이 이름만 적혔는지 ("ffmpeg", "python.exe"). 빈 값과 경로("C:\x\ffmpeg.exe", "tools\ffmpeg.exe")는 아니다.</summary>
    public static bool IsBareName(string fileName) =>
        fileName.Trim().Length > 0 && fileName.AsSpan().IndexOfAny('\\', '/', ':') < 0;

    /// <summary>
    /// 실제로 실행할 경로. 이름만 적힌 도구는 찾은 전체 경로로 바꾸고, 못 찾으면 null. 경로로 적힌 값은 그대로 돌려준다.
    /// </summary>
    public static string? Resolve(string fileName) => IsBareName(fileName) ? Find(fileName.Trim(), SearchDirectories()) : fileName;

    /// <summary>이름만 적힌 도구를 전체 경로로. 못 찾으면 적힌 그대로 (다른 프로그램에 넘길 값 — 실행할 때 못 찾았다고 알린다).</summary>
    public static string ResolveOrSame(string fileName) => Resolve(fileName) ?? fileName;

    internal static string? Find(string name, IEnumerable<string> directories)
    {
        // Windows와 같게: 확장자가 없으면 .exe를 붙여 찾는다.
        string file = Path.HasExtension(name) ? name : name + ".exe";
        foreach (string directory in directories)
        {
            string candidate;
            try
            {
                if (!Path.IsPathFullyQualified(directory)) continue;
                candidate = Path.Combine(directory, file);
            }
            catch (ArgumentException)
            {
                continue; // PATH에 경로로 쓸 수 없는 글자가 섞인 항목
            }
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        yield return AppContext.BaseDirectory;
        yield return Environment.SystemDirectory;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = entry.Trim().Trim('"');
            if (directory.Length > 0) yield return directory;
        }
    }
}
