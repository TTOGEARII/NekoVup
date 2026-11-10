using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace NekoVup.App.Controls;

/// <summary>
/// 영상 편집 도구의 타임라인 모양으로 그린 탐색 막대: 위에 시간 눈금자, 아래에 영상 트랙(클립 한 개), 그 위를 지나는 재생 헤드.
/// 누르거나 끌면 그 시각으로 옮긴다. 처리할 구간(<see cref="RangeSeconds"/>)은 클립 위에 밝게 칠한다.
/// </summary>
public sealed partial class TimelineStrip : UserControl
{
    private const double Gutter = 40;      // 왼쪽 트랙 이름 자리
    private const double RightPad = 12;
    private const double RulerHeight = 24;
    private const double TrackTop = 28, TrackHeight = 30;

    // 눈금 간격 후보(초). 큰 눈금 사이가 화면에서 90픽셀 이상 되는 가장 작은 값을 고른다.
    private static readonly double[] Steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
    private const double MinMajorSpacing = 90;

    private double _duration = 1, _position, _range;
    private string _clipName = "";
    private Line? _playLine;
    private Polygon? _playHead;
    private bool _dragging;

    public TimelineStrip()
    {
        InitializeComponent();
        // 테마가 바뀌면 붓이 새것으로 바뀐다. 바뀐 뒤에 다시 그리도록 한 박자 늦춘다.
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(Redraw);
    }

    /// <summary>사용자가 눌러서 시각을 옮겼을 때.</summary>
    public event EventHandler? PositionChanged;

    /// <summary>영상 길이(초).</summary>
    public double Duration
    {
        get => _duration;
        set
        {
            double duration = double.IsFinite(value) && value > 0 ? value : 1;
            if (duration == _duration) return;
            _duration = duration;
            Redraw();
        }
    }

    /// <summary>재생 헤드 위치(초).</summary>
    public double Position
    {
        get => _position;
        set
        {
            double position = double.IsFinite(value) ? Math.Clamp(value, 0, _duration) : 0;
            if (position == _position) return;
            _position = position;
            PlacePlayhead();
        }
    }

    /// <summary>앞에서부터 처리할 길이(초). 0이면 영상 전체.</summary>
    public double RangeSeconds
    {
        get => _range;
        set
        {
            double range = double.IsFinite(value) && value > 0 ? value : 0;
            if (range == _range) return;
            _range = range;
            Redraw();
        }
    }

    /// <summary>클립 위에 적을 이름(파일 이름).</summary>
    public string ClipName
    {
        get => _clipName;
        set
        {
            if (value == _clipName) return;
            _clipName = value ?? "";
            Redraw();
        }
    }

    // ───────── 좌표 ─────────

    private double Span => Math.Max(Area.ActualWidth - Gutter - RightPad, 1);

    private double ToX(double seconds) => Gutter + seconds / _duration * Span;

    private double ToSeconds(double x) => Math.Clamp((x - Gutter) / Span, 0, 1) * _duration;

    // ───────── 그리기 ─────────

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        Surface.Children.Clear();
        _playLine = null;
        _playHead = null;
        double width = Area.ActualWidth, height = Area.ActualHeight;
        if (width < Gutter + RightPad + 20 || height < TrackTop + 8) return;

        // 트랙 바닥과 이름
        Add(new Rectangle { Width = width, Height = TrackHeight, Fill = TrackInk.Fill }, 0, TrackTop);
        Add(new TextBlock { Text = "V1", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = HintInk.Fill }, 12, TrackTop + 7);
        Add(new Line { X1 = 0, X2 = width, Y1 = RulerHeight + 0.5, Y2 = RulerHeight + 0.5, Stroke = LineInk.Fill, StrokeThickness = 1 }, 0, 0);
        Add(new Line { X1 = Gutter - 0.5, X2 = Gutter - 0.5, Y1 = RulerHeight, Y2 = TrackTop + TrackHeight, Stroke = LineInk.Fill, StrokeThickness = 1 }, 0, 0);

        DrawRuler();
        DrawClip();

        _playLine = new Line { Y1 = 0, Y2 = TrackTop + TrackHeight + 4, Stroke = PlayheadInk.Fill, StrokeThickness = 1.5 };
        _playHead = new Polygon { Points = [new Point(-5.5, 0), new Point(5.5, 0), new Point(5.5, 6), new Point(0, 12), new Point(-5.5, 6)], Fill = PlayheadInk.Fill };
        Surface.Children.Add(_playLine);
        Surface.Children.Add(_playHead);
        PlacePlayhead();
    }

    private void DrawRuler()
    {
        double pixelsPerSecond = Span / _duration;
        double step = Steps[^1];
        foreach (double candidate in Steps)
        {
            if (candidate * pixelsPerSecond >= MinMajorSpacing)
            {
                step = candidate;
                break;
            }
        }

        const int divisions = 5;
        double minor = step / divisions;
        int count = (int)Math.Floor(_duration / minor + 1e-6);
        for (int i = 0; i <= count; i++)
        {
            double x = Math.Round(ToX(i * minor)) + 0.5;
            bool major = i % divisions == 0;
            Add(new Line { X1 = x, X2 = x, Y1 = major ? RulerHeight - 9 : RulerHeight - 4, Y2 = RulerHeight, Stroke = major ? HintInk.Fill : LineInk.Fill, StrokeThickness = 1 }, 0, 0);
            if (!major) continue;
            // 마지막 눈금의 글자가 오른쪽 밖으로 나가지 않게 한다
            if (x + 44 > Area.ActualWidth) continue;
            Add(new TextBlock { Text = Label(i * minor, step), FontSize = 10.5, FontFamily = new FontFamily("Consolas"), Foreground = HintInk.Fill }, x + 4, 2);
        }
    }

    private void DrawClip()
    {
        double left = ToX(0), right = ToX(_duration);
        double top = TrackTop + 3, clipHeight = TrackHeight - 6;
        Add(new Rectangle { Width = right - left, Height = clipHeight, RadiusX = 3, RadiusY = 3, Fill = ClipInk.Fill }, left, top);

        // 처리할 구간: 전체면 클립 전체, 앞부분만이면 그만큼만 밝게
        double activeEnd = _range > 0 ? ToX(Math.Min(_range, _duration)) : right;
        if (activeEnd - left >= 1)
            Add(new Rectangle { Width = activeEnd - left, Height = clipHeight, RadiusX = 3, RadiusY = 3, Fill = ActiveInk.Fill }, left, top);

        double room = right - left - 16;
        if (room > 40 && _clipName.Length > 0)
        {
            Add(new TextBlock
            {
                Text = _clipName,
                FontSize = 11,
                Width = room,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = TextInk.Fill,
            }, left + 8, top + 4);
        }
    }

    private void PlacePlayhead()
    {
        if (_playLine is null || _playHead is null) return;
        double x = ToX(_position);
        _playLine.X1 = _playLine.X2 = x;
        Canvas.SetLeft(_playHead, x);
        Canvas.SetTop(_playHead, RulerHeight - 12);
    }

    private void Add(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        Surface.Children.Add(element);
    }

    /// <summary>눈금 글자. 간격이 1초보다 촘촘하면 소수점까지, 한 시간이 넘으면 시:분:초로 적는다.</summary>
    private string Label(double seconds, double step)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (_duration >= 3600) return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}");
        return step < 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds / 100}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}:{span.Seconds:00}");
    }

    // ───────── 조작 ─────────

    private void Seek(double seconds)
    {
        double before = _position;
        Position = seconds;
        if (_position != before) PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Area);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer);
        _dragging = Area.CapturePointer(e.Pointer);
        Seek(ToSeconds(point.Position.X));
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) Seek(ToSeconds(e.GetCurrentPoint(Area).Position.X));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Area.ReleasePointerCaptures();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left: Seek(_position - 1); break;
            case VirtualKey.Right: Seek(_position + 1); break;
            case VirtualKey.Home: Seek(0); break;
            case VirtualKey.End: Seek(_duration); break;
            default: return;
        }
        e.Handled = true;
    }
}
