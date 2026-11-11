using NekoVup.Core.Processes;

namespace NekoVup.Core.Pipeline;

/// <summary>여러 영상을 차례로 처리할 때의 작업 하나. <paramref name="Name"/>은 알림에 적을 이름(파일 이름).</summary>
public readonly record struct BatchEntry(string Name, string InputPath, string OutputPath);

/// <summary>
/// 여러 영상을 차례로 처리하기 전에 보는 것. 작업 하나하나는 <see cref="JobRunner"/>가 확인하지만,
/// 작업끼리 부딪치는 것(앞 작업의 결과를 뒤 작업이 덮어쓴다, 아직 처리하지 않은 원본을 덮어쓴다)은 여기서만 보인다.
/// </summary>
public static class BatchCheck
{
    /// <summary>
    /// 작업끼리 부딪치는 곳을 찾는다. 없으면 null, 있으면 사용자에게 보여 줄 글.
    /// <paramref name="inputs"/>는 목록에 있는 모든 영상의 경로다 (이번에 처리하지 않는 것도 덮어쓰면 안 된다).
    /// </summary>
    public static string? FindConflict(IReadOnlyList<BatchEntry> jobs, IEnumerable<string> inputs)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            for (int j = i + 1; j < jobs.Count; j++)
            {
                if (SamePath(jobs[i].OutputPath, jobs[j].OutputPath))
                    return $"출력 파일이 겹칩니다: '{jobs[i].Name}', '{jobs[j].Name}' → {jobs[i].OutputPath}\n한쪽의 출력 파일 이름을 바꾸세요.";
            }
        }
        var sources = inputs.ToList();
        foreach (var job in jobs)
        {
            foreach (string source in sources)
            {
                if (job.OutputPath.Length == 0 || source.Length == 0) continue;
                if (SamePath(job.OutputPath, source) || FileLinker.SameFile(job.OutputPath, source))
                    return $"'{job.Name}'의 출력 파일이 목록에 있는 영상 파일과 같습니다: {job.OutputPath}\n출력 파일 이름을 바꾸세요.";
            }
        }
        return null;
    }

    /// <summary>
    /// <paramref name="taken"/>에 없는 경로를 돌려준다. 겹치면 이름 뒤에 " (2)", " (3)"…을 붙인다.
    /// 같은 폴더의 "영상.mp4"와 "영상.avi"는 기본 출력 이름이 같아진다 — 목록에 넣을 때 갈라 둔다.
    /// </summary>
    public static string MakeUnique(string path, IEnumerable<string> taken)
    {
        var used = taken.ToList();
        if (!used.Any(other => SamePath(path, other))) return path;
        string folder = Path.GetDirectoryName(path) ?? "", name = Path.GetFileNameWithoutExtension(path), extension = Path.GetExtension(path);
        for (int number = 2; ; number++)
        {
            string candidate = Path.Combine(folder, $"{name} ({number}){extension}");
            if (!used.Any(other => SamePath(candidate, other))) return candidate;
        }
    }

    /// <summary>같은 파일을 가리키는 경로인지 (글자로만 본다 — 아직 없는 파일도 견줄 수 있게).</summary>
    private static bool SamePath(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0) return false;
        return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
