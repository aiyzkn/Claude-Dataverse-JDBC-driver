using System.Text;

namespace Gateway.Dataverse;

/// <summary>
/// RFC4180 準拠の CSV パーサ(dvload。3.1.3)。引用符・"" エスケープ・埋め込み改行/区切りに対応。
/// 先頭行をヘッダ、以降をデータ行として返す。空文字セルは NULL(null)扱い。
/// </summary>
public static class CsvParser
{
    public static (List<string> Header, List<string?[]> Rows) Read(string text, char delimiter = ',')
    {
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var sawAny = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') { inQuotes = true; sawAny = true; }
            else if (ch == delimiter) { record.Add(field.ToString()); field.Clear(); sawAny = true; }
            else if (ch == '\r') { /* \n を行区切りに使う */ }
            else if (ch == '\n')
            {
                record.Add(field.ToString()); field.Clear();
                records.Add(record); record = new List<string>(); sawAny = false;
            }
            else { field.Append(ch); sawAny = true; }
        }
        if (sawAny || field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        records.RemoveAll(r => r.Count == 1 && r[0].Length == 0); // 空行を除去

        if (records.Count == 0) return (new List<string>(), new List<string?[]>());
        var header = records[0].Select(h => h.Trim()).ToList();
        var rows = new List<string?[]>(records.Count - 1);
        for (var r = 1; r < records.Count; r++)
        {
            var arr = new string?[header.Count];
            for (var c = 0; c < header.Count; c++)
            {
                var v = c < records[r].Count ? records[r][c] : null;
                arr[c] = string.IsNullOrEmpty(v) ? null : v; // 空文字は NULL 扱い
            }
            rows.Add(arr);
        }
        return (header, rows);
    }
}
