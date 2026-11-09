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

Get-ChildItem $Output -File | ForEach-Object { '{0,8:N1} MB  {1}' -f ($_.Length / 1MB), $_.Name }
