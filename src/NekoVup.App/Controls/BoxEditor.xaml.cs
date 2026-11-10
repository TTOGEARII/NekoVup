using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NekoVup.App.ViewModels;
using NekoVup.Core.Regions;
using Windows.Foundation;
using Windows.System;

namespace NekoVup.App.Controls;

/// <summary>
/// 미리보기 그림 위에서 워터마크 박스를 그리고(빈 곳에서 끌기), 옮기고(박스 안에서 끌기), 크기를 바꾸는(손잡이 끌기) 컨트롤.
/// 박스 값은 항상 영상 픽셀 좌표이고, 좌표 계산은 <see cref="BoxEditing"/>이 한다.
/// </summary>
public sealed partial class BoxEditor : UserControl
{
    private const double HandleSize = 9;
    private const double HandleReach = 9;   // 손잡이를 잡았다고 보는 거리(화면 픽셀)
    private const double StageMargin = 12;  // 그림 둘레에 남기는 여백(화면 픽셀)

    private MainViewModel? _viewModel;
    private int _videoWidth, _videoHeight;
    private double _aspect = 16.0 / 9;

    private BoxViewModel? _selected;
    private BoxGrip _grip;
    private (double X, double Y) _pressVideo;   // 누른 지점 (영상 좌표)
    private PixelRect _pressRect;               // 누른 순간의 박스
    private bool _drawStarted;

    public BoxEditor() => InitializeComponent();

    /// <summary>편집이 가능한지 (작업 중이거나 워터마크 제거를 껐으면 막는다).</summary>
    public bool IsEditable { get; set; } = true;

    /// <summary>박스와 처리 영역을 그릴지. 워터마크 제거를 끄면 감춘다 (박스 값은 남아 있어 다시 켜면 그대로 보인다).</summary>
    public bool ShowBoxes { get; set; } = true;

    public void Attach(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.OverlayChanged += (_, _) => Redraw();
    }

    /// <summary>새 영상: 픽셀 크기와 화면에 보이는 가로세로 비.</summary>
    public void SetVideo(int width, int height, double displayAspect)
    {
        (_videoWidth, _videoHeight) = (width, height);
        _aspect = displayAspect > 0 ? displayAspect : (double)width / height;
        _selected = null;
        Stage.Visibility = Visibility.Visible;
        ArrangeHost();
    }

    public void SetImage(ImageSource? image) => Picture.Source = image;

    // ───────── 배치와 좌표 변환 ─────────

    private void OnOuterSizeChanged(object sender, SizeChangedEventArgs e) => ArrangeHost();

    /// <summary>그림 영역을 화면비를 지키면서 가능한 한 크게 잡는다. 박스 좌표는 이 영역에 대해 가로·세로 따로 선형으로 대응한다.</summary>
    private void ArrangeHost()
    {
        // 화면이 패널 테두리에 붙지 않게 둘레 여백을 남긴다
        double roomWidth = Outer.ActualWidth - 2 * StageMargin, roomHeight = Outer.ActualHeight - 2 * StageMargin;
        if (_videoWidth == 0 || roomWidth < 1 || roomHeight < 1) return;
        double width = Math.Min(roomWidth, roomHeight * _aspect);
        double height = width / _aspect;
        Host.Width = Overlay.Width = width;
        Host.Height = Overlay.Height = height;
        Redraw();
    }

    private double ScaleX => Host.Width / _videoWidth;
    private double ScaleY => Host.Height / _videoHeight;

    private (double X, double Y) ToVideo(Point screen) =>
        BoxEditing.ToVideo(screen.X, screen.Y, Host.Width, Host.Height, _videoWidth, _videoHeight);

    // ───────── 그리기 ─────────

    public void Redraw()
    {
        Overlay.Children.Clear();
        if (_viewModel is null || _videoWidth == 0 || double.IsNaN(Host.Width) || !ShowBoxes) return;
        if (_selected is not null && !_viewModel.Boxes.Contains(_selected)) _selected = null;

        // 처리 영역: 노란 점선
        foreach (var region in _viewModel.RegionRects)
        {
            var shape = new Rectangle
            {
                Width = region.Width * ScaleX,
                Height = region.Height * ScaleY,
                Stroke = new SolidColorBrush(Colors.Gold),
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 3],
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(shape, region.X * ScaleX);
            Canvas.SetTop(shape, region.Y * ScaleY);
            Overlay.Children.Add(shape);
        }

        // 워터마크 박스: 주황 사각형 (선택된 것은 흰 테두리 + 손잡이)
        foreach (var box in _viewModel.Boxes)
        {
            var rect = box.ToRect();
            if (rect.IsEmpty) continue;
            bool selected = ReferenceEquals(box, _selected);
            var shape = new Rectangle
            {
                Width = rect.Width * ScaleX,
                Height = rect.Height * ScaleY,
                Stroke = new SolidColorBrush(selected ? Colors.White : Colors.OrangeRed),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(ColorHelper.FromArgb(selected ? (byte)70 : (byte)45, 255, 69, 0)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(shape, rect.X * ScaleX);
            Canvas.SetTop(shape, rect.Y * ScaleY);
            Overlay.Children.Add(shape);

            if (!selected || !IsEditable) continue;
            foreach (var (_, x, y) in BoxEditing.Handles(rect))
            {
                var handle = new Rectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = new SolidColorBrush(Colors.White),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(handle, x * ScaleX - HandleSize / 2);
                Canvas.SetTop(handle, y * ScaleY - HandleSize / 2);
                Overlay.Children.Add(handle);
            }
        }
    }

    // ───────── 포인터 ─────────

    private (BoxGrip Grip, BoxViewModel? Box) HitTest(Point screen)
    {
        if (_viewModel is null) return (BoxGrip.None, null);
        var rects = _viewModel.Boxes.Select(b => b.ToRect()).ToList();
        int selectedIndex = _selected is null ? -1 : _viewModel.Boxes.IndexOf(_selected);
        var (x, y) = ToVideo(screen);
        var (grip, index) = BoxEditing.HitTest(rects, selectedIndex, x, y, HandleReach / ScaleX, HandleReach / ScaleY);
        return (grip, index >= 0 ? _viewModel.Boxes[index] : null);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null || !IsEditable || _videoWidth == 0) return;
        var point = e.GetCurrentPoint(Overlay);
        if (!point.Properties.IsLeftButtonPressed) return;

        Focus(FocusState.Pointer);
        var (grip, box) = HitTest(point.Position);
        _grip = grip;
        _pressVideo = ToVideo(point.Position);
        _drawStarted = false;
        _selected = box;
        _pressRect = box?.ToRect() ?? default;
        Overlay.CapturePointer(e.Pointer);
        Redraw();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_viewModel is null || _videoWidth == 0) return;
        var screen = e.GetCurrentPoint(Overlay).Position;

        if (_grip == BoxGrip.None)
        {
            ProtectedCursor = InputSystemCursor.Create(IsEditable ? CursorFor(HitTest(screen).Grip) : InputSystemCursorShape.Arrow);
            return;
        }

        var now = ToVideo(screen);
        double dx = now.X - _pressVideo.X, dy = now.Y - _pressVideo.Y;

        if (_grip == BoxGrip.Draw)
        {
            // 조금이라도 끌어야 새 박스가 생긴다 (그냥 누르면 선택 해제)
            if (!_drawStarted)
            {
                if (Math.Abs(dx) * ScaleX < 4 && Math.Abs(dy) * ScaleY < 4) return;
                _drawStarted = true;
                _selected = _viewModel.AddBox(BoxEditing.FromCorners(_pressVideo.X, _pressVideo.Y, now.X, now.Y, _videoWidth, _videoHeight));
            }
            _selected!.Set(BoxEditing.FromCorners(_pressVideo.X, _pressVideo.Y, now.X, now.Y, _videoWidth, _videoHeight));
            return;
        }

        _selected?.Set(BoxEditing.Drag(_pressRect, _grip, dx, dy, _videoWidth, _videoHeight));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_grip == BoxGrip.None) return;
        _grip = BoxGrip.None;
        Overlay.ReleasePointerCaptures();
        Redraw();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_grip == BoxGrip.None) ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Delete or VirtualKey.Back && _selected is not null && IsEditable && _viewModel is not null)
        {
            _viewModel.Boxes.Remove(_selected);
            _selected = null;
            e.Handled = true;
        }
    }

    private static InputSystemCursorShape CursorFor(BoxGrip grip) => grip switch
    {
        BoxGrip.Move => InputSystemCursorShape.SizeAll,
        BoxGrip.Left or BoxGrip.Right => InputSystemCursorShape.SizeWestEast,
        BoxGrip.Top or BoxGrip.Bottom => InputSystemCursorShape.SizeNorthSouth,
        BoxGrip.TopLeft or BoxGrip.BottomRight => InputSystemCursorShape.SizeNorthwestSoutheast,
        BoxGrip.TopRight or BoxGrip.BottomLeft => InputSystemCursorShape.SizeNortheastSouthwest,
        _ => InputSystemCursorShape.Cross,
    };
}
