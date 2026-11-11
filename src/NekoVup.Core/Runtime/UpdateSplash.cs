using System.Globalization;
using System.Text;

namespace NekoVup.Core.Runtime;

/// <summary>업데이트 안내 창(<see cref="UpdateSplash"/>)에 넘기는 것.</summary>
public sealed record UpdateSplashOptions
{
    /// <summary>창 위쪽에 보여 줄 그림 (임시 폴더로 복사해 둔 splash.png).</summary>
    public required string ImagePath { get; init; }

    /// <summary>창 아이콘(.ico). 없으면 기본 아이콘.</summary>
    public string? IconPath { get; init; }

    /// <summary>설치 폴더의 버전 파일(current\sq.version). 여기에 새 버전이 적히면 "적용됨"이다.</summary>
    public required string VersionFile { get; init; }

    /// <summary>적용될 버전 ("1.1.0").</summary>
    public required string TargetVersion { get; init; }

    /// <summary>지금 버전 (창에 "v1.0.2 → v1.1.0"으로 보여 준다).</summary>
    public required string FromVersion { get; init; }

    /// <summary>프로그램의 프로세스 이름, 확장자 없이 ("NekoVup"). 이 이름의 프로세스가 창을 띄우면 "열렸다"로 본다.</summary>
    public required string ImageName { get; init; }

    /// <summary>업데이트를 시작한(곧 닫힐) 프로그램의 프로세스 번호. 이것이 끝나야 적용이 시작된다.</summary>
    public required int OldProcessId { get; init; }

    /// <summary>단계가 바뀔 때마다 한 줄씩 적을 파일 (update.log). 없으면 적지 않는다.</summary>
    public string? LogFile { get; init; }

    /// <summary>이 시간(초) 안에 새 버전이 열리지 않으면 "아직 끝나지 않았습니다"로 바꾸고 기다리기를 멈춘다.</summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>적용은 끝났는데 이 시간(초)이 지나도 프로그램이 안 뜨면 백신 안내를 보여 준다.</summary>
    public int SlowHintSeconds { get; init; } = 15;

    /// <summary>새 버전이 열린 뒤 이 시간(초)이 지나면 창을 닫는다.</summary>
    public int DoneCloseSeconds { get; init; } = 4;

    /// <summary>상태를 살피는 간격(ms).</summary>
    public int PollMilliseconds { get; init; } = 500;
}

/// <summary>
/// 업데이트를 적용하는 동안 보여 주는 안내 창 (PowerShell WinForms 스크립트). 프로그램 폴더가 통째로 바뀌므로 프로그램 밖에서 따로 띄운다.
/// 사용자 요청(2026-11-11): 적용 중에 아무것도 보이지 않아 멈춘 것처럼 보였고, 적용이 끝나기 전에 바로 가기로 프로그램을 열어 오류가 났다 —
/// 그래서 "닫는 중 → 적용 중(아직 열지 마세요) → 다시 여는 중(백신이면 1분) → 업데이트 끝"을 글과 진행 막대로 보여 주고,
/// 새 버전의 창이 실제로 뜬 것을 확인한 뒤에야 "끝났다"고 알리고 닫힌다. 그 전에는 창을 눌러도 닫히지 않는다 (Esc와 [닫기]만).
/// </summary>
public static class UpdateSplash
{
    /// <summary>단계 이름. 로그(update.log)에 "안내 창: 단계"로 남는다.</summary>
    public const string Closing = "closing", Applying = "applying", Launching = "launching", Slow = "slow", Done = "done", Timeout = "timeout";

    /// <summary>안내 창 스크립트 내용. UTF-8 BOM으로 저장할 것 (Windows PowerShell 5.1은 BOM 없는 파일을 ANSI로 읽어 한글이 깨진다).</summary>
    public static string BuildScript(UpdateSplashOptions options)
    {
        if (options.ImageName.Length == 0 || !options.ImageName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException($"프로세스 이름이 아닙니다: {options.ImageName}", nameof(options));
        string marker = RestartWatchdog.VersionMarker(options.TargetVersion);
        string to = PsQuoted(options.TargetVersion), from = PsQuoted(options.FromVersion);

        return string.Create(CultureInfo.InvariantCulture, $$"""
            Add-Type -AssemblyName System.Windows.Forms, System.Drawing
            [System.Windows.Forms.Application]::EnableVisualStyles()

            $logFile = '{{PsQuoted(options.LogFile ?? "")}}'
            function Note($text) {
                if ($logFile.Length -eq 0) { return }
                try { Add-Content -LiteralPath $logFile -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss ') + '안내 창: ' + $text) -Encoding UTF8 } catch {}
            }

            $oldPid = {{options.OldProcessId}}
            $versionFile = '{{PsQuoted(options.VersionFile)}}'
            $marker = '{{PsQuoted(marker)}}'
            $imageName = '{{PsQuoted(options.ImageName)}}'
            $iconPath = '{{PsQuoted(options.IconPath ?? "")}}'
            $timeoutSeconds = {{options.TimeoutSeconds}}
            $slowHintSeconds = {{options.SlowHintSeconds}}
            $doneCloseSeconds = {{options.DoneCloseSeconds}}

            $dark = [System.Drawing.Color]::FromArgb(23, 24, 27)
            $panel = [System.Drawing.Color]::FromArgb(42, 43, 48)
            $stroke = [System.Drawing.Color]::FromArgb(70, 72, 80)
            $text = [System.Drawing.Color]::FromArgb(232, 233, 237)
            $hint = [System.Drawing.Color]::FromArgb(160, 163, 170)

            $form = New-Object System.Windows.Forms.Form
            $form.Text = 'NekoVup 업데이트'
            $form.FormBorderStyle = 'FixedDialog'
            $form.MaximizeBox = $false
            $form.StartPosition = 'CenterScreen'
            $form.TopMost = $true
            $form.ShowInTaskbar = $true
            $form.BackColor = $dark
            $form.ForeColor = $text
            $form.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
            if ($iconPath.Length -gt 0) { try { $form.Icon = New-Object System.Drawing.Icon($iconPath) } catch {} }

            $width = 480
            $top = 0
            $picture = $null
            try { $picture = [System.Drawing.Image]::FromFile('{{PsQuoted(options.ImagePath)}}') } catch {}
            if ($picture) {
                $box = New-Object System.Windows.Forms.PictureBox
                $box.Image = $picture
                $box.SizeMode = 'Zoom'
                $box.Location = New-Object System.Drawing.Point(0, 0)
                $box.Size = New-Object System.Drawing.Size($width, [int][Math]::Round($picture.Height * $width / $picture.Width))
                $form.Controls.Add($box)
                $top = $box.Height
            }

            $status = New-Object System.Windows.Forms.Label
            $status.Location = New-Object System.Drawing.Point(20, ($top + 16))
            $status.Size = New-Object System.Drawing.Size(($width - 40), 26)
            $status.Font = New-Object System.Drawing.Font('Segoe UI', 11.5, [System.Drawing.FontStyle]::Bold)
            $status.ForeColor = $text
            $form.Controls.Add($status)

            $detail = New-Object System.Windows.Forms.Label
            $detail.Location = New-Object System.Drawing.Point(20, ($top + 46))
            $detail.Size = New-Object System.Drawing.Size(($width - 40), 44)
            $detail.ForeColor = $hint
            $form.Controls.Add($detail)

            $bar = New-Object System.Windows.Forms.ProgressBar
            $bar.Location = New-Object System.Drawing.Point(20, ($top + 96))
            $bar.Size = New-Object System.Drawing.Size(($width - 40), 8)
            $bar.Style = 'Marquee'
            $bar.MarqueeAnimationSpeed = 25
            $form.Controls.Add($bar)

            $versions = New-Object System.Windows.Forms.Label
            $versions.Text = 'v{{from}}  →  v{{to}}'
            $versions.Location = New-Object System.Drawing.Point(20, ($top + 116))
            $versions.Size = New-Object System.Drawing.Size(($width - 150), 28)
            $versions.ForeColor = $hint
            $versions.TextAlign = 'MiddleLeft'
            $form.Controls.Add($versions)

            $close = New-Object System.Windows.Forms.Button
            $close.Text = '닫기'
            $close.Location = New-Object System.Drawing.Point(($width - 106), ($top + 114))
            $close.Size = New-Object System.Drawing.Size(86, 30)
            $close.FlatStyle = 'Flat'
            $close.BackColor = $panel
            $close.ForeColor = $text
            $close.FlatAppearance.BorderColor = $stroke
            $close.Enabled = $false
            $close.Add_Click({ $form.Close() })
            $form.Controls.Add($close)

            $form.ClientSize = New-Object System.Drawing.Size($width, ($top + 156))
            $form.KeyPreview = $true
            $form.Add_KeyDown({ if ($_.KeyCode -eq 'Escape') { $form.Close() } })
            $form.Add_Shown({ $form.Activate() })

            $script:phase = ''
            $script:launchingSince = $null
            $script:doneAt = $null
            $started = Get-Date

            function Set-Phase($name, $title, $note) {
                if ($script:phase -eq $name) { return }
                $script:phase = $name
                Note $name
                $status.Text = $title
                $detail.Text = $note
            }

            $timer = New-Object System.Windows.Forms.Timer
            $timer.Interval = {{options.PollMilliseconds}}
            $timer.Add_Tick({
                $now = Get-Date
                if ($script:doneAt) {
                    if (($now - $script:doneAt).TotalSeconds -ge $doneCloseSeconds) { $timer.Stop(); $form.Close() }
                    return
                }
                $oldAlive = $false
                try { $oldAlive = [bool](Get-Process -Id $oldPid -ErrorAction SilentlyContinue) } catch {}
                $applied = $false
                try { if (Test-Path -LiteralPath $versionFile) { $applied = ([IO.File]::ReadAllText($versionFile)).Contains($marker) } } catch {}
                $open = $false
                try { $open = [bool](Get-Process -Name $imageName -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $oldPid -and $_.Id -ne $PID -and $_.MainWindowHandle -ne 0 }) } catch {}

                if ($applied -and $open) {
                    Set-Phase '{{Done}}' '업데이트가 끝났습니다.' ('NekoVup v{{to}}이(가) 열렸습니다. 이 창은 ' + $doneCloseSeconds + '초 뒤에 닫힙니다.')
                    $bar.Style = 'Continuous'
                    $bar.Value = 100
                    $close.Enabled = $true
                    $script:doneAt = $now
                    return
                }
                if (($now - $started).TotalSeconds -ge $timeoutSeconds) {
                    Set-Phase '{{Timeout}}' '업데이트가 아직 끝나지 않았습니다.' '잠시 뒤 바탕 화면이나 시작 메뉴의 NekoVup을 직접 열어 보세요. 그래도 열리지 않으면 설치 파일(NekoVup-win-Setup.exe)을 다시 받아 설치하세요. 설정과 ProPainter 환경은 그대로 남습니다.'
                    $bar.Style = 'Continuous'
                    $bar.Value = 0
                    $close.Enabled = $true
                    $timer.Stop()
                    return
                }
                if (-not $applied) {
                    if ($oldAlive) { Set-Phase '{{Closing}}' '프로그램을 닫는 중…' '내려받은 새 버전을 적용할 준비를 합니다.' }
                    else { Set-Phase '{{Applying}}' '새 버전을 적용하는 중… 아직 프로그램을 열지 마세요.' '파일을 바꾸는 동안 바탕 화면의 NekoVup을 열면 오류가 납니다. 끝나면 저절로 다시 열립니다.' }
                    return
                }
                if (-not $script:launchingSince) { $script:launchingSince = $now }
                if (($now - $script:launchingSince).TotalSeconds -ge $slowHintSeconds) {
                    Set-Phase '{{Slow}}' '적용이 끝났습니다. 프로그램을 다시 여는 중…' '백신이 새 파일을 검사하는 동안 1분쯤 걸릴 수 있습니다. 잠시만 기다려 주세요. 바탕 화면의 NekoVup을 직접 열어도 됩니다.'
                } else {
                    Set-Phase '{{Launching}}' '적용이 끝났습니다. 프로그램을 다시 여는 중…' '새 버전이 열리면 이 창은 저절로 닫힙니다.'
                }
            })
            $timer.Start()
            Note ('시작 (v{{from}} → v{{to}}, 닫힐 프로세스 ' + $oldPid + ')')
            [void]$form.ShowDialog()
            Note ('닫힘 (' + $script:phase + ')')
            """);
    }

    /// <summary>
    /// PowerShell 작은따옴표 문자열 안에 넣을 수 있게 따옴표를 두 번 적는다 (사용자 이름 O'Neil 등).
    /// PowerShell은 굽은 따옴표(‘ ’ ‚ ‛)도 작은따옴표로 읽으므로 그것도 두 번 적어야 한다 — 그대로 두면 스크립트가 깨져 안내 창이 뜨지 않는다.
    /// </summary>
    public static string PsQuoted(string text)
    {
        var quoted = new StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            if (c is '\'' or '‘' or '’' or '‚' or '‛') quoted.Append(c);
            quoted.Append(c);
        }
        return quoted.ToString();
    }
}
