using System.Text.Json;
using System.Text.Json.Serialization;
using NekoVup.Core.Pipeline;

namespace NekoVup.Core.Settings;

/// <summary>외부 도구 위치. 이 프로그램은 도구를 내장하지 않고 설치된 것을 불러 쓴다.</summary>
public sealed class AppSettings
{
    /// <summary>ffmpeg 실행 파일. PATH에 있으면 이름만 적어도 된다.</summary>
    public string Ffmpeg { get; set; } = "ffmpeg";

    public string Ffprobe { get; set; } = "ffprobe";

    /// <summary>ProPainter용 파이썬 (보통 ProPainter 폴더의 venv\Scripts\python.exe).</summary>
    public string Python { get; set; } = "";

    /// <summary>inference_propainter.py가 있는 폴더.</summary>
    public string ProPainterDir { get; set; } = "";

    /// <summary>video2x.exe 경로.</summary>
    public string Video2X { get; set; } = "";

    /// <summary>창 화면 테마: "Light"(종이 흰색) 또는 "Dark".</summary>
    public string Theme { get; set; } = "Light";
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
    public static AppSettings Load(string? path = null)
    {
        if (path is null)
        {
            path = DefaultPath;
            MoveLegacy(LegacyPath, path);
        }
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new AppSettings();
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
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings));
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(JobManifest))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
