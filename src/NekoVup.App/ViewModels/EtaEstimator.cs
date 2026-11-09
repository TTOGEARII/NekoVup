using System.Diagnostics;
using System.Globalization;

namespace NekoVup.App.ViewModels;

/// <summary>전체 진행률의 최근 속도로 남은 시간을 어림한다.</summary>
public sealed class EtaEstimator
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly Stopwatch _clock = new();
    private readonly Queue<(TimeSpan Time, double Fraction)> _samples = new();

    public TimeSpan Elapsed => _clock.Elapsed;

    public void Restart()
    {
        _samples.Clear();
        _clock.Restart();
    }

    public void Stop() => _clock.Stop();

    /// <summary>진행률(0~1)을 넣으면 남은 시간 문구를 돌려준다. 아직 어림할 수 없으면 빈 문자열.</summary>
    public string Update(double fraction)
    {
        var now = _clock.Elapsed;
        _samples.Enqueue((now, fraction));
        while (_samples.Count > 2 && now - _samples.Peek().Time > Window) _samples.Dequeue();

        var (firstTime, firstFraction) = _samples.Peek();
        double seconds = (now - firstTime).TotalSeconds;
        double gained = fraction - firstFraction;
        if (seconds < 3 || gained <= 1e-6 || fraction >= 1) return "";

        double remaining = (1 - fraction) / (gained / seconds);
        return "약 " + Format(TimeSpan.FromSeconds(remaining)) + " 남음";
    }

    public static string Format(TimeSpan span)
    {
        if (span.TotalHours >= 1) return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}시간 {span.Minutes}분");
        if (span.TotalMinutes >= 1) return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}분 {span.Seconds}초");
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)span.TotalSeconds)}초");
    }
}
