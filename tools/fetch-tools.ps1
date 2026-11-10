# 설치 파일에 함께 넣을 외부 도구(FFmpeg, Video2X)를 내려받아 build\tools 아래에 정리한다.
# publish-app.ps1이 게시 결과의 tools\ 폴더로 복사하고, 프로그램은 설정에 도구 경로가 비어 있으면 이 폴더의 도구를 쓴다
# (src\NekoVup.Core\Settings\BundledTools.cs).
#
# 판은 고정돼 있고 내려받은 파일은 SHA-256으로 확인한다. 판을 올리려면 아래 표의 주소·해시를 바꾼다.
# 이미 같은 판이 풀려 있으면 아무것도 하지 않는다.
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\build\tools'),
    [string]$Downloads = (Join-Path $PSScriptRoot '..\build\downloads')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$tools = @(
    @{
        Name = 'ffmpeg'
        Version = '8.1.2'
        Url = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip'
        Sha256 = 'DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC'
        # 압축 안 경로 → tools\ffmpeg\ 안 경로. ffplay는 쓰지 않아 뺀다.
        Files = @{
            'ffmpeg-8.1.2-essentials_build/bin/ffmpeg.exe'  = 'ffmpeg.exe'
            'ffmpeg-8.1.2-essentials_build/bin/ffprobe.exe' = 'ffprobe.exe'
            'ffmpeg-8.1.2-essentials_build/LICENSE'         = 'LICENSE.txt'
            'ffmpeg-8.1.2-essentials_build/README.txt'      = 'README.txt'
        }
    },
    @{
        Name = 'video2x'
        Version = '6.4.0'
        Url = 'https://github.com/k4yt3x/video2x/releases/download/6.4.0/video2x-windows-amd64.zip'
        Sha256 = '0337B6DCAE2BAD2FD13E43FBDB73EF728D2F6289F37CC3617E87AC3A1A5CF793'
        # 실행에 필요한 것만: exe, dll, 모델. 헤더·cmake·lib·RIFE(프레임 보간, 안 씀) 모델은 뺀다.
        Include = '^(video2x\.exe|[^/]+\.dll|models/(libplacebo|realcugan|realesrgan)/.+)$'
    }
)

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force $Output, $Downloads | Out-Null
$Output = (Resolve-Path $Output).Path

$stamp = Join-Path $Output 'versions.txt'
$wanted = ($tools | ForEach-Object { '{0} {1}' -f $_.Name, $_.Version }) -join "`n"
if ((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $wanted) -and
    (Test-Path (Join-Path $Output 'ffmpeg\ffmpeg.exe')) -and (Test-Path (Join-Path $Output 'video2x\video2x.exe'))) {
    "도구가 이미 준비되어 있습니다: $Output"
    return
}

foreach ($tool in $tools) {
    $zip = Join-Path $Downloads ('{0}-{1}.zip' -f $tool.Name, $tool.Version)
    if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $tool.Sha256) {
        "내려받는 중: $($tool.Url)"
        $partial = "$zip.part"
        curl.exe -sSL --fail --retry 3 -o $partial $tool.Url
        if ($LASTEXITCODE -ne 0) { throw "내려받기 실패 ($($tool.Name), curl 종료 코드 $LASTEXITCODE)" }
        $hash = (Get-FileHash $partial -Algorithm SHA256).Hash
        if ($hash -ne $tool.Sha256) { Remove-Item $partial; throw "$($tool.Name) 파일의 해시가 다릅니다: $hash (기대 $($tool.Sha256))" }
        Move-Item $partial $zip -Force
    }

    $dir = Join-Path $Output $tool.Name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null

    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $count = 0
        foreach ($entry in $archive.Entries) {
            if ($entry.Name -eq '') { continue }   # 폴더 항목
            $target = $null
            if ($tool.Files) { $target = $tool.Files[$entry.FullName] }
            elseif ($entry.FullName -match $tool.Include) { $target = $entry.FullName }
            if (-not $target) { continue }
            $path = Join-Path $dir ($target -replace '/', '\')
            New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $true)
            $count++
        }
    }
    finally { $archive.Dispose() }
    '{0} {1}: {2}개 파일, {3:N0} MB' -f $tool.Name, $tool.Version, $count, ((Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
}

# 함께 배포하는 프로그램의 라이선스 안내. Video2X 압축 파일에는 라이선스 본문이 없어 출처와 조건을 적어 둔다.
@"
NekoVup과 함께 설치되는 프로그램
================================

FFmpeg 8.1.2 (tools\ffmpeg)
  빌드: https://www.gyan.dev/ffmpeg/builds/  (essentials build)
  라이선스: GNU GPL v3 — 본문은 tools\ffmpeg\LICENSE.txt
  소스 코드: https://ffmpeg.org/download.html  (빌드에 쓰인 구성은 tools\ffmpeg\README.txt)

Video2X 6.4.0 (tools\video2x)
  저장소·소스 코드: https://github.com/k4yt3x/video2x  (태그 6.4.0)
  라이선스: GNU AGPL v3 — https://github.com/k4yt3x/video2x/blob/6.4.0/LICENSE
  함께 들어 있는 모델: Real-ESRGAN(BSD-3), Real-CUGAN(MIT), Anime4K(MIT), ncnn(BSD-3)

NekoVup은 이 프로그램들을 별도 프로세스로 실행할 뿐, 코드를 포함하거나 링크하지 않습니다.
"@ | Out-File (Join-Path $Output 'THIRD-PARTY.txt') -Encoding utf8

$wanted | Out-File $stamp -Encoding ascii
"도구 준비 끝: $Output"
