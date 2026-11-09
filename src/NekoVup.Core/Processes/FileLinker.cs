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

    // Win32 API를 직접 부를 때는 긴 경로(260자 이상)에 \\?\ 접두사가 필요하다.
    private static string Extended(string path)
    {
        path = Path.GetFullPath(path);
        if (path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }
}
