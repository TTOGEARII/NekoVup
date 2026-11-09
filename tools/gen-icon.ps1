# 앱 아이콘과 설치 화면 그림을 만든다: 먹선 고양이 + 선글라스.
# 화면 속 로고(src\NekoVup.App\Controls\NekoLogo.xaml)와 같은 도형이다. 도형을 바꾸면 양쪽을 함께 고칠 것.
#
# 사용:  powershell -Sta -File tools\gen-icon.ps1
# 결과:  src\NekoVup.App\app.ico (256·64·48·32·24·16), assets\splash-setup.png
param(
    [string]$Ico = (Join-Path $PSScriptRoot '..\src\NekoVup.App\app.ico'),
    [string]$Splash = (Join-Path $PSScriptRoot '..\assets\splash-setup.png'),
    [string]$Preview = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

# 좌표계 0..100. 굵은 먹선 + 종이 흰 면.
$cat = @'
<Canvas xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="100" Height="100">
  <Path Fill="White" Stroke="#111111" StrokeThickness="7" StrokeLineJoin="Miter"
        Data="M 22,42 L 12,8 L 40,22 L 60,22 L 88,8 L 78,42 C 88,50 90,66 82,78 C 70,92 30,92 18,78 C 10,66 12,50 22,42 Z"/>
  <Path Stroke="#111111" StrokeThickness="3.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
        Data="M 6,61 L 22,63 M 6,72 L 22,69 M 94,61 L 78,63 M 94,72 L 78,69"/>
  <Path Stroke="#111111" StrokeThickness="4" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
        Data="M 50,68 L 50,72 M 41,72 Q 45.5,78 50,72 Q 54.5,78 59,72"/>
  <Path Stroke="#111111" StrokeThickness="4.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
        Data="M 24,46 L 15,42 M 76,46 L 85,42 M 46,47 L 54,47"/>
  <Path Fill="#111111" Stroke="#111111" StrokeThickness="2" StrokeLineJoin="Round"
        Data="M 23,44 L 47,44 L 46,55 Q 44,63 35,63 Q 26,63 24,55 Z M 53,44 L 77,44 L 76,55 Q 74,63 65,63 Q 56,63 54,55 Z"/>
  <Path Stroke="White" StrokeThickness="2.4" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
        Data="M 28,54 L 33,48 M 58,54 L 63,48"/>
</Canvas>
'@

function Render([string]$xaml, [int]$width, [int]$height) {
    $element = [Windows.Markup.XamlReader]::Parse($xaml)
    $element.Measure((New-Object Windows.Size $width, $height))
    $element.Arrange((New-Object Windows.Rect 0, 0, $width, $height))
    $element.UpdateLayout()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $width, $height, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($element)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

# 테두리 획이 잘리지 않게 살짝 줄여서 가운데에 놓는다.
function CatAt([int]$size) {
    return @"
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="$size" Height="$size">
  <Viewbox Margin="$([math]::Round($size * 0.04, 2))">$cat</Viewbox>
</Grid>
"@
}

# ── 아이콘: PNG 여러 장을 ICO 컨테이너로 묶는다 (Vista 이후 지원) ──
$sizes = 256, 64, 48, 32, 24, 16
$pngs = foreach ($size in $sizes) { ,(Render (CatAt $size) $size $size) }

$file = [IO.File]::Create((New-Item -ItemType File -Force $Ico).FullName)
$writer = New-Object IO.BinaryWriter $file
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([Byte]$dimension); $writer.Write([Byte]$dimension)
    $writer.Write([Byte]0); $writer.Write([Byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$pngs[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $writer.Write($png) }
$writer.Close()
"icon: $Ico ($offset bytes)"

if ($Preview) { [IO.File]::WriteAllBytes($Preview, $pngs[0]); "preview: $Preview" }

# ── 설치 화면: 종이 흰색 + 먹선 테두리 + 네 귀 재단선 ──
$splashXaml = @"
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Width="480" Height="300" Background="White">
  <Border BorderBrush="#111111" BorderThickness="3" Margin="14"/>
  <Path Stroke="#111111" StrokeThickness="2" Data="M 4,22 L 4,4 L 22,4 M 458,4 L 476,4 L 476,22 M 476,278 L 476,296 L 458,296 M 22,296 L 4,296 L 4,278"/>
  <StackPanel VerticalAlignment="Center" HorizontalAlignment="Center" Margin="0,0,0,34">
    <Viewbox Width="118" Height="118">$cat</Viewbox>
    <Border Background="#111111" Padding="12,4" Margin="0,14,0,0" HorizontalAlignment="Center">
      <TextBlock Text="NekoVup" Foreground="White" FontFamily="Segoe UI" FontWeight="Bold" FontSize="22"/>
    </Border>
  </StackPanel>
</Grid>
"@
$splashFile = (New-Item -ItemType File -Force $Splash).FullName
[IO.File]::WriteAllBytes($splashFile, (Render $splashXaml 480 300))
"splash: $Splash"
