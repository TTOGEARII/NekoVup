using CommunityToolkit.Mvvm.ComponentModel;
using NekoVup.Core.Pipeline;
using NekoVup.Core.Regions;

namespace NekoVup.App.ViewModels;

/// <summary>
/// 워터마크 박스 하나. 값은 영상 픽셀 좌표다 (숫자 입력 칸이 double을 쓰므로 double로 둔다).
/// 움직이는 워터마크면 장면마다 맞춰 둔 자리를 기억하고, 다른 장면에서는 그 사이의 자리를 보여 준다.
/// </summary>
public sealed partial class BoxViewModel : ObservableObject
{
    /// <summary>맞춰 둔 자리 (원본의 프레임 번호 → 자리). 움직이는 박스일 때만 쓴다.</summary>
    private readonly SortedList<long, PixelRect> _keys = [];

    /// <summary>지금 보고 있는 장면의 프레임.</summary>
    private long _frame;

    /// <summary>값을 우리가 옮기는 중 (사용자가 맞춘 것이 아니므로 적어 두지 않는다).</summary>
    private bool _quiet;

    [ObservableProperty]
    public partial double X { get; set; }

    [ObservableProperty]
    public partial double Y { get; set; }

    [ObservableProperty]
    public partial double Width { get; set; }

    [ObservableProperty]
    public partial double Height { get; set; }

    /// <summary>워터마크가 영상 안에서 자리를 옮기는지. 켜면 박스를 맞출 때마다 그 장면의 자리로 적어 둔다.</summary>
    [ObservableProperty]
    public partial bool Moving { get; set; }

    /// <summary>한 자리에 머물다가 다음 자리로 한 번에 옮겨 가는지 (끄면 맞춰 둔 자리 사이를 고르게 이어 간다).</summary>
    [ObservableProperty]
    public partial bool Jump { get; set; }

    /// <summary>맞춰 둔 자리가 몇 곳인지 알리는 글.</summary>
    [ObservableProperty]
    public partial string KeyText { get; set; } = "";

    public BoxViewModel()
    {
    }

    public BoxViewModel(PixelRect rect) => Set(rect);

    /// <summary>맞춰 둔 자리의 수. 자리가 바뀔 때마다(수가 같아도) 바뀌었다고 알린다 — 처리 영역을 다시 잡게.</summary>
    public int KeyCount => _keys.Count;

    /// <summary>
    /// <see cref="Set"/>으로 자리를 옮긴 뒤 한 번 알릴 때 쓰는 이름. X·Y·너비·높이가 하나씩 바뀔 때마다 처리 영역을 다시 잡으면
    /// 박스를 끄는 동안 같은 계산을 네 번씩 한다 — 넣는 동안(<see cref="Setting"/>)은 건너뛰고 이것 하나로 받는다.
    /// </summary>
    public const string PlacedProperty = "Placed";

    /// <summary><see cref="Set"/>이 값을 차례로 넣는 중.</summary>
    public bool Setting { get; private set; }

    public void Set(PixelRect rect)
    {
        var before = (X, Y, Width, Height);
        bool quiet = _quiet, setting = Setting;
        (_quiet, Setting) = (true, true);
        (X, Y, Width, Height) = (rect.X, rect.Y, rect.Width, rect.Height);
        (_quiet, Setting) = (quiet, setting);
        Record();
        if (!Setting && before != (X, Y, Width, Height)) OnPropertyChanged(PlacedProperty);
    }

    /// <summary>
    /// 정수 픽셀 사각형으로. 칸이 비어 있으면(NaN) 0으로 본다 — 움직이는 박스는 그 값을 적어 두지 않으므로
    /// 적어 둔 자리대로 돌려준다 (작업에 쓰는 자리와 화면에 그리는 자리가 같게).
    /// </summary>
    public PixelRect ToRect() => HasTrack && !Filled
        ? BoxPath.FromFrames(_keys, Jump).At(_frame)
        : new(Round(X), Round(Y), Round(Width), Round(Height));

    private bool Filled => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height);

    private static int Round(double value) => double.IsFinite(value) ? (int)Math.Round(value) : 0;

    partial void OnXChanged(double value) => Record();
    partial void OnYChanged(double value) => Record();
    partial void OnWidthChanged(double value) => Record();
    partial void OnHeightChanged(double value) => Record();

    partial void OnMovingChanged(bool value)
    {
        // 끄면 지금 자리에 고정한다. 켜면 지금 장면의 자리가 첫 위치가 된다.
        _keys.Clear();
        Record();
        Refresh();
    }

    partial void OnJumpChanged(bool value) => ShowAt(_frame);

    /// <summary>사용자가 박스를 맞췄다 — 움직이는 박스면 지금 장면의 자리로 적어 둔다.</summary>
    private void Record()
    {
        if (_quiet || !Moving || !Filled) return;
        var rect = ToRect();
        if (rect.IsEmpty) return;
        _keys[_frame] = rect;
        Refresh();
    }

    private void Refresh()
    {
        KeyText = !Moving ? ""
            : _keys.ContainsKey(_frame) ? $"자리 {_keys.Count}곳 · 이 장면에 맞춰 둠"
            : $"자리 {_keys.Count}곳";
        OnPropertyChanged(nameof(KeyCount));
    }

    /// <summary>보고 있는 장면이 바뀌었다. 움직이는 박스는 그 장면에서의 자리로 옮긴다.</summary>
    public void ShowAt(long frame)
    {
        _frame = frame;
        if (!Moving || _keys.Count == 0) return;
        bool quiet = _quiet;
        _quiet = true;
        Set(BoxPath.FromFrames(_keys, Jump).At(frame));
        _quiet = quiet;
        Refresh();
    }

    /// <summary>적어 둔 자리들을 통째로 넣는다 (명령줄로 받은 움직이는 박스).</summary>
    public void Load(BoxPath path, long frame)
    {
        _quiet = true;
        (Moving, Jump) = (true, path.Jump);
        _keys.Clear();
        for (int i = 0; i < path.Frames.Count; i++) _keys[path.Frames[i]] = path.Rects[i];
        _quiet = false;
        ShowAt(frame);
    }

    /// <summary>지금 장면보다 앞(-1)·뒤(+1)에 맞춰 둔 자리의 프레임. 없으면 null.</summary>
    public long? NeighborKey(int direction)
    {
        long? found = null;
        foreach (long frame in _keys.Keys)
        {
            if (direction > 0 && frame > _frame) return frame;
            if (direction < 0 && frame < _frame) found = frame;
        }
        return found;
    }

    /// <summary>지금 장면에 맞춰 둔 자리를 지운다 (마지막 하나는 남긴다). 지웠으면 true.</summary>
    public bool RemoveKeyHere()
    {
        if (_keys.Count <= 1 || !_keys.Remove(_frame)) return false;
        ShowAt(_frame);
        return true;
    }

    /// <summary>움직이는 박스로 다룰지 (맞춰 둔 자리가 있을 때).</summary>
    public bool HasTrack => Moving && _keys.Count > 0;

    /// <summary>영상 안에서의 움직임. 고정 박스면 한 자리짜리, 빈 박스면 null.</summary>
    public BoxPath? ToPath() => HasTrack ? BoxPath.FromFrames(_keys, Jump)
        : ToRect() is { IsEmpty: false } rect ? BoxPath.Fixed(rect)
        : null;

    /// <summary>작업 요청에 넣을 꼴 (시각 → 자리). 고정 박스면 null.</summary>
    public BoxTrack? ToTrack(double fps) => HasTrack
        ? new BoxTrack { Keys = [.. _keys.Select(k => new BoxKey(FrameGrid.TimeOf(k.Key, fps), k.Value))], Jump = Jump }
        : null;
}

/// <summary>환경 점검 결과 한 줄.</summary>
public sealed partial class CheckItemViewModel(string glyph, string name, string detail) : ObservableObject
{
    public string Glyph { get; } = glyph;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
}
