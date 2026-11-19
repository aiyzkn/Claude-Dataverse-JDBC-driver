using Gateway.Dataverse;
using Xunit;

namespace Gateway.Tests;

/// <summary>ファイル名サニタイズ(L-2: パストラバーサル防止)。</summary>
public class SafeNameTests
{
    [Theory]
    [InlineData("account", "account")]
    [InlineData("new_contact", "new_contact")]
    public void 通常のテーブル名はそのまま(string input, string expected)
        => Assert.Equal(expected, SafeName.ForFile(input));

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("a/b/c")]
    [InlineData("a\\b")]
    public void 区切り文字は除去されディレクトリを跨がない(string input)
    {
        var result = SafeName.ForFile(input);
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
        // Path.Combine で基準ディレクトリ配下に留まる(親へ遡らない)
        var combined = Path.GetFullPath(Path.Combine(@"C:\base", $"failed_{result}.csv"));
        Assert.StartsWith(Path.GetFullPath(@"C:\base"), combined);
    }

    [Fact]
    public void 先頭末尾のドットは除去され_ドット2つは無効化()
    {
        Assert.Equal("unknown", SafeName.ForFile(".."));
        Assert.Equal("unknown", SafeName.ForFile("."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空や空白は_fallback(string? input)
        => Assert.Equal("unknown", SafeName.ForFile(input));

    [Fact]
    public void 長すぎる名前は切り詰められる()
        => Assert.True(SafeName.ForFile(new string('a', 500)).Length <= 128);

    [Fact]
    public void 独自fallbackを指定できる()
        => Assert.Equal("x", SafeName.ForFile("", "x"));
}
