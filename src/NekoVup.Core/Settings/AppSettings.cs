using System.Text.Json;
using System.Text.Json.Serialization;
using NekoVup.Core.Pipeline;

namespace NekoVup.Core.Settings;

/// <summary>
/// 외부 도구 위치. 설치 파일에는 FFmpeg·Video2X가 들어 있어(tools\) 그 칸이 기본값이면 내장 도구를 쓴다 — <see cref="BundledTools"/>.
/// 여기 값은 사용자가 적은 그대로 저장되고, 실제 쓸 경로는 쓰는 쪽에서 <see cref="BundledTools.Resolve(AppSettings)"/>로 정한다.
/// </summary>
public sealed class AppSettings
{
    /// <summary>ffmpeg 실행 파일. 기본값 "ffmpeg"는 내장 도구(없으면 PATH)를 뜻한다.</summary>
    public string Ffmpeg { get; set; } = "ffmpeg";

    public string Ffprobe { get; set; } = "ffprobe";

    /// <summary>ProPainter용 파이썬 (보통 ProPainter 폴더의 venv\Scripts\python.exe).</summary>
    public string Python { get; set; } = "";

    /// <summary>inference_propainter.py가 있는 폴더.</summary>
    public string ProPainterDir { get; set; } = "";

    /// <summary>video2x.exe 경로. 비어 있으면 내장 도구.</summary>
    public string Video2X { get; set; } = "";

    /// <summary>창 화면 테마: "Dark"(기본) 또는 "Light".</summary>
    public string Theme { get; set; } = "Dark";
}

public static class SettingsStore
{
    /// <summary>설정·로그를 두는 폴더 (%AppData%\NekoVup).</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoVup");

    public static string DefaultPath { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>
    /// 예전 위치 (%LocalAppData%\NekoVup). 설치 프로그램이 프로그램 파일을 두는 폴더와 같아서
    /// 설치·제거 때 함께 지워질 수 있으므로 설정을 %AppData%로 옮겼다.
    /// </summary>
    public static string LegacyPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NekoVup", "settings.json");

    /// <summary>설정 파일을 읽는다. 없거나 깨졌으면 기본값을 돌려준다.</summary>
    public static AppSettings Load(string? path = null) => Load(path, out _);

    /// <summary>
    /// 설정 파일을 읽는다. 없으면 기본값. 있는데 읽지 못했으면(손으로 고치다 틀린 JSON, 정전으로 잘린 파일) 기본값을 돌려주고
    /// 까닭을 <paramref name="error"/>에 담는다. 깨진 파일은 옆에 ".bad"로 복사해 둔다 — 기본값으로 다시 저장되면
    /// 사용자가 적어 둔 경로가 아무 말 없이 사라지기 때문이다.
    /// </summary>
    public static AppSettings Load(string? path, out string? error)
    {
        error = null;
        if (path is null)
        {
            path = DefaultPath;
            MoveLegacy(LegacyPath, path);
        }
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            if (JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.AppSettings) is { } settings)
            {
                // 손으로 고친 파일에 "python": null 같은 값이 있으면 쓰는 곳마다 오류가 난다 — 기본값으로 메운다.
                var defaults = new AppSettings();
                settings.Ffmpeg ??= defaults.Ffmpeg;
                settings.Ffprobe ??= defaults.Ffprobe;
                settings.Python ??= defaults.Python;
                settings.ProPainterDir ??= defaults.ProPainterDir;
                settings.Video2X ??= defaults.Video2X;
                settings.Theme ??= defaults.Theme;
                return settings;
            }
            error = "내용이 비어 있습니다.";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        error += KeepBrokenCopy(path);
        return new AppSettings();
    }

    /// <summary>읽지 못한 설정 파일을 옆에 복사해 둔다. 복사한 곳을 알리는 말을 돌려준다 (못 했으면 빈 문자열).</summary>
    private static string KeepBrokenCopy(string path)
    {
        string copy = path + ".bad";
        try
        {
            File.Copy(path, copy, overwrite: true);
            return $" (원래 파일은 {copy}에 남겨 두었습니다)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>새 위치에 설정이 없고 예전 위치에만 있으면 복사해 온다. 실패해도 조용히 넘어간다(기본값으로 시작).</summary>
    public static void MoveLegacy(string legacyPath, string path)
    {
        try
        {
            if (File.Exists(path) || !File.Exists(legacyPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(legacyPath, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath); // "my.json"처럼 폴더 없이 준 이름도 받는다
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 임시 이름은 저장하는 쪽마다 다르게 한다 — 앱과 CLI가(또는 두 스레드가) 동시에 저장해도 서로 쓰다 만 파일을 옮기지 않는다.
        string temp = $"{path}.{Environment.ProcessId}-{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

// 설정 파일은 손으로 고치기도 한다 — 주석과 끝의 쉼표 정도는 받아 준다.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(JobManifest))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
