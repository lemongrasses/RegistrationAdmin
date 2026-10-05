using Google.Apis.Sheets.v4;
using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.GoogleSheets;

/// <summary>
/// Google Form 原始回應工作表的唯讀存取。此類別刻意只有讀取方法：
/// 不新增欄、不覆寫答案、不刪列、不移列。
/// </summary>
public sealed class SourceResponseReader
{
    /// <summary>同時讀取 FORMATTED（答案文字）與 UNFORMATTED＋SERIAL_NUMBER（時間戳序號）。</summary>
    public async Task<SourceTable> ReadAsync(SheetsService service, string spreadsheetId, SheetInfo sheet, CancellationToken cancellationToken)
    {
        var range = A1.Range(sheet.Title, "A1:ZZ");
        var formatted = await SheetsApi.GetAsync(service, spreadsheetId, range, unformatted: false, cancellationToken).ConfigureAwait(false);
        var unformatted = await SheetsApi.GetAsync(service, spreadsheetId, range, unformatted: true, cancellationToken).ConfigureAwait(false);

        if (formatted.Count == 0)
        {
            return new SourceTable(sheet.SheetId, sheet.Title, Array.Empty<string>(), Array.Empty<SourceRow>());
        }

        var headers = formatted[0].Select((_, i) => SheetsApi.CellText(formatted[0], i)).ToList();
        var width = headers.Count;
        var rows = new List<SourceRow>(formatted.Count);
        for (var r = 1; r < formatted.Count; r++)
        {
            var text = new string[width];
            var raw = new object?[width];
            var rawRow = r < unformatted.Count ? unformatted[r] : null;
            for (var c = 0; c < width; c++)
            {
                text[c] = SheetsApi.CellText(formatted[r], c);
                raw[c] = rawRow is not null && c < rawRow.Count ? rawRow[c] : null;
            }

            rows.Add(new SourceRow(r + 1, text, raw));
        }

        return new SourceTable(sheet.SheetId, sheet.Title, headers, rows);
    }
}
