using System.Diagnostics;
using System.Globalization;
using System.Text;
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

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>새 버전이 있으면 그 정보를, 없거나 설치형이 아니면 null을 돌려준다.</summary>
    public async Task<UpdateInfo?> CheckAsync() => _manager.IsInstalled ? await _manager.CheckForUpdatesAsync() : null;

    /// <summary>내려받아 적용하고 다시 시작한다. 성공하면 돌아오지 않는다(프로세스가 끝난다).</summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, Action<int>? progress)
    {
        Log("내려받기 시작");
        await _manager.DownloadUpdatesAsync(info, progress);
        Log("내려받기 끝");

        string version = info.TargetFullRelease.Version.ToString();
        StartRestartWatchdog(version);
        StartApplySplash(version);

        // 조용히 적용하고 다시 시작한다. 이 호출 안에서 현재 프로세스가 끝난다.
        Log("적용 시작");
        _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    /// <summary>제거 프로그램을 띄운다. 설치형에서만 쓴다.</summary>
    public static void LaunchUninstaller()
    {
        var start = new ProcessStartInfo(Path.Combine(InstallRoot, "Update.exe"))
        {
            UseShellExecute = false,
            // 프로그램 폴더를 붙잡고 있지 않도록 작업 폴더를 밖으로 뺀다
            WorkingDirectory = Path.GetTempPath(),
        };
        start.ArgumentList.Add("--uninstall");
        Process.Start(start)?.Dispose();
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
    /// 새 버전이 적용되고 프로그램이 다시 뜨면 스스로 닫힌다.
    /// </summary>
    private static void StartApplySplash(string targetVersion, int timeoutSeconds = 180)
    {
        try
        {
            string source = Path.Combine(AppContext.BaseDirectory, "assets", "splash.png");
            if (!File.Exists(source)) return;
            string folder = Path.Combine(Path.GetTempPath(), "NekoVup-splash");
            Directory.CreateDirectory(folder);
            string image = Path.Combine(folder, "splash.png");
            File.Copy(source, image, overwrite: true);

            string versionFile = Path.Combine(InstallRoot, "current", "sq.version");
            string script = Path.Combine(folder, "splash.ps1");
            // Windows PowerShell 5.1은 BOM 없는 스크립트를 ANSI로 읽어 한글 경로가 깨지므로 BOM을 붙여 쓴다.
            File.WriteAllText(script, $$"""
                Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                [System.Windows.Forms.Application]::EnableVisualStyles()
                $picture = [System.Drawing.Image]::FromFile('{{image}}')
                $box = New-Object System.Windows.Forms.PictureBox
                $box.Image = $picture
                $box.SizeMode = 'AutoSize'
                $form = New-Object System.Windows.Forms.Form
                $form.FormBorderStyle = 'None'
                $form.StartPosition = 'CenterScreen'
                $form.ClientSize = New-Object System.Drawing.Size($picture.Width, $picture.Height)
                $form.TopMost = $true
                $form.ShowInTaskbar = $false
                $form.BackColor = [System.Drawing.Color]::White
                $form.Controls.Add($box)
                $versionFile = '{{versionFile}}'
                $deadline = (Get-Date).AddSeconds({{timeoutSeconds}})
                $timer = New-Object System.Windows.Forms.Timer
                $timer.Interval = 500
                $timer.Add_Tick({
                    $applied = $false
                    try { $applied = (Test-Path $versionFile) -and (Select-String -Path $versionFile -Pattern '<version>{{targetVersion}}' -SimpleMatch -Quiet) } catch {}
                    $running = [bool](Get-Process {{ExeName}} -ErrorAction SilentlyContinue)
                    if (($applied -and $running) -or ((Get-Date) -gt $deadline)) { $timer.Stop(); $form.Close() }
                })
                $timer.Start()
                [void]$form.ShowDialog()
                """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            };
            foreach (string argument in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-STA", "-WindowStyle", "Hidden", "-File", script])
                start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // 안내 창이 안 떠도 적용은 그대로 진행된다
        }
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

            // timeout은 콘솔이 없으면 실패할 수 있어 ping으로 기다린다.
            // PATH에 다른 find가 앞서 있는 환경이 있어 시스템 도구는 절대 경로로 부른다.
            string script = Path.Combine(Path.GetTempPath(), "NekoVup-restart.cmd");
            // 스크립트를 UTF-8로 쓰므로 한글이 든 경로도 읽히게 코드 페이지를 먼저 바꾼다.
            File.WriteAllText(script, $"""
                @echo off
                chcp 65001 >nul
                set "sys=%SystemRoot%\System32"
                for /l %%i in (1,1,60) do (
                  "%sys%\findstr.exe" /c:"<version>{targetVersion}" "{versionFile}" >nul 2>&1 && goto applied
                  "%sys%\ping.exe" -n 2 127.0.0.1 >nul
                )
                :applied
                "%sys%\ping.exe" -n 5 127.0.0.1 >nul
                for /l %%j in (1,1,36) do (
                  "%sys%\tasklist.exe" /fi "imagename eq {ExeName}.exe" | "%sys%\find.exe" /i "{ExeName}.exe" >nul && goto end
                  start "" "{stub}"
                  "%sys%\ping.exe" -n 11 127.0.0.1 >nul
                )
                :end
                """);

            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                // 프로그램 폴더를 작업 폴더로 물려받으면 감시자가 그 폴더를 붙잡아 교체를 막는다
                WorkingDirectory = Path.GetTempPath(),
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(script);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // 감시자가 없어도 기본 재시작 경로는 그대로 시도된다
        }
    }
}
