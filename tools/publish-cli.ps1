# nekovup.exe (명령줄 판)를 Native AOT 단일 실행 파일로 게시한다.
# 필요: .NET 10 SDK, Visual Studio(또는 Build Tools)의 "C++를 사용한 데스크톱 개발" + Windows SDK.
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\publish\cli')
)

$ErrorActionPreference = 'Stop'

# 링커를 찾는 스크립트가 vswhere.exe를 PATH에서 찾으므로, 없으면 Visual Studio Installer 폴더를 잠시 추가한다.
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $installer 'vswhere.exe'))) {
    $env:PATH = "$installer;$env:PATH"
}

$project = Join-Path $PSScriptRoot '..\src\NekoVup.Cli\NekoVup.Cli.csproj'
dotnet publish $project -c Release -r win-x64 -o $Output
if ($LASTEXITCODE -ne 0) { throw "게시 실패 (종료 코드 $LASTEXITCODE)" }

Get-Item (Join-Path $Output 'nekovup.exe') | ForEach-Object { '{0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
