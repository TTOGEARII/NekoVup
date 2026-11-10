using Windows.Win32;

namespace NekoVup.Core.Processes;

/// <summary>프레임 파일을 조각 폴더로 "복사 없이" 가져오기 위한 하드링크.</summary>
public static class FileLinker
{
    /// <summary>
    /// target을 가리키는 하드링크를 linkPath에 만든다. 하드링크를 못 쓰는 볼륨(FAT, 다른 드라이브 등)이면 복사한다.
    /// </summary>
    /// <returns>하드링크로 만들었으면 true, 복사했으면 false.</returns>
    public static bool LinkOrCopy(string target, string linkPath)
    {
        if (File.Exists(linkPath)) File.Delete(linkPath);
        if (PInvoke.CreateHardLink(Extended(linkPath), Extended(target)))
            return true;

        File.Copy(target, linkPath, overwrite: true);
        return false;
    }

    /// <summary>
    /// 두 경로가 같은 파일인지. 적은 글자가 달라도(드라이브 문자와 네트워크 경로, subst·정션, 8.3 짧은 이름, \\?\ 접두사)
    /// 같은 파일이면 true다. 하나라도 없거나 열어 볼 수 없으면 false.
    /// </summary>
    public static bool SameFile(string first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            // 둘을 함께 열어 둔 채로 비교한다 (닫힌 파일의 번호는 다른 파일이 다시 쓸 수 있다).
            const FileShare share = FileShare.ReadWrite | FileShare.Delete;
            using var a = File.OpenHandle(first, FileMode.Open, FileAccess.Read, share);
            using var b = File.OpenHandle(second, FileMode.Open, FileAccess.Read, share);
            if (!PInvoke.GetFileInformationByHandle(a, out var x) || !PInvoke.GetFileInformationByHandle(b, out var y)) return false;
            // 파일 번호를 주지 않는 파일 시스템은 0을 돌려준다 — 그때는 알 수 없다.
            if ((x.nFileIndexHigh | x.nFileIndexLow) == 0) return false;
            return x.dwVolumeSerialNumber == y.dwVolumeSerialNumber && x.nFileIndexHigh == y.nFileIndexHigh && x.nFileIndexLow == y.nFileIndexLow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // Win32 API를 직접 부를 때는 긴 경로(260자 이상)에 \\?\ 접두사가 필요하다.
    private static string Extended(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
