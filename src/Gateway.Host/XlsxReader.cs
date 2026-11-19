using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Gateway.Host;

/// <summary>Excel(.xlsx)の先頭シートを (ヘッダ, 行) として読む(dvload。3.1.3)。
/// 先頭行をヘッダ、以降をデータ行とし、共有文字列を解決、欠損セルはセル参照で位置補正する。</summary>
public static class XlsxReader
{
    public static (List<string> Header, List<string?[]> Rows) Read(string path, long maxUncompressedBytes = 0)
    {
        using var doc = SpreadsheetDocument.Open(path, isEditable: false);
        var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("xlsx: WorkbookPart がありません");
        var sheet = wbPart.Workbook.Descendants<Sheet>().FirstOrDefault()
            ?? throw new InvalidOperationException("xlsx: シートがありません");
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);

        // M-3: 展開後(非圧縮)のワークシート/共有文字列サイズを DOM 読込前に検査し、展開ボム
        // (小さな .xlsx が展開後に巨大化する)による OOM を防ぐ。圧縮後サイズだけでは検知できない。
        if (maxUncompressedBytes > 0)
        {
            var uncompressed = StreamLength(wsPart) + StreamLength(wbPart.SharedStringTablePart);
            if (uncompressed > maxUncompressedBytes)
                throw new InvalidOperationException(
                    $"xlsx: 展開後サイズが上限を超えています({uncompressed / (1024 * 1024)}MB > {maxUncompressedBytes / (1024 * 1024)}MB)。" +
                    "Gateway:MaxInputFileMB を上げるか、ファイルを分割してください。");
        }
        var shared = wbPart.SharedStringTablePart?.SharedStringTable;

        var raw = new List<List<string?>>();
        foreach (var row in wsPart.Worksheet.Descendants<Row>())
        {
            var cells = new List<string?>();
            var expected = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var col = ColumnIndex(cell.CellReference?.Value);
                while (expected < col) { cells.Add(null); expected++; } // 欠損セルを埋める
                cells.Add(CellText(cell, shared));
                expected++;
            }
            raw.Add(cells);
        }
        raw.RemoveAll(r => r.All(v => string.IsNullOrEmpty(v))); // 空行除去

        if (raw.Count == 0) return (new List<string>(), new List<string?[]>());
        var header = raw[0].Select(h => (h ?? "").Trim()).ToList();
        var rows = new List<string?[]>(raw.Count - 1);
        for (var r = 1; r < raw.Count; r++)
        {
            var arr = new string?[header.Count];
            for (var c = 0; c < header.Count; c++)
            {
                var v = c < raw[r].Count ? raw[r][c] : null;
                arr[c] = string.IsNullOrEmpty(v) ? null : v;
            }
            rows.Add(arr);
        }
        return (header, rows);
    }

    /// <summary>OpenXML パートの展開後(非圧縮)ストリーム長。取得不能時は 0(検査をすり抜けさせない安全側は呼び出し側の上限で担保)。</summary>
    private static long StreamLength(DocumentFormat.OpenXml.Packaging.OpenXmlPart? part)
    {
        if (part is null) return 0;
        try { using var s = part.GetStream(FileMode.Open, FileAccess.Read); return s.Length; }
        catch { return 0; }
    }

    private static string? CellText(Cell cell, SharedStringTable? shared)
    {
        var value = cell.CellValue?.Text ?? cell.InnerText;
        if (string.IsNullOrEmpty(value)) return null;
        if (cell.DataType?.Value == CellValues.SharedString && shared is not null
            && int.TryParse(value, out var idx) && idx >= 0 && idx < shared.ChildElements.Count)
            return shared.ChildElements[idx].InnerText;
        if (cell.DataType?.Value == CellValues.Boolean)
            return value == "1" ? "true" : "false";
        return value;
    }

    /// <summary>Excel の列数上限(XFD 列)。これを超えるセル参照は不正として扱う(M-3)。</summary>
    private const int MaxColumns = 16384;

    /// <summary>
    /// セル参照(例 "B3")の列部分を 0 始まりの列インデックスへ。参照が無ければ 0。
    /// M-3: Excel 上限(XFD=16384)を超える参照は即エラーにし、欠損セル埋め(padding)による
    /// 過大メモリ確保・int オーバーフローを防ぐ(例: "AAAAAA1" で数億セルを確保しに行く問題)。
    /// </summary>
    private static int ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return 0;
        long col = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
            if (col > MaxColumns)
                throw new InvalidOperationException(
                    $"xlsx: セル参照 '{cellRef}' の列が Excel の上限(XFD={MaxColumns})を超えています。ファイルが破損しているか不正です。");
        }
        return (int)col - 1;
    }
}
