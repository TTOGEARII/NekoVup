# 설치 파일을 만든다: 창 화면 판을 게시한 뒤 Velopack(vpk)으로 묶는다.
# 결과는 Releases 폴더에 생긴다 (NekoVup-win-Setup.exe, 휴대용 zip, 업데이트용 nupkg).
# 배포 워크플로(.github\workflows\release.yml)도 이 스크립트를 쓴다.
#
# 필요: tools\publish-app.ps1의 요구 사항 + vpk
#       dotnet tool install -g vpk --version 1.2.161   (프로젝트의 Velopack 패키지와 같은 버전)
param(
    # 비워 두면 프로젝트 파일에 적힌 버전을 쓴다.
    [string]$Version = '',
    [string]$Output = (Join-Path $PSScriptRoot '..\Releases')
)

$ErrorActionPreference = 'Stop'
# 아래 .NET zip 함수는 상대 경로를 PowerShell의 현재 위치가 아니라 프로세스 작업 폴더 기준으로 푼다 — 전체 경로로 바꿔 둔다.
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'src\NekoVup.App\NekoVup.App.csproj') -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+') { throw "버전을 알 수 없습니다: '$Version'" }

$publish = Join-Path $root 'publish\app'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& (Join-Path $PSScriptRoot 'publish-app.ps1') -Output $publish -Version $Version

vpk pack -u NekoVup -v $Version -r win-x64 -p $publish -e NekoVup.exe -o $Output `
    --packTitle 'NekoVup' --packAuthors 'TTOGEARII' --shortcuts 'Desktop,StartMenuRoot' `
    --icon (Join-Path $root 'src\NekoVup.App\app.ico') --splashImage (Join-Path $root 'assets\splash-setup.png')
if ($LASTEXITCODE -ne 0) { throw "설치 파일 만들기 실패 (종료 코드 $LASTEXITCODE)" }

# vpk가 만든 휴대용 zip에는 맨 위 폴더가 없다. 압축 프로그램의 "여기에 풀기"로 내려받기 폴더나 바탕 화면에 풀면
# Update.exe와 current 폴더가 그 자리에 쏟아지고, 그 폴더가 통째로 프로그램 폴더가 된다. 모든 항목을 NekoVup 폴더 안으로 옮겨 다시 묶는다.
# (assets.win.json에는 파일 이름만 있어 다시 묶어도 올리기에 지장이 없다.)
$portable = Join-Path $Output 'NekoVup-win-Portable.zip'
if (Test-Path $portable) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $staging = Join-Path ([IO.Path]::GetTempPath()) ('nekovup-portable-' + [guid]::NewGuid().ToString('N'))
    # 새 zip은 옆 이름으로 다 만든 뒤에 바꿔 놓는다 — 중간에 실패해도 원래 zip이 지워지거나 반쯤 쓰인 채 남지 않게.
    $rezipped = "$portable.tmp"
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory($portable, (Join-Path $staging 'NekoVup'))
        if (Test-Path $rezipped) { Remove-Item $rezipped -Force }
        # 항목 이름은 직접 짓는다 — Windows PowerShell 5.1(.NET Framework)의 CreateFromDirectory는 경로 구분자를 역슬래시로 써서
        # zip 규격(슬래시)에 어긋나고, 일부 압축 프로그램은 폴더를 만들지 않고 "NekoVup\current\…"라는 이름의 파일로 푼다.
        $archive = [IO.Compression.ZipFile]::Open($rezipped, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $base = $staging.TrimEnd('\') + '\'
            Get-ChildItem $staging -Recurse -File -Force | ForEach-Object {
                $name = $_.FullName.Substring($base.Length).Replace('\', '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally {
            $archive.Dispose()
        }
        # 백신 검사 등이 방금 쓴 .tmp를 잠깐 붙잡고 있으면 바꿔 놓기가 실패한다 — 몇 번 더 해 본다.
        for ($attempt = 1; ; $attempt++) {
            try { Move-Item $rezipped $portable -Force; break }
            catch { if ($attempt -ge 10) { throw }; Start-Sleep -Milliseconds 500 }
        }
    }
    finally {
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
        # Move-Item -Force는 대상을 먼저 지운다. 바꿔 놓기에 실패해 원래 zip이 없으면 .tmp가 하나뿐인 완성본이다 — 지우지 않는다.
        if (Test-Path $rezipped) {
            if (Test-Path $portable) { Remove-Item $rezipped -Force }
            else { Write-Warning "휴대용 zip을 제자리에 놓지 못했습니다. 새로 묶은 파일은 여기 있습니다: $rezipped" }
        }
    }
}

Get-ChildItem $Output -File | ForEach-Object { '{0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name }
