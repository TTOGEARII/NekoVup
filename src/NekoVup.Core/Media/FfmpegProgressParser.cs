using System.Globalization;

namespace NekoVup.Core.Media;

/// <summary>ffmpeg가 <c>-progress pipe:1</c>로 내보내는 진행 상황 한 묶음.</summary>
public readonly record struct FfmpegProgress(long Frame, TimeSpan OutTime, bool IsEnd);

/// <summary>
/// <c>-progress pipe:1 -nostats</c> 출력(key=value 줄 묶음, 묶음 끝은 <c>progress=continue|end</c>)을 읽는다.
/// 줄 단위로 <see cref="Feed"/>에 넣으면 묶음이 끝날 때마다 true와 함께 값을 돌려준다.
/// </summary>
public sealed class FfmpegProgressParser
{
    /// <summary>ffmpeg 전역 옵션으로 붙일 인자.</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["-progress", "pipe:1", "-nostats"];

    private long _frame;
    private TimeSpan _outTime;

    public bool Feed(string? line, out FfmpegProgress progress)
    {
        progress = default;
        if (string.IsNullOrEmpty(line)) return false;

        int eq = line.IndexOf('=');
        if (eq <= 0) return false;
        var key = line.AsSpan(0, eq).Trim();
        var value = line.AsSpan(eq + 1).Trim();

        if (key.SequenceEqual("frame"))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frame)) _frame = frame;
        }
        else if (key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
        {
            // out_time_ms는 이름과 달리 마이크로초 단위다 (ffmpeg의 오래된 특성).
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0)
                _outTime = TimeSpan.FromMicroseconds(us);
        }
        else if (key.SequenceEqual("progress"))
        {
            progress = new FfmpegProgress(_frame, _outTime, value.SequenceEqual("end"));
            return true;
        }

        return false;
    }
}
