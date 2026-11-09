# NekoVup.exe (창 화면 판)를 Native AOT로 게시한다. 결과 폴더를 통째로 복사하면 다른 PC에서도 설치 없이 실행된다.
# 설치 파일까지 만들려면 tools\pack.ps1을 쓴다.
# 필요: .NET 10 SDK, Visual Studio(또는 Build Tools)의 "C++를 사용한 데스크톱 개발" + Windows SDK.
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\publish\app'),
    # 비워 두면 프로젝트 파일에 적힌 버전을 쓴다.
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

# 링커를 찾는 스크립트가 vswhere.exe를 PATH에서 찾으므로, 없으면 Visual Studio Installer 폴더를 잠시 추가한다.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $installer 'vswhere.exe'))) {
    $env:PATH = "$installer;$env:PATH"
}

$project = Join-Path $PSScriptRoot '..\src\NekoVup.App\NekoVup.App.csproj'
$arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '-o', $Output)
if ($Version) { $arguments += "/p:Version=$Version" }
dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "게시 실패 (종료 코드 $LASTEXITCODE)" }

# 화면 리소스(.pri)가 빠지면 실행 직후 꺼진다. 빠졌는지 여기서 바로 확인한다.
if (-not (Test-Path (Join-Path $Output 'NekoVup.pri'))) { throw 'NekoVup.pri가 게시 결과에 없습니다 (EnableMsixTooling 설정 확인).' }

$size = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
'{0}  (폴더 전체 {1:N0} MB)' -f (Join-Path (Resolve-Path $Output) 'NekoVup.exe'), $size
