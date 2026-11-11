using NekoVup.Core.Processes;

namespace NekoVup.Tests;

/// <summary>이름만 적힌 도구("ffmpeg")를 어디서 찾는지: 프로그램 폴더·시스템 폴더·PATH의 전체 경로 항목만, 현재 폴더는 보지 않는다.</summary>
public class ToolPathTests
{
    [Theory]
    [InlineData("ffmpeg", true)]
    [InlineData("python.exe", true)]
    [InlineData(" ffmpeg ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(@"C:\tools\ffmpeg.exe", false)]
    [InlineData(@"tools\ffmpeg.exe", false)]
    [InlineData("tools/ffmpeg.exe", false)]
    [InlineData(@".\ffmpeg.exe", false)]
    [InlineData("C:ffmpeg.exe", false)]
    public void IsBareName_Is_True_Only_For_A_Name_Without_A_Folder(string fileName, bool expected) =>
        Assert.Equal(expected, ToolPath.IsBareName(fileName));

    [Fact]
    public void A_Path_Is_Returned_As_Written()
    {
        Assert.Equal(@"C:\없는 폴더\ffmpeg.exe", ToolPath.Resolve(@"C:\없는 폴더\ffmpeg.exe"));
        Assert.Equal(@"tools\ffmpeg.exe", ToolPath.Resolve(@"tools\ffmpeg.exe"));
        Assert.Equal("", ToolPath.Resolve(""));
    }

    [Fact]
    public void A_System_Tool_Is_Found_By_Name()
    {
        string expected = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.Equal(expected, ToolPath.Resolve("cmd.exe"), ignoreCase: true);
        Assert.Equal(expected, ToolPath.Resolve("cmd"), ignoreCase: true); // 확장자가 없으면 .exe
    }

    [Fact]
    public void An_Unknown_Name_Is_Not_Found()
    {
        Assert.Null(ToolPath.Resolve("이런-프로그램은-없다.exe"));
        Assert.Equal("이런-프로그램은-없다.exe", ToolPath.ResolveOrSame("이런-프로그램은-없다.exe"));
    }

    [Fact]
    public void Find_Takes_The_First_Folder_That_Has_The_File()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nekovup-toolpath-" + Guid.NewGuid().ToString("N"));
        string first = Path.Combine(dir, "첫째"), second = Path.Combine(dir, "둘째");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            File.WriteAllText(Path.Combine(second, "tool.exe"), "");
            Assert.Equal(Path.Combine(second, "tool.exe"), ToolPath.Find("tool", [first, second]));
            File.WriteAllText(Path.Combine(first, "tool.exe"), "");
            Assert.Equal(Path.Combine(first, "tool.exe"), ToolPath.Find("tool.exe", [first, second]));
            Assert.Null(ToolPath.Find("other", [first, second]));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_Current_Folder_And_Relative_Path_Entries_Are_Never_Searched()
    {
        // 현재 폴더에 같은 이름의 파일이 있어도 "."·""·상대 경로 항목으로는 찾지 않는다 (영상을 연 폴더에 놓인 가짜 ffmpeg.exe).
        string dir = Path.Combine(Path.GetTempPath(), "nekovup-toolpath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        string name = "nekovup-fake-" + Guid.NewGuid().ToString("N") + ".exe";
        string before = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(dir, name), "");
            File.WriteAllText(Path.Combine(dir, "bin", name), "");
            Environment.CurrentDirectory = dir;
            Assert.Null(ToolPath.Find(name, [".", "", "bin", @".\bin", "C:", @"\"]));
            Assert.Null(ToolPath.Resolve(name));
            Assert.Equal(Path.Combine(dir, name), ToolPath.Find(name, [".", dir]));
        }
        finally
        {
            Environment.CurrentDirectory = before;
            Directory.Delete(dir, recursive: true);
        }
    }
}
