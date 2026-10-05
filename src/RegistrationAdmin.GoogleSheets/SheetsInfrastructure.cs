using System.Globalization;
using System.Net;
using System.Text;
using Google;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

namespace RegistrationAdmin.GoogleSheets;

public sealed record SheetInfo(int SheetId, string Title, int Index)
{
    public override string ToString() => $"{Title}（sheetId {SheetId}）";
}

public sealed record SpreadsheetInfo(string Title, IReadOnlyList<SheetInfo> Sheets)
{
    public SheetInfo? FindById(int sheetId) => Sheets.FirstOrDefault(s => s.SheetId == sheetId);

    public SheetInfo? FindByTitle(string title) => Sheets.FirstOrDefault(s => string.Equals(s.Title, title, StringComparison.Ordinal));
}

/// <summary>A1 表示法。工作表名稱一律加引號，避免中文、空白或底線開頭造成解析錯誤。</summary>
internal static class A1
{
    public static string Sheet(string title) => "'" + title.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string Range(string title, string range) => Sheet(title) + "!" + range;

    /// <summary>0 起算欄索引 → 欄字母（0→A、26→AA）。</summary>
    public static string Column(int index)
    {
        var sb = new StringBuilder();
        var n = index + 1;
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }

        return sb.ToString();
    }

    public static string Row(string title, int row, int columnCount) =>
        Range(title, $"A{row}:{Column(Math.Max(0, columnCount - 1))}{row}");

    public static string Cell(string title, int row, int columnIndex) =>
        Range(title, $"{Column(columnIndex)}{row}");
}

/// <summary>
/// 429 與 5xx 以指數退避＋jitter 有限重試；400／401／403／404 不重試。
/// 429 是「每分鐘配額」，退避拉長到約 90 秒內（Google 建議的 truncated exponential backoff）。
/// 非冪等的附加寫入（append）只重試 429（請求被拒、未套用），網路逾時不重送，避免重複列。
/// </summary>
internal static class SheetsRetry
{
    private const int QuotaAttempts = 7;

    public static async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken, int maxAttempts = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (GoogleApiException ex) when (IsQuota(ex.HttpStatusCode) && attempt < Math.Max(maxAttempts, QuotaAttempts))
            {
                await QuotaDelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException ex) when (IsTransient(ex.HttpStatusCode) && attempt < maxAttempts)
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>只重試配額錯誤（429）：用於不可重送的附加寫入。</summary>
    public static async Task<T> RunOnQuotaAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (GoogleApiException ex) when (IsQuota(ex.HttpStatusCode) && attempt < QuotaAttempts)
            {
                await QuotaDelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsQuota(HttpStatusCode code) => (int)code == 429;

    private static bool IsTransient(HttpStatusCode code) => (int)code >= 500;

    private static Task QuotaDelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseMs = Math.Min(30_000, 2_000 * Math.Pow(2, attempt - 1));
        return Task.Delay(TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, 1_000)), cancellationToken);
    }

    private static Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseMs = Math.Min(16_000, 500 * Math.Pow(2, attempt - 1));
        return Task.Delay(TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, 400)), cancellationToken);
    }
}

internal static class SheetsApi
{
    public static async Task<SpreadsheetInfo> GetSpreadsheetAsync(SheetsService service, string spreadsheetId, CancellationToken ct)
    {
        var request = service.Spreadsheets.Get(spreadsheetId);
        request.Fields = "properties.title,sheets.properties(sheetId,title,index)";
        var spreadsheet = await SheetsRetry.RunAsync(() => request.ExecuteAsync(ct), ct).ConfigureAwait(false);
        var sheets = (spreadsheet.Sheets ?? new List<Sheet>())
            .Where(s => s.Properties is not null)
            .Select(s => new SheetInfo(s.Properties.SheetId ?? 0, s.Properties.Title ?? "", s.Properties.Index ?? 0))
            .OrderBy(s => s.Index)
            .ToList();
        return new SpreadsheetInfo(spreadsheet.Properties?.Title ?? "", sheets);
    }

    public static async Task<IReadOnlyList<IList<IList<object>>>> BatchGetFormattedAsync(
        SheetsService service, string spreadsheetId, IReadOnlyList<string> ranges, CancellationToken ct)
    {
        var request = service.Spreadsheets.Values.BatchGet(spreadsheetId);
        request.Ranges = new Google.Apis.Util.Repeatable<string>(ranges);
        request.ValueRenderOption = SpreadsheetsResource.ValuesResource.BatchGetRequest.ValueRenderOptionEnum.FORMATTEDVALUE;
        request.DateTimeRenderOption = SpreadsheetsResource.ValuesResource.BatchGetRequest.DateTimeRenderOptionEnum.FORMATTEDSTRING;
        var response = await SheetsRetry.RunAsync(() => request.ExecuteAsync(ct), ct).ConfigureAwait(false);
        var valueRanges = response.ValueRanges ?? new List<ValueRange>();
        return ranges
            .Select((_, i) => i < valueRanges.Count ? valueRanges[i].Values ?? new List<IList<object>>() : new List<IList<object>>())
            .ToList();
    }

    public static async Task<IList<IList<object>>> GetAsync(
        SheetsService service, string spreadsheetId, string range, bool unformatted, CancellationToken ct)
    {
        var request = service.Spreadsheets.Values.Get(spreadsheetId, range);
        request.ValueRenderOption = unformatted
            ? SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE
            : SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.FORMATTEDVALUE;
        request.DateTimeRenderOption = unformatted
            ? SpreadsheetsResource.ValuesResource.GetRequest.DateTimeRenderOptionEnum.SERIALNUMBER
            : SpreadsheetsResource.ValuesResource.GetRequest.DateTimeRenderOptionEnum.FORMATTEDSTRING;
        var response = await SheetsRetry.RunAsync(() => request.ExecuteAsync(ct), ct).ConfigureAwait(false);
        return response.Values ?? new List<IList<object>>();
    }

    /// <summary>values.batchUpdate（RAW：字串不被解析，01234567 保持文字）。冪等，可重試。</summary>
    public static Task BatchUpdateAsync(SheetsService service, string spreadsheetId, IList<ValueRange> data, CancellationToken ct)
    {
        if (data.Count == 0)
        {
            return Task.CompletedTask;
        }

        var body = new BatchUpdateValuesRequest { ValueInputOption = "RAW", Data = data };
        return SheetsRetry.RunAsync(() => service.Spreadsheets.Values.BatchUpdate(body, spreadsheetId).ExecuteAsync(ct), ct);
    }

    /// <summary>附加列（INSERT_ROWS、RAW）。只在 429 配額錯誤時重試。</summary>
    public static async Task AppendAsync(SheetsService service, string spreadsheetId, string sheetTitle, IList<IList<object>> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var request = service.Spreadsheets.Values.Append(new ValueRange { Values = rows }, spreadsheetId, A1.Range(sheetTitle, "A1"));
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.RAW;
        request.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
        await SheetsRetry.RunOnQuotaAsync(() => request.ExecuteAsync(ct), ct).ConfigureAwait(false);
    }

    public static string CellText(IList<object>? row, int index) =>
        row is not null && index < row.Count
            ? Convert.ToString(row[index], CultureInfo.InvariantCulture) ?? ""
            : "";

    public static IList<object> ToRow(IEnumerable<string> values) => values.Cast<object>().ToList();
}

/// <summary>讀寫相關錯誤，訊息不含個資。</summary>
public sealed class SheetsAccessException : Exception
{
    public SheetsAccessException(string message, Exception? inner = null) : base(message, inner)
    {
    }

    public static Exception Translate(Exception ex) => ex switch
    {
        GoogleApiException { HttpStatusCode: HttpStatusCode.Forbidden } =>
            new SheetsAccessException("Google 回應 403：目前授權的帳號沒有此試算表的編輯權限，或組織政策阻擋。", ex),
        GoogleApiException { HttpStatusCode: HttpStatusCode.NotFound } =>
            new SheetsAccessException("Google 回應 404：找不到試算表或工作表，請確認 Spreadsheet ID。", ex),
        GoogleApiException { HttpStatusCode: HttpStatusCode.Unauthorized } =>
            new SheetsAccessException("Google 授權已失效，請在「設定與診斷」重新授權。", ex),
        GoogleApiException { HttpStatusCode: HttpStatusCode.BadRequest } api =>
            new SheetsAccessException("Google 拒絕此要求（400）：" + api.Error?.Message, ex),
        GoogleApiException api when (int)api.HttpStatusCode == 429 =>
            new SheetsAccessException("Google Sheets API 配額暫時用盡，請稍候再試。", ex),
        HttpRequestException => new SheetsAccessException("網路連線失敗，資料未儲存。請確認網路後重試。", ex),
        _ => ex,
    };
}
