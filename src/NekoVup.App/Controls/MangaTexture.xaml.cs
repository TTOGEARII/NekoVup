using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace NekoVup.App.Controls;

public enum MangaTextureKind
{
    /// <summary>스크린톤 망점. 넓은 빈 배경에 은은한 질감을 준다.</summary>
    Dots,

    /// <summary>원고지 격자. 미리보기 작업판과 끌어다 놓는 자리에 깐다.</summary>
    Grid,
}

/// <summary>
/// 만화 원고 느낌을 내는 배경 무늬. 먹색 테마 브러시로 그리므로 화이트/다크를 따라간다.
/// 무늬는 선 도형으로 만든다: 격자는 가는 선, 망점은 "길이 0인 둥근 점선"(선 굵기가 곧 점 지름)이다.
/// </summary>
public sealed partial class MangaTexture : UserControl
{
    private const double GridCell = 28, GridThickness = 1, GridOpacity = 0.10;
    private const double DotCell = 9, DotDiameter = 2.2, DotOpacity = 0.08;

    private MangaTextureKind _kind;

    public MangaTexture()
    {
        InitializeComponent();
        Apply();
    }

    public MangaTextureKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Apply();
            Rebuild(ActualWidth, ActualHeight);
        }
    }

    private void Apply()
    {
        if (_kind == MangaTextureKind.Grid)
        {
            Shape.StrokeThickness = GridThickness;
            Shape.Opacity = GridOpacity;
            Shape.StrokeDashArray = null;
            Shape.StrokeDashCap = PenLineCap.Flat;
        }
        else
        {
            Shape.StrokeThickness = DotDiameter;
            Shape.Opacity = DotOpacity;
            // 점선 간격은 선 굵기의 배수로 적는다
            Shape.StrokeDashArray = [0, DotCell / DotDiameter];
            Shape.StrokeDashCap = PenLineCap.Round;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Rebuild(e.NewSize.Width, e.NewSize.Height);

    private void Rebuild(double width, double height)
    {
        if (width < 1 || height < 1)
        {
            Shape.Data = null;
            return;
        }

        var lines = new GeometryGroup();
        if (_kind == MangaTextureKind.Grid)
        {
            for (double x = 0; x <= width; x += GridCell) Add(lines, x + 0.5, 0, x + 0.5, height);
            for (double y = 0; y <= height; y += GridCell) Add(lines, 0, y + 0.5, width, y + 0.5);
        }
        else
        {
            // 줄마다 점이 같은 자리에서 시작하도록 왼쪽 끝을 맞춘다. 오른쪽은 칸 단위로 끊어 반쪽 점이 생기지 않게 한다.
            double right = Math.Floor(width / DotCell) * DotCell;
            for (double y = DotCell / 2; y < height; y += DotCell) Add(lines, DotCell / 2, y, right, y);
        }
        Shape.Data = lines;
    }

    private static void Add(GeometryGroup group, double x1, double y1, double x2, double y2) =>
        group.Children.Add(new LineGeometry { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2) });
}
