using NekoVup.Core.Media;

namespace NekoVup.Tests;

/// <summary>구간 칸의 시각 표기: 분:초로 적고, 여러 모양으로 적은 것을 읽는다.</summary>
public class TimeTextTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(83.5, "1:23.5")]
    [InlineData(83.25, "1:23.25")]
    [InlineData(600, "10:00")]
    [InlineData(3599.999, "1:00:00")] // 반올림이 분·시로 올라간다
    [InlineData(3723.05, "1:02:03.05")]
    [InlineData(-3, "0:00")]
    [InlineData(double.NaN, "0:00")]
    public void Formats_As_Minutes_And_Seconds(double seconds, string expected) =>
        Assert.Equal(expected, TimeText.Format(seconds));

    [Theory]
    [InlineData("1:23.5", 83.5)]
    [InlineData(" 1:23 ", 83)]
    [InlineData("0:05", 5)]
    [InlineData("1:02:03", 3723)]
    [InlineData("1:02:03.25", 3723.25)]
    [InlineData("90", 90)]           // 숫자만 적으면 초
    [InlineData("12.5", 12.5)]
    [InlineData("2:75", 195)]        // 60을 넘겨 적어도 받는다
    [InlineData("1분 23초", 83)]
    [InlineData("1분23.5초", 83.5)]
    [InlineData("1분 30", 90)]       // 단위 없이 끝나면 바로 아래 단위
    [InlineData("1시간 5", 3900)]
    [InlineData("1시간 2분 3초", 3723)]
    [InlineData("2분", 120)]
    [InlineData("1.5분", 90)]
    [InlineData("45초", 45)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("5M", 300)]
    public void Reads_What_People_Type(string text, double expected)
    {
        Assert.True(TimeText.TryParse(text, out double seconds));
        Assert.Equal(expected, seconds, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1:")]
    [InlineData(":30")]
    [InlineData("1:2:3:4")]
    [InlineData("1.5:30")]   // 소수는 초에만
    [InlineData("-5")]
    [InlineData("1분 2개")]
    [InlineData("1..2")]
    [InlineData("분")]
    [InlineData("1 30")]         // 분인지 초인지 알 수 없다 (더해서 31초로 읽지 않는다)
    [InlineData("5 5")]
    [InlineData("1분 30 20")]    // 단위 없는 숫자는 맨 끝에만
    [InlineData("1분 30 20초")]
    [InlineData("30초 5")]
    [InlineData("1분 2시간")]    // 큰 단위부터 적는다
    [InlineData("30초 2분")]
    [InlineData("1분 5분")]      // 같은 단위는 한 번만
    [InlineData("1h 2h")]
    public void Rejects_What_It_Cannot_Read(string? text) =>
        Assert.False(TimeText.TryParse(text, out _));

    [Fact]
    public void Formatted_Text_Reads_Back_To_The_Same_Time()
    {
        foreach (double seconds in new[] { 0, 0.01, 0.1, 9.99, 59.99, 60, 61.5, 599.95, 3600, 3661.07, 86399.99 })
        {
            Assert.True(TimeText.TryParse(TimeText.Format(seconds), out double back));
            Assert.Equal(seconds, back, 9);
        }
    }

    [Theory]
    [InlineData(0, "0초")]
    [InlineData(45, "45초")]
    [InlineData(45.5, "45.5초")]
    [InlineData(60, "1분")]
    [InlineData(83.5, "1분 23.5초")]
    [InlineData(3600, "1시간")]
    [InlineData(3605, "1시간 5초")]
    [InlineData(3723, "1시간 2분 3초")]
    [InlineData(7320, "2시간 2분")]
    public void Lengths_Are_Spelled_Out(double seconds, string expected) =>
        Assert.Equal(expected, TimeText.Length(seconds));
}
