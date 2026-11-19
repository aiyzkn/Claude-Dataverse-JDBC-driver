using System.Collections.Concurrent;
using System.Text;

namespace Gateway.Dataverse;

/// <summary>
/// 失敗行レポート(F-14)。二分探索で特定した原因行を蓄積し CSV 出力する。
/// 行番号・テーブル・原因列・エラーメッセージを記録し、実施報告に転用可能な形式にする。
/// </summary>
public sealed class FailureReport
{
    public sealed record FailedRow(long RowIndex, string Table, string? Column, string Error);

    private readonly ConcurrentBag<FailedRow> _rows = new();

    /// <summary>原因列を特定できた場合はその列名を付けて記録する(F-14 列名特定)。</summary>
    public void Add(long rowIndex, string table, string? column, string error)
        => _rows.Add(new FailedRow(rowIndex, table, column, error));

    /// <summary>原因列が不明な場合の互換オーバーロード。</summary>
    public void Add(long rowIndex, string table, string error)
        => Add(rowIndex, table, null, error);

    public int Count => _rows.Count;

    public IReadOnlyList<FailedRow> Rows => _rows.OrderBy(r => r.RowIndex).ToList();

    /// <summary>
    /// CSV(BOM 付き UTF-8)を書き出し、パスを返す。失敗が無ければ null。
    /// 出力ファイルは現ユーザーのみへ制限し、retentionDays>0 なら古い失敗行 CSV を掃除する(M-2)。
    /// </summary>
    public string? WriteCsv(string directory, string jobLabel, int retentionDays = 0)
    {
        if (_rows.IsEmpty) return null;
        Directory.CreateDirectory(directory);
        // L-2: jobLabel は外部由来(テーブル名等)。ファイル名として無害化しパストラバーサルを防ぐ。
        var path = Path.Combine(directory, $"failed_{SafeName.ForFile(jobLabel)}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("row_index,table,column,error");
        foreach (var r in Rows)
            sb.AppendLine($"{r.RowIndex},{Csv(r.Table)},{Csv(r.Column ?? "")},{Csv(r.Error)}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        FileProtection.RestrictToCurrentUser(path); // M-2: 失敗行 CSV を他ユーザーから保護
        FileProtection.PruneOld(directory, "failed_*.csv", retentionDays, DateTime.UtcNow); // M-2: 無期限残置を防止
        return path;
    }

    private static string Csv(string s)
    {
        var v = s.ReplaceLineEndings(" ");
        // CSV フォーミュラインジェクション対策: 数式記号で始まる値は先頭に ' を付与して無害化
        // (移行元データ由来の値や Dataverse エラー文が Excel で数式評価されるのを防ぐ)。
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t')
            v = "'" + v;
        return v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
