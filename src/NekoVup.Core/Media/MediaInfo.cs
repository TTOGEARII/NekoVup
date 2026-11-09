namespace NekoVup.Core.Media;

/// <summary>ffprobe로 읽은 입력 영상 정보. 크기는 회전 메타데이터를 적용한 "화면에 보이는" 기준이다.</summary>
public sealed record MediaInfo
{
    public required string Path { get; init; }

    /// <summary>파일 안에서의 절대 스트림 번호 (ffmpeg의 0:N).</summary>
    public required int VideoStreamIndex { get; init; }

    /// <summary>회전 적용 후 가로. ffmpeg 필터가 보는 크기와 같다.</summary>
    public required int Width { get; init; }

    /// <summary>회전 적용 후 세로.</summary>
    public required int Height { get; init; }

    public required int CodedWidth { get; init; }
    public required int CodedHeight { get; init; }

    /// <summary>0, 90, 180, 270 중 하나.</summary>
    public int Rotation { get; init; }

    /// <summary>픽셀 하나의 가로:세로 비 (회전 적용 후). DVD 영상처럼 픽셀이 정사각형이 아닌 경우 1:1이 아니다.</summary>
    public Rational SampleAspectRatio { get; init; } = new(1, 1);

    /// <summary>화면에 보이는 가로세로 비 (가로 ÷ 세로).</summary>
    public double DisplayAspect => (double)Width * SampleAspectRatio.Num / ((double)Height * SampleAspectRatio.Den);

    public required Rational Fps { get; init; }
    public double DurationSeconds { get; init; }

    public string? VideoCodec { get; init; }
    public string? PixelFormat { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorRange { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }

    /// <summary>모든 오디오 스트림의 코덱 이름. 오디오가 없으면 비어 있다.</summary>
    public IReadOnlyList<string> AudioCodecs { get; init; } = [];

    public bool HasAudio => AudioCodecs.Count > 0;

    /// <summary>자막 스트림의 코덱 이름 (ass, subrip 등).</summary>
    public IReadOnlyList<string> SubtitleCodecs { get; init; } = [];

    /// <summary>길이 × fps로 어림한 프레임 수 (진행률 표시용, 실제 수는 추출 후 센다).</summary>
    public long EstimateFrames(double? startSeconds = null, double? durationSeconds = null)
    {
        double start = Math.Max(0, startSeconds ?? 0);
        double length = Math.Max(0, DurationSeconds - start);
        if (durationSeconds is { } d) length = Math.Min(length, d);
        return Math.Max(1, (long)Math.Round(length * Fps.Value));
    }
}
