using System.Globalization;

namespace NekoVup.Core.Media;

/// <summary>
/// 사람이 읽고 적는 시각. 적을 때는 분:초("1:23.5"), 한 시간부터는 시:분:초("1:02:03").
/// 읽을 때는 그 모양과 "1분 23초"·"1m23s" 같은 단위 표기를 받고, 숫자만 적으면 초로 본다.
/// </summary>
public static class TimeText
{
    /// <summary>분:초로 적는다. 소수는 둘째 자리까지, 끝의 0은 뗀다 — <see cref="TryParse"/>로 다시 읽으면 같은 값이다.</summary>
    public static string Format(double seconds)
    {
        var (hours, minutes, secs, fraction) = Split(seconds, 100);
        string tail = fraction == 0 ? "" : fraction % 10 == 0 ? $".{fraction / 10}" : $".{fraction:00}";
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}{tail}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}{tail}");
    }

    /// <summary>길이를 말로 적는다: "45초", "1분 23.5초", "1시간 2분 3초". 소수는 첫째 자리까지.</summary>
    public static string Length(double seconds)
    {
        var (hours, minutes, secs, fraction) = Split(seconds, 10);
        string tail = fraction == 0 ? "" : $".{fraction}";
        var parts = new List<string>(3);
        if (hours > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{hours}시간"));
        if (minutes > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{minutes}분"));
        if (secs > 0 || fraction > 0 || parts.Count == 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{secs}{tail}초"));
        return string.Join(' ', parts);
    }

    /// <summary>적은 시각을 초로 읽는다. 읽을 수 없으면 false.</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        double total = text.Contains(':') ? ParseClock(text) : ParseUnits(text);
        if (!double.IsFinite(total) || total < 0) return false;
        seconds = total;
        return true;
    }

    /// <summary>"분:초", "시:분:초". 소수는 마지막 칸(초)에만 쓸 수 있다.</summary>
    private static double ParseClock(string text)
    {
        string[] parts = text.Split(':');
        if (parts.Length > 3) return double.NaN;
        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            double value = Number(parts[i].Trim(), fraction: i == parts.Length - 1);
            if (double.IsNaN(value)) return double.NaN;
            total = total * 60 + value;
        }
        return total;
    }

    /// <summary>
    /// "1시간 2분 3초", "1h2m3s", "90". 단위 없는 숫자는 맨 끝에만 올 수 있고 앞 단위의 바로 아래 단위다 ("1분 30" = 1분 30초).
    /// "1 30"처럼 무엇인지 알 수 없는 것은 읽지 않는다.
    /// </summary>
    private static double ParseUnits(string text)
    {
        double total = 0, next = 1, last = double.PositiveInfinity;
        int at = 0;
        bool any = false;
        while (at < text.Length)
        {
            if (char.IsWhiteSpace(text[at])) { at++; continue; }
            if (double.IsNaN(next)) return double.NaN;
            int start = at;
            while (at < text.Length && (char.IsAsciiDigit(text[at]) || text[at] == '.')) at++;
            double value = Number(text[start..at], fraction: true);
            if (double.IsNaN(value)) return double.NaN;

            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            start = at;
            while (at < text.Length && !char.IsAsciiDigit(text[at]) && !char.IsWhiteSpace(text[at]) && text[at] != '.') at++;
            string name = text[start..at].ToLowerInvariant();
            double unit = name switch
            {
                "" => next,
                "시간" or "시" or "h" => 3600,
                "분" or "m" => 60,
                "초" or "s" => 1,
                _ => double.NaN,
            };
            // 큰 단위부터 한 번씩만 ("1분 2시간", "1분 5분"은 읽지 않는다)
            if (double.IsNaN(unit) || unit >= last) return double.NaN;
            last = unit;
            total += value * unit;
            // 단위 없는 숫자나 초 뒤에는 더 올 것이 없다.
            next = name.Length == 0 || unit < 60 ? double.NaN : unit >= 3600 ? 60 : 1;
            any = true;
        }
        return any ? total : double.NaN;
    }

    private static double Number(string text, bool fraction)
    {
        var style = fraction ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
        return text.Length > 0 && double.TryParse(text, style, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
    }

    private static (long Hours, long Minutes, long Seconds, long Fraction) Split(double seconds, int perSecond)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        long units = (long)Math.Round(seconds * perSecond, MidpointRounding.AwayFromZero);
        long whole = units / perSecond;
        return (whole / 3600, whole / 60 % 60, whole % 60, units % perSecond);
    }
}
