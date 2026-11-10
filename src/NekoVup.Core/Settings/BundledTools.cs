namespace NekoVup.Core.Settings;

/// <summary>
/// 설치 파일에 함께 들어 있는 도구(프로그램 폴더의 <c>tools\</c>). 설정에 경로가 비어 있거나 기본 이름("ffmpeg")뿐이거나
/// 적힌 파일이 사라졌으면 이 도구를 쓴다. 사용자가 다른 곳의 도구를 골라 적었고 그 파일이 있으면 그쪽을 쓴다.
/// 개발 중(dotnet run)처럼 tools 폴더가 없으면 아무것도 바꾸지 않는다.
/// </summary>
public static class BundledTools
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "tools");

    public static string? Ffmpeg => Existing(Path.Combine(Root, "ffmpeg", "ffmpeg.exe"));
    public static string? Ffprobe => Existing(Path.Combine(Root, "ffmpeg", "ffprobe.exe"));
    public static string? Video2X => Existing(Path.Combine(Root, "video2x", "video2x.exe"));

    public static bool HasFfmpeg => Ffmpeg is not null && Ffprobe is not null;
    public static bool HasVideo2X => Video2X is not null;

    /// <summary>설정의 도구 경로를 실제로 쓸 경로로 바꾼 사본. 설정 파일에 저장되는 값은 그대로 둔다.</summary>
    public static AppSettings Resolve(AppSettings settings) => Resolve(settings, Ffmpeg, Ffprobe, Video2X);

    public static AppSettings Resolve(AppSettings settings, string? ffmpeg, string? ffprobe, string? video2x) => new()
    {
        Ffmpeg = Pick(settings.Ffmpeg, "ffmpeg", ffmpeg),
        Ffprobe = Pick(settings.Ffprobe, "ffprobe", ffprobe),
        Video2X = Pick(settings.Video2X, "", video2x),
        Python = settings.Python,
        ProPainterDir = settings.ProPainterDir,
        Theme = settings.Theme,
    };

    /// <summary>
    /// 사용자 값이 비었거나 기본 이름이면 내장 도구. 경로로 적혀 있는데 파일이 없으면(다른 PC에서 가져온 설정 등) 내장 도구.
    /// 그 밖에는 사용자 값. 내장 도구가 없으면 언제나 사용자 값.
    /// </summary>
    public static string Pick(string configured, string defaultName, string? bundled)
    {
        if (bundled is null) return configured;
        string value = configured.Trim();
        if (value.Length == 0 || value.Equals(defaultName, StringComparison.OrdinalIgnoreCase)) return bundled;
        bool looksLikePath = value.Contains('\\') || value.Contains('/') || value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (looksLikePath && !File.Exists(value)) return bundled;
        return value;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;
}
