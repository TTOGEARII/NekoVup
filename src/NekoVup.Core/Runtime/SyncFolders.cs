using Microsoft.Win32;

namespace NekoVup.Core.Runtime;

/// <summary>
/// OneDrive가 동기화하는 폴더. 바탕 화면·문서를 OneDrive로 옮긴 PC에서 출력 폴더 옆에 작업 폴더를 두면
/// 중간 PNG 수십 GB가 그대로 올라간다 — 그런 곳이면 작업 폴더를 밖에 둔다 (<see cref="WorkFolders.FallbackRoot(string?)"/>).
/// </summary>
public static class SyncFolders
{
    private static IReadOnlyList<string>? _roots;

    /// <summary>이 PC에서 OneDrive가 동기화하는 폴더들 (계정마다 하나). 처음 쓸 때 한 번 찾는다.</summary>
    public static IReadOnlyList<string> Roots => _roots ??= Find();

    /// <summary><paramref name="path"/>가 동기화 폴더 자체이거나 그 안인지.</summary>
    public static bool Contains(string path, IReadOnlyList<string>? roots = null)
    {
        if (Normalize(path) is not { } full) return false;
        return (roots ?? Roots).Any(root => full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Find()
    {
        var found = new List<string>();
        // 로그인한 개인·회사 계정의 폴더 (OneDrive가 적어 두는 환경 변수)
        foreach (string name in (string[])["OneDrive", "OneDriveConsumer", "OneDriveCommercial"])
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } folder) found.Add(folder);
        // 계정을 여럿 붙였으면 환경 변수에는 일부만 있다 — 계정별 설정에서 모두 읽는다.
        try
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (accounts is not null)
            {
                foreach (string account in accounts.GetSubKeyNames())
                {
                    using var key = accounts.OpenSubKey(account);
                    if (key?.GetValue("UserFolder") is string { Length: > 0 } folder) found.Add(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return [.. found.Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string? Normalize(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
