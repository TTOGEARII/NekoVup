using CommunityToolkit.Mvvm.ComponentModel;
using NekoVup.Core.Regions;

namespace NekoVup.App.ViewModels;

/// <summary>워터마크 박스 하나. 값은 영상 픽셀 좌표다 (숫자 입력 칸이 double을 쓰므로 double로 둔다).</summary>
public sealed partial class BoxViewModel : ObservableObject
{
    [ObservableProperty]
    public partial double X { get; set; }

    [ObservableProperty]
    public partial double Y { get; set; }

    [ObservableProperty]
    public partial double Width { get; set; }

    [ObservableProperty]
    public partial double Height { get; set; }

    public BoxViewModel()
    {
    }

    public BoxViewModel(PixelRect rect) => Set(rect);

    public void Set(PixelRect rect) => (X, Y, Width, Height) = (rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>정수 픽셀 사각형으로. 칸이 비어 있으면(NaN) 0으로 본다.</summary>
    public PixelRect ToRect() => new(Round(X), Round(Y), Round(Width), Round(Height));

    private static int Round(double value) => double.IsFinite(value) ? (int)Math.Round(value) : 0;
}

/// <summary>환경 점검 결과 한 줄.</summary>
public sealed partial class CheckItemViewModel(string glyph, string name, string detail) : ObservableObject
{
    public string Glyph { get; } = glyph;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
}
