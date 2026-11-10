using System.Diagnostics;
using System.Globalization;
using System.Text;
using NekoVup.Core.Runtime;
using NekoVup.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace NekoVup.App;

/// <summary>
/// GitHub Releases에서 새 버전을 확인하고 적용한다 (Velopack).
/// 설치 파일(Setup.exe)로 설치한 경우에만 동작하고, 폴더째 복사해 쓰는 경우에는 아무 일도 하지 않는다.
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/TTOGEARII/NekoVup";
    private const string ExeName = "NekoVup";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));

    /// <summary>
    /// 설치 파일(Setup.exe)로 설치했는지. 휴대용 zip을 푼 것도 Velopack은 설치된 것으로 보지만, 그 폴더는 사용자가 고른 곳
    /// (내려받기 폴더·바탕 화면일 수도 있다)이라 제거하면 폴더째 지워지고, 업데이트하면 그 폴더에서 돌던 다른 프로그램까지 강제로 닫힌다.
    /// </summary>
    public bool IsInstalled => _manager.IsInstalled && !IsPortable;

    /// <summary>휴대용(zip) 판인지.</summary>
    private bool IsPortable => _manager.IsPortable || File.Exists(PortableMarker);

    /// <summary>Velopack이 휴대용 판 폴더에 두는 표시 파일.</summary>
    private static string PortableMarker => Path.Combine(InstallRoot, ".portable");

    /// <summary>새 버전이 있으면 그 정보를, 없거나 설치형이 아니면 null을 돌려준다.</summary>
    public async Task<UpdateInfo?> CheckAsync() => IsInstalled ? await _manager.CheckForUpdatesAsync() : null;

    /// <summary>내려받아 적용하고 다시 시작한다. 성공하면 돌아오지 않는다(프로세스가 끝난다).</summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, Action<int>? progress)
    {
        Log("내려받기 시작");
        await _manager.DownloadUpdatesAsync(info, progress);
        Log("내려받기 끝");

        // 내려받는 몇 분 사이에 다른 창을 열어 작업을 시작했을 수 있다 — 적용하면 그 창도 강제로 끝난다.
        if (OtherInstancesRunning())
            throw new InvalidOperationException("다른 NekoVup 창이 열려 있어 적용하지 않았습니다. 다른 창을 모두 닫은 뒤에 다시 업데이트하세요.");

        string version = info.TargetFullRelease.Version.ToString();
        StartRestartWatchdog(version);
        using var splash = StartApplySplash(version);

        // 조용히 적용하고 다시 시작한다. 이 호출 안에서 현재 프로세스가 끝난다.
        Log("적용 시작");
        try
        {
            _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
        catch (Exception ex)
        {
            // 적용을 시작하지도 못했다. 안내 창은 항상 맨 위에 떠 있어서, 그냥 두면 3분 동안 오류 알림을 가린다.
            Log("적용 실패: " + ex.Message);
            try
            {
                if (splash is { HasExited: false }) splash.Kill();
            }
            catch (Exception kill) when (kill is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            throw;
        }
    }

    /// <summary>
    /// 이 창 말고 다른 NekoVup이 떠 있는지. 업데이트를 적용하면 Velopack이 설치 폴더에서 도는 것을 모두 강제로 끝내므로
    /// 다른 창에서 몇 시간째 돌던 작업도 아무 말 없이 죽는다.
    /// </summary>
    public static bool OtherInstancesRunning()
    {
        var processes = Process.GetProcessesByName(ExeName);
        try
        {
            return processes.Any(p => p.Id != Environment.ProcessId);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>제거 프로그램을 띄운다. 설치형에서만 쓴다.</summary>
    public static void LaunchUninstaller()
    {
        // 휴대용 판에서 Update.exe --uninstall을 돌리면 푼 폴더를 통째로 지운다 (내려받기 폴더에 바로 풀었으면 그 안의 파일 전부).
        if (File.Exists(PortableMarker))
            throw new InvalidOperationException("휴대용 판에는 제거 프로그램이 없습니다. 푼 폴더를 직접 지우세요.");
        var start = new ProcessStartInfo(Path.Combine(InstallRoot, "Update.exe"))
        {
            UseShellExecute = false,
            // 프로그램 폴더를 붙잡고 있지 않도록 작업 폴더를 밖으로 뺀다
            WorkingDirectory = Path.GetTempPath(),
        };
        start.ArgumentList.Add("--uninstall");
        Process.Start(start)?.Dispose();
    }

    private const string SplashScriptName = "splash.ps1";
    private static string SplashFolder => Path.Combine(Path.GetTempPath(), "NekoVup-splash");
    private const string WatchdogScriptName = "NekoVup-restart.cmd";
    private static string WatchdogScript => Path.Combine(Path.GetTempPath(), WatchdogScriptName);

    /// <summary>업데이트할 때 임시 폴더에 만들어 둔 안내 창·감시자 파일을 지운다 (프로그램을 제거할 때).</summary>
    public static void DeleteTempFiles()
    {
        // 하나를 못 지워도(안내 창이 아직 그림을 붙잡고 있을 때 등) 다른 것은 지운다.
        try
        {
            if (Directory.Exists(SplashFolder)) Directory.Delete(SplashFolder, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            File.Delete(WatchdogScript);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>설치 폴더. 실행 파일은 그 아래 current 폴더에 있다.</summary>
    private static string InstallRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.AppendAllText(Path.Combine(SettingsStore.DataDirectory, "update.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture) + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 적용하는 동안 보여 줄 안내 창. 프로그램 폴더가 통째로 바뀌므로 그림과 스크립트를 임시 폴더로 복사해 따로 띄운다.
    /// 새 버전이 적용되고 프로그램이 다시 뜨면 스스로 닫힌다. 그 전에라도 창을 누르거나 Esc를 치면 닫힌다 —
    /// 항상 맨 위에 뜨는 창이라, 적용이 오래 걸리거나 멈췄을 때 화면 한가운데를 계속 가리지 않게 한다.
    /// </summary>
    private static Process? StartApplySplash(string targetVersion, int timeoutSeconds = 180)
    {
        try
        {
            string marker = RestartWatchdog.VersionMarker(targetVersion);
            string source = Path.Combine(AppContext.BaseDirectory, "assets", "splash.png");
            if (!File.Exists(source)) return null;
            string folder = SplashFolder;
            Directory.CreateDirectory(folder);
            string image = Path.Combine(folder, "splash.png");
            File.Copy(source, image, overwrite: true);

            string versionFile = PsQuoted(Path.Combine(InstallRoot, "current", "sq.version"));
            string script = Path.Combine(folder, SplashScriptName);
            // Windows PowerShell 5.1은 BOM 없는 스크립트를 ANSI로 읽어 한글 경로가 깨지므로 BOM을 붙여 쓴다.
            File.WriteAllText(script, $$"""
                Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                [System.Windows.Forms.Application]::EnableVisualStyles()
                $picture = [System.Drawing.Image]::FromFile('{{PsQuoted(image)}}')
                $box = New-Object System.Windows.Forms.PictureBox
                $box.Image = $picture
                $box.SizeMode = 'AutoSize'
                $form = New-Object System.Windows.Forms.Form
                $form.FormBorderStyle = 'None'
                $form.StartPosition = 'CenterScreen'
                $form.ClientSize = New-Object System.Drawing.Size($picture.Width, $picture.Height)
                $form.TopMost = $true
                $form.ShowInTaskbar = $false
                $form.BackColor = [System.Drawing.Color]::FromArgb(23, 24, 27)
                $form.Controls.Add($box)
                $box.Add_Click({ $form.Close() })
                $form.KeyPreview = $true
                $form.Add_KeyDown({ if ($_.KeyCode -eq 'Escape') { $form.Close() } })
                $versionFile = '{{versionFile}}'
                $deadline = (Get-Date).AddSeconds({{timeoutSeconds}})
                $timer = New-Object System.Windows.Forms.Timer
                $timer.Interval = 500
                $timer.Add_Tick({
                    $applied = $false
                    try { $applied = (Test-Path $versionFile) -and (Select-String -Path $versionFile -Pattern '{{marker}}' -SimpleMatch -Quiet) } catch {}
                    $running = [bool](Get-Process {{ExeName}} -ErrorAction SilentlyContinue)
                    if (($applied -and $running) -or ((Get-Date) -gt $deadline)) { $timer.Stop(); $form.Close() }
                })
                $timer.Start()
                [void]$form.ShowDialog()
                """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // PATH에 같은 이름의 다른 파일이 앞서 있을 수 있어 시스템 폴더의 것을 바로 가리킨다.
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(File.Exists(powershell) ? powershell : "powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            };
            foreach (string argument in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-WindowStyle", "Hidden", "-File", script])
                start.ArgumentList.Add(argument);
            return Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            // 안내 창이 안 떠도 적용은 그대로 진행된다
            return null;
        }
    }

    /// <summary>
    /// PowerShell 작은따옴표 문자열 안에 넣을 수 있게 따옴표를 두 번 적는다 (사용자 이름 O'Neil 등).
    /// PowerShell은 굽은 따옴표(‘ ’ ‚ ‛)도 작은따옴표로 읽으므로 그것도 두 번 적어야 한다 — 그대로 두면 스크립트가 깨져 안내 창이 뜨지 않는다.
    /// </summary>
    private static string PsQuoted(string text)
    {
        var quoted = new StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            if (c is '\'' or '‘' or '’' or '‚' or '‛') quoted.Append(c);
            quoted.Append(c);
        }
        return quoted.ToString();
    }

    /// <summary>
    /// 적용 뒤 자동으로 다시 시작되지 않는 환경(백신이 새 실행 파일을 잠시 막는 경우)에 대비해,
    /// 바깥에서 지켜보다가 새 버전이 적용됐는데 프로그램이 떠 있지 않으면 대신 실행해 주는 감시자를 띄운다.
    /// </summary>
    private static void StartRestartWatchdog(string targetVersion)
    {
        try
        {
            string stub = Path.Combine(InstallRoot, ExeName + ".exe");
            string versionFile = Path.Combine(InstallRoot, "current", "sq.version");
            if (!File.Exists(stub)) return;

            // 기다리는 시간(3분)은 안내 창과 같다. 스크립트는 ASCII뿐이고, 경로는 아래에서 환경 변수로 넘긴다.
            File.WriteAllText(WatchdogScript, RestartWatchdog.BuildScript(targetVersion, ExeName + ".exe", RestartWatchdog.LaunchCommand));

            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // 프로그램 폴더를 작업 폴더로 물려받으면 감시자가 그 폴더를 붙잡아 교체를 막는다
                WorkingDirectory = Path.GetTempPath(),
            };
            // 스크립트를 전체 경로로 넘기면, 임시 폴더 이름에 공백과 괄호·&가 함께 있을 때 cmd가 따옴표를 벗겨 실행하지 못한다.
            // 작업 폴더가 스크립트가 있는 곳이므로 이름만 넘긴다. /d는 레지스트리에 걸린 자동 실행 명령을 건너뛴다.
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(@".\" + WatchdogScriptName);
            // 경로를 스크립트에 적으면 cmd가 콘솔 코드 페이지로 읽어 한글 등이 든 경로(사용자 이름)가 깨질 수 있다 — 환경 변수로 넘긴다.
            start.Environment[RestartWatchdog.VersionFileVariable] = versionFile;
            start.Environment[RestartWatchdog.TargetVariable] = stub;
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            // 감시자가 없어도 기본 재시작 경로는 그대로 시도된다
        }
    }
}
