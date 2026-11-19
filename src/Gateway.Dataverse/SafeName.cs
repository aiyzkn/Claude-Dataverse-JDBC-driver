using System.Text;

namespace Gateway.Dataverse;

/// <summary>
/// 外部由来トークン(テーブル名・ジョブラベル等)をファイル名の一部として安全化する(L-2)。
/// パス区切り・`..`・無効文字を除去し、出力ディレクトリ外への逸脱(パストラバーサル)を防ぐ。
/// </summary>
public static class SafeName
{
    private const int MaxLength = 128;

    /// <summary>
    /// 名前をファイル名の 1 セグメントとして無害化する。パス区切り(`/` `\`)・OS 無効文字を
    /// `_` に置換し、先頭末尾のドット/空白を除去して `.`/`..` を無効化する。空になれば fallback。
    /// </summary>
    public static string ForFile(string? name, string fallback = "unknown")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var invalid = Path.GetInvalidFileNameChars(); // OS 依存(Linux は少ない)ため区切りは別途明示除去
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch is '/' or '\\' || Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var s = sb.ToString().Trim().Trim('.', ' ');
        if (s.Length == 0) return fallback;
        return s.Length > MaxLength ? s[..MaxLength] : s;
    }
}
