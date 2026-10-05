using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace RegistrationAdmin.Export;

/// <summary>XLSX：文字欄位設為文字格式，另附「匯出資訊」工作表。</summary>
public static class XlsxExporter
{
    public static void Write(ExportTable table, string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("名單");

        for (var c = 0; c < table.Columns.Count; c++)
        {
            var header = sheet.Cell(1, c + 1);
            header.Value = table.Columns[c].Header;
            header.Style.Font.Bold = true;
            if (table.Columns[c].IsText)
            {
                sheet.Column(c + 1).Style.NumberFormat.Format = "@";
            }
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var cell = sheet.Cell(r + 2, c + 1);
                if (table.Columns[c].IsText)
                {
                    cell.Style.NumberFormat.Format = "@";
                }

                // 字串一律以文字寫入，不做型別推斷，前導零不會消失。
                cell.Value = row[c];
            }
        }

        sheet.SheetView.FreezeRows(1);
        TryAdjustColumns(sheet);

        if (table.Summary.Count > 0)
        {
            var summary = workbook.Worksheets.Add("統計");
            WriteKeyValues(summary, table.Summary, "項目", "數量");
        }

        var info = workbook.Worksheets.Add("匯出資訊");
        WriteKeyValues(info, table.Metadata, "項目", "內容");

        workbook.SaveAs(path);
    }

    private static void WriteKeyValues(IXLWorksheet sheet, IReadOnlyList<KeyValuePair<string, string>> values, string keyHeader, string valueHeader)
    {
        sheet.Cell(1, 1).Value = keyHeader;
        sheet.Cell(1, 2).Value = valueHeader;
        sheet.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < values.Count; i++)
        {
            sheet.Cell(i + 2, 1).Value = values[i].Key;
            sheet.Cell(i + 2, 2).Value = values[i].Value;
        }

        TryAdjustColumns(sheet);
    }

    private static void TryAdjustColumns(IXLWorksheet sheet)
    {
        try
        {
            sheet.Columns().AdjustToContents();
        }
        catch (Exception)
        {
            // 某些環境缺少字型時無法量測欄寬；不影響資料內容。
        }
    }
}

/// <summary>
/// CSV：UTF-8 BOM（Excel 可正確顯示中文）。開頭為 # 的列是匯出資訊。
/// 文字欄位若是以 0 開頭的純數字，預設寫成 ="0123" 讓 Excel 開啟時保留前導零。
/// 其他欄位以 = + - @ 開頭時加上 ' 前綴，避免被當成公式執行。
/// </summary>
public static class CsvExporter
{
    public static void Write(ExportTable table, string path, bool excelFriendlyText = true, bool includeMetadata = true)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));

        if (includeMetadata)
        {
            foreach (var (key, value) in table.Metadata)
            {
                csv.WriteField("# " + key);
                csv.WriteField(Guard(value));
                csv.NextRecord();
            }
        }

        foreach (var column in table.Columns)
        {
            csv.WriteField(column.Header);
        }

        csv.NextRecord();

        foreach (var row in table.Rows)
        {
            for (var c = 0; c < table.Columns.Count; c++)
            {
                csv.WriteField(FormatCell(row[c], table.Columns[c].IsText, excelFriendlyText));
            }

            csv.NextRecord();
        }
    }

    internal static string FormatCell(string value, bool isText, bool excelFriendlyText)
    {
        var numericText = value.Length > 1
                          && (value[0] == '0' || value[0] == '+')
                          && value.Skip(1).All(char.IsAsciiDigit)
                          && char.IsAsciiDigit(value[1]);
        if (isText && excelFriendlyText && numericText)
        {
            return "=\"" + value + "\"";
        }

        return Guard(value);
    }

    internal static string Guard(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
    }
}

public static class ExportService
{
    public static int Export(ExportRequest request, ExportFormat format, string path)
    {
        var table = ExportTableBuilder.Build(request);
        switch (format)
        {
            case ExportFormat.Csv:
                CsvExporter.Write(table, path);
                break;
            default:
                XlsxExporter.Write(table, path);
                break;
        }

        return table.Rows.Count;
    }
}
