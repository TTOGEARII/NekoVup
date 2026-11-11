using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NekoVup.Core.Media;

namespace NekoVup.App.ViewModels;

/// <summary>목록에 올린 영상의 상태.</summary>
public enum QueueState
{
    /// <summary>아직 처리하지 않았다 (시작을 누르면 처리한다)</summary>
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// 목록에 올린 영상 하나 (사용자 요청: 영상을 여러 개 올려 차례로 렌더링).
/// 박스·구간·출력 파일은 영상마다 따로 가진다 — 다른 영상을 고르면 여기에 넣어 두었다가 다시 고를 때 꺼낸다.
/// 제거 방식·업스케일 같은 옵션은 모든 영상이 함께 쓴다.
/// </summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(MediaInfo info, string infoText, string outputPath)
    {
        Info = info;
        Name = System.IO.Path.GetFileName(info.Path);
        InfoText = infoText;
        OutputPath = outputPath;
        PreviewSeconds = Math.Min(Math.Max(info.DurationSeconds, 0.1) / 2, 60);
    }

    public MediaInfo Info { get; }

    public string Path => Info.Path;

    public string Name { get; }

    public string InfoText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    public partial QueueState State { get; set; }

    /// <summary>끝난 뒤의 한 줄 (걸린 시간, 실패한 까닭).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    [NotifyPropertyChangedFor(nameof(Tip))]
    public partial string Detail { get; set; } = "";

    /// <summary>처리하는 동안의 진행률(0~100).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial int Percent { get; set; }

    /// <summary>지금 화면에 열려 있는 영상인지.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>목록에서 이름 아래에 보이는 한 줄.</summary>
    public string StatusLine => State switch
    {
        QueueState.Running => string.Create(CultureInfo.InvariantCulture, $"처리 중 · {Percent}%"),
        QueueState.Done => "✔ 완료",
        QueueState.Failed => "✖ 실패",
        QueueState.Cancelled => "취소됨",
        _ => "대기",
    };

    /// <summary>목록의 줄에 마우스를 올리면 보이는 글.</summary>
    public string Tip => Detail.Length > 0 ? $"{Path}\n{InfoText}\n{Detail}" : $"{Path}\n{InfoText}";

    // ───────── 다른 영상을 보는 동안 넣어 두는 것 ─────────

    internal List<BoxViewModel> Boxes { get; set; } = [];

    internal bool RangeOn { get; set; }

    internal double RangeStart { get; set; }

    internal double RangeEnd { get; set; }

    internal string OutputPath { get; set; }

    internal double PreviewSeconds { get; set; }

    /// <summary>이 영상을 처리한 결과 파일 (빠른 미리보기는 넣어 두지 않는다).</summary>
    internal string? ResultPath { get; set; }

    internal MainViewModel.ResultWindow? ResultWindow { get; set; }
}
