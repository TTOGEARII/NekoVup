using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NekoVup.Core.Regions;
using Windows.Foundation;
using Windows.System;

namespace NekoVup.App.Controls;

/// <summary>
/// 영상 편집 도구의 타임라인 모양으로 그린 탐색 막대: 위에 시간 눈금자, 아래에 영상 트랙(클립 한 개), 그 위를 지나는 재생 헤드.
/// 누르거나 끌면 그 시각으로 옮긴다. 처리할 구간(<see cref="RangeStart"/>~<see cref="RangeEnd"/>)은 클립 위에 밝게 칠하고,
/// 고칠 수 있을 때(<see cref="RangeEditable"/>)는 양 끝의 손잡이를 끌어 바꾼다. I·O 키는 재생 헤드 자리를 시작·끝으로 정해 달라고 알린다.
/// </summary>
public sealed partial class TimelineStrip : UserControl
{
    private const double Gutter = 40;      // 왼쪽 트랙 이름 자리
    private const double RightPad = 12;
    private const double RulerHeight = 24;
    private const double TrackTop = 28, TrackHeight = 30;
    private const double MinRange = 0.1;   // 손잡이로 줄일 수 있는 가장 짧은 구간(초)

    private enum Drag { None, Seek, RangeStart, RangeEnd }

    // 눈금 간격 후보(초). 큰 눈금 사이가 화면에서 90픽셀 이상 되는 가장 작은 값을 고른다.
    private static readonly double[] Steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
    private const double MinMajorSpacing = 90;

    private double _duration = 1, _position, _rangeStart, _rangeEnd;
    private bool _rangeEditable;
    private string _clipName = "";
    private Line? _playLine;
    private Polygon? _playHead;
    private Drag _drag;
    private double _pressX;      // 누른 자리 (화면 x)
    private double _grabOffset;  // 손잡이 위치 - 누른 자리: 끄는 동안 이 간격을 지킨다 (누른 자리로 튀지 않게)
    private bool _moved;         // 손잡이를 누른 뒤 끌기 문턱을 넘었는지

    public TimelineStrip()
    {
        InitializeComponent();
        // 테마가 바뀌면 붓이 새것으로 바뀐다. 바뀐 뒤에 다시 그리도록 한 박자 늦춘다.
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(Redraw);
    }

    /// <summary>사용자가 눌러서 시각을 옮겼을 때.</summary>
    public event EventHandler? PositionChanged;

    /// <summary>사용자가 구간 손잡이를 끌어 구간을 바꿨을 때 (<see cref="RangeStart"/>·<see cref="RangeEnd"/>에 새 값이 있다).</summary>
    public event EventHandler? RangeChanged;

    /// <summary>I(시작, true)·O(끝, false) 키를 눌렀을 때 — 재생 헤드 자리를 구간의 시작·끝으로 정해 달라는 뜻.</summary>
    public event EventHandler<bool>? RangeMarkRequested;

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

    /// <summary>프레임 하나의 길이(초). 쉼표·마침표 키로 한 프레임씩 옮길 때 쓴다 (0이면 모른다).</summary>
    public double FrameSeconds { get; set; }

    /// <summary>처리할 구간의 시작(초). 시작과 끝이 같으면(둘 다 0) 영상 전체.</summary>
    public double RangeStart
    {
        get => _rangeStart;
        set
        {
            double start = double.IsFinite(value) && value > 0 ? value : 0;
            if (start == _rangeStart) return;
            _rangeStart = start;
            Redraw();
        }
    }

    /// <summary>처리할 구간의 끝(초).</summary>
    public double RangeEnd
    {
        get => _rangeEnd;
        set
        {
            double end = double.IsFinite(value) && value > 0 ? value : 0;
            if (end == _rangeEnd) return;
            _rangeEnd = end;
            Redraw();
        }
    }

    /// <summary>구간 손잡이를 보이고 끌 수 있게 할지 (구간을 켰고 작업 중이 아닐 때).</summary>
    public bool RangeEditable
    {
        get => _rangeEditable;
        set
        {
            if (value == _rangeEditable) return;
            _rangeEditable = value;
            Redraw();
        }
    }

    private bool HasRange => _rangeEnd > _rangeStart;

    /// <summary>구간을 그릴지: 구간이 있거나, 고칠 수 있는 상태(구간을 켰음)면 길이가 0이어도 손잡이를 보인다.</summary>
    private bool ShowsRange => HasRange || (_rangeEditable && _rangeEnd >= _rangeStart);

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

        // 처리할 구간: 전체면 클립 전체, 구간을 골랐으면 그 부분만 밝게
        double activeLeft = ShowsRange ? ToX(Math.Min(_rangeStart, _duration)) : left;
        double activeRight = ShowsRange ? ToX(Math.Min(_rangeEnd, _duration)) : right;
        if (activeRight - activeLeft >= 1)
            Add(new Rectangle { Width = activeRight - activeLeft, Height = clipHeight, RadiusX = 3, RadiusY = 3, Fill = ActiveInk.Fill }, activeLeft, top);

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

        if (ShowsRange && _rangeEditable)
        {
            DrawHandle(activeLeft, opening: true);
            DrawHandle(activeRight, opening: false);
        }
    }

    /// <summary>구간 끝의 손잡이: 세로 막대에 안쪽으로 꺾인 끝을 단 대괄호 모양 ([ 와 ]).</summary>
    private void DrawHandle(double x, bool opening)
    {
        double top = TrackTop, height = TrackHeight;
        double bar = opening ? x : x - 3;
        double tick = opening ? x : x - 8;
        Add(new Rectangle { Width = 3, Height = height, Fill = TextInk.Fill }, bar, top);
        Add(new Rectangle { Width = 8, Height = 3, Fill = TextInk.Fill }, tick, top);
        Add(new Rectangle { Width = 8, Height = 3, Fill = TextInk.Fill }, tick, top + height - 3);
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

    /// <summary>이 지점에서 누르면 무엇을 끄는지: 트랙 높이에서 손잡이 위면 그 손잡이, 아니면 재생 헤드 (눈금자에서는 늘 재생 헤드).</summary>
    private Drag HitTest(Point point)
    {
        if (!ShowsRange || !_rangeEditable || point.Y < TrackTop - 4) return Drag.Seek;
        return RangeEditing.HitTest(point.X, ToX(_rangeStart), ToX(_rangeEnd)) switch
        {
            RangeGrip.Start => Drag.RangeStart,
            RangeGrip.End => Drag.RangeEnd,
            _ => Drag.Seek,
        };
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Area);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer);
        _drag = HitTest(point.Position);
        if (!Area.CapturePointer(e.Pointer)) _drag = Drag.None;
        _pressX = point.Position.X;
        // 재생 헤드는 누른 곳으로 바로 옮긴다. 손잡이는 끌기 시작할 때까지 그대로 둔다 (누르기만 해서는 구간이 바뀌지 않게).
        _moved = _drag == Drag.Seek;
        _grabOffset = _drag switch
        {
            Drag.RangeStart => ToX(_rangeStart) - _pressX,
            Drag.RangeEnd => ToX(_rangeEnd) - _pressX,
            _ => 0,
        };
        if (_drag == Drag.Seek) DragTo(_pressX);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Area).Position;
        if (_drag == Drag.None)
        {
            ProtectedCursor = InputSystemCursor.Create(HitTest(point) == Drag.Seek ? InputSystemCursorShape.Arrow : InputSystemCursorShape.SizeWestEast);
            return;
        }
        if (!_moved && Math.Abs(point.X - _pressX) < RangeEditing.DragThreshold) return;
        _moved = true;
        DragTo(point.X + _grabOffset);
    }

    /// <summary>
    /// 끄는 대로 반영한다. 손잡이를 끌 때는 재생 헤드도 그 자리로 옮겨, 자르는 자리의 장면을 미리보기로 보며 맞출 수 있게 한다.
    /// </summary>
    private void DragTo(double x)
    {
        double seconds = ToSeconds(x);
        if (_drag == Drag.Seek)
        {
            Seek(seconds);
            return;
        }
        if (_drag is not (Drag.RangeStart or Drag.RangeEnd)) return;
        var grip = _drag == Drag.RangeStart ? RangeGrip.Start : RangeGrip.End;
        (_rangeStart, _rangeEnd) = RangeEditing.Move(grip, seconds, _rangeStart, _rangeEnd, _duration, MinRange);
        Redraw();
        RangeChanged?.Invoke(this, EventArgs.Empty);
        Seek(grip == RangeGrip.Start ? _rangeStart : _rangeEnd);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.None) return;
        // 손잡이를 누르고 움직이지 않은 채 뗐으면 그 자리를 누른 것으로 본다 (재생 헤드만 옮긴다).
        bool click = !_moved && _drag is Drag.RangeStart or Drag.RangeEnd;
        // 캡처를 풀면 PointerCaptureLost로 이 처리가 다시 불린다 — 먼저 끝난 것으로 표시해 두 번 하지 않게.
        _drag = Drag.None;
        Area.ReleasePointerCaptures();
        if (click) Seek(ToSeconds(_pressX));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left: Seek(_position - 1); break;
            case VirtualKey.Right: Seek(_position + 1); break;
            case KeyComma: StepFrame(-1); break;
            case KeyPeriod: StepFrame(+1); break;
            case VirtualKey.Home: Seek(0); break;
            case VirtualKey.End: Seek(_duration); break;
            case VirtualKey.I: RangeMarkRequested?.Invoke(this, true); break;
            case VirtualKey.O: RangeMarkRequested?.Invoke(this, false); break;
            default: return;
        }
        e.Handled = true;
    }

    // 쉼표(,)·마침표(.) 키 — VirtualKey에 이름이 없다.
    private const VirtualKey KeyComma = (VirtualKey)188, KeyPeriod = (VirtualKey)190;

    /// <summary>한 프레임 앞·뒤로. 프레임 사이에 서 있었으면 지금 보이는 프레임에서 센다.</summary>
    private void StepFrame(int direction)
    {
        if (!(FrameSeconds > 0)) return;
        double frame = Math.Ceiling(_position / FrameSeconds - 1e-6);
        Seek((frame + direction) * FrameSeconds);
    }
}
