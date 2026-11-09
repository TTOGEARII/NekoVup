using System.Globalization;

namespace NekoVup.Core.Media;

/// <summary>
/// 프레임레이트 같은 유리수. ffmpeg에 넘길 때 "30000/1001" 같은 비율을 소수로 바꾸지 않고 그대로 유지하기 위한 형식이다.
/// </summary>
public readonly record struct Rational(long Num, long Den)
{
    public bool IsValid => Num > 0 && Den > 0;

    public double Value => Den == 0 ? 0 : (double)Num / Den;

    /// <summary>"30000/1001", "25", "29.97" 형태를 읽는다. "0/0"은 읽히지만 <see cref="IsValid"/>가 false다.</summary>
    public static bool TryParse(string? text, out Rational value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        int slash = text.IndexOfAny(['/', ':']);
        if (slash >= 0)
        {
            if (!long.TryParse(text.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) return false;
            if (!long.TryParse(text.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long d)) return false;
            if (n < 0 || d < 0) return false;
            value = Reduce(n, d);
            return true;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
        {
            if (whole < 0) return false;
            value = new Rational(whole, 1);
            return true;
        }

        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal dec) && dec >= 0)
        {
            // 소수는 1/1000000 단위로 옮긴 뒤 약분한다 ("29.97" → 2997/100).
            const long scale = 1_000_000;
            value = Reduce((long)Math.Round(dec * scale), scale);
            return true;
        }

        return false;
    }

    public static Rational Parse(string text) =>
        TryParse(text, out var value) ? value : throw new FormatException($"비율 형식이 아닙니다: '{text}'");

    private static Rational Reduce(long n, long d)
    {
        if (n == 0 || d == 0) return new Rational(n, d);
        long a = n, b = d;
        while (b != 0) (a, b) = (b, a % b);
        return new Rational(n / a, d / a);
    }

    /// <summary>ffmpeg 인자에 그대로 쓸 수 있는 문자열. 분모가 1이면 정수로 쓴다.</summary>
    public override string ToString() =>
        Den == 1
            ? Num.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{Num}/{Den}");
}
