using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Diagnostics;
using RegistrationAdmin.GoogleSheets.Auth;

namespace RegistrationAdmin.GoogleSheets;

/// <summary>
/// 以 Google Sheets 實作的存取層。只寫入 _Admin 與 _ChangeLog；
/// 原始回應透過 <see cref="SourceResponseReader"/> 唯讀讀取。
/// </summary>
public sealed class GoogleSheetsRegistrationStore : IRegistrationStore, IChangeLogReader
{
    private readonly ISheetsServiceProvider _provider;
    private readonly IConnectionSettings _settings;
    private readonly SourceResponseReader _sourceReader = new();

    public GoogleSheetsRegistrationStore(ISheetsServiceProvider provider, IConnectionSettings settings)
    {
        _provider = provider;
        _settings = settings;
    }

    public async Task<WorkbookSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (service, id) = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var meta = await SheetsApi.GetSpreadsheetAsync(service, id, cancellationToken).ConfigureAwait(false);

            var missing = ManagementSheetNames.All.Where(n => meta.FindByTitle(n) is null).ToList();
            if (missing.Count > 0)
            {
                throw new SchemaNotInitializedException(
                    "試算表尚未建立管理分頁：" + string.Join("、", missing) + "。請到「設定與診斷」選擇來源工作表並按「建立／驗證管理分頁」。");
            }

            var ranges = new[]
            {
                A1.Range(ManagementSheetNames.Config, "A1:C"),
                A1.Range(ManagementSheetNames.FieldMap, "A1:Z"),
                A1.Range(ManagementSheetNames.Lookups, "A1:F"),
                A1.Range(ManagementSheetNames.Admin, "A1:ZZ"),
            };
            var values = await SheetsApi.BatchGetFormattedAsync(service, id, ranges, cancellationToken).ConfigureAwait(false);

            var config = ManagementSheetCodec.ParseConfig(values[0]);
            var mappings = ManagementSheetCodec.ParseFieldMap(values[1]);
            var lookups = ManagementSheetCodec.ParseLookups(values[2]);
            var (adminHeaders, adminRows) = ManagementSheetCodec.ParseAdmin(values[3]);
            EnsureAdminHeaders(adminHeaders);

            if (config.SourceSheetId is not int sourceSheetId)
            {
                throw new SchemaNotInitializedException("_Config 尚未設定 source_sheet_id。請到「設定與診斷」選擇原始回應工作表。");
            }

            var sourceSheet = meta.FindById(sourceSheetId)
                ?? throw new SchemaNotInitializedException($"找不到 sheetId {sourceSheetId} 的原始回應工作表（可能被刪除）。請重新選擇來源工作表。");
            if (ManagementSheetNames.All.Contains(sourceSheet.Title))
            {
                throw new SchemaNotInitializedException("來源工作表不可以是程式管理分頁。");
            }

            var source = await _sourceReader.ReadAsync(service, id, sourceSheet, cancellationToken).ConfigureAwait(false);
            return new WorkbookSnapshot(config, mappings, lookups, source, adminRows);
        }
        catch (Exception ex) when (ex is not RegistrationAdminException and not OperationCanceledException)
        {
            throw SheetsAccessException.Translate(ex);
        }
    }

    public async Task AppendAdminRowsAsync(IReadOnlyList<AdminRecord> rows, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        try
        {
            var (service, id) = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var headerValues = await SheetsApi.GetAsync(service, id, A1.Range(ManagementSheetNames.Admin, "A1:ZZ1"), false, cancellationToken)
                .ConfigureAwait(false);
            var headers = headerValues.Count > 0
                ? headerValues[0].Select((_, i) => SheetsApi.CellText(headerValues[0], i).Trim()).ToList()
                : new List<string>();
            EnsureAdminHeaders(headers);

            var data = rows.Select(r => SheetsApi.ToRow(headers.Select(h => h.Length == 0 ? "" : r[h]))).ToList();
            await SheetsApi.AppendAsync(service, id, ManagementSheetNames.Admin, data, cancellationToken).ConfigureAwait(false);
            await AppendChangeLogAsync(service, id, changeLog, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RegistrationAdminException and not OperationCanceledException)
        {
            throw SheetsAccessException.Translate(ex);
        }
    }

    public async Task UpdateAdminRowsAsync(IReadOnlyList<AdminRowUpdate> updates, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return;
        }

        try
        {
            var (service, id) = await ConnectAsync(cancellationToken).ConfigureAwait(false);

            // 寫入前重讀 _Admin，以 record_id 找目前列號並比對 row_version。
            var current = await SheetsApi.GetAsync(service, id, A1.Range(ManagementSheetNames.Admin, "A1:ZZ"), false, cancellationToken)
                .ConfigureAwait(false);
            var (headers, records) = ManagementSheetCodec.ParseAdmin(current);
            EnsureAdminHeaders(headers);
            var byId = records.GroupBy(r => r.RecordId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var conflicts = new List<string>();
            var data = new List<ValueRange>();
            foreach (var update in updates)
            {
                if (!byId.TryGetValue(update.Record.RecordId, out var matches) || matches.Count != 1 || matches[0].RowNumber is not int rowNumber)
                {
                    conflicts.Add(update.Record.RecordId);
                    continue;
                }

                var cloud = matches[0];
                if (update.ExpectedRowVersion is int expected && cloud.RowVersion != expected)
                {
                    conflicts.Add(update.Record.RecordId);
                    continue;
                }

                if (update.OnlyColumns is null)
                {
                    var row = headers.Select(h => h.Length == 0 ? "" : update.Record.Values.ContainsKey(h) ? update.Record[h] : cloud[h]);
                    data.Add(new ValueRange
                    {
                        Range = A1.Row(ManagementSheetNames.Admin, rowNumber, headers.Count),
                        Values = new List<IList<object>> { SheetsApi.ToRow(row) },
                    });
                }
                else
                {
                    foreach (var column in update.OnlyColumns)
                    {
                        var index = headers.ToList().IndexOf(column);
                        data.Add(new ValueRange
                        {
                            Range = A1.Cell(ManagementSheetNames.Admin, rowNumber, index),
                            Values = new List<IList<object>> { SheetsApi.ToRow(new[] { update.Record[column] }) },
                        });
                    }
                }
            }

            if (conflicts.Count > 0)
            {
                throw new ConcurrencyConflictException(conflicts);
            }

            await SheetsApi.BatchUpdateAsync(service, id, data, cancellationToken).ConfigureAwait(false);
            await AppendChangeLogAsync(service, id, changeLog, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RegistrationAdminException and not OperationCanceledException)
        {
            throw SheetsAccessException.Translate(ex);
        }
    }

    public async Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(string recordId, CancellationToken cancellationToken)
    {
        try
        {
            var (service, id) = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var values = await SheetsApi.GetAsync(service, id, A1.Range(ManagementSheetNames.ChangeLog, "A1:I"), false, cancellationToken)
                .ConfigureAwait(false);
            return ManagementSheetCodec.ParseTable(values).Rows
                .Select(r => r.Values)
                .Where(v => v.GetValueOrDefault("record_id") == recordId)
                .Select(v => new ChangeLogEntry(
                    v.GetValueOrDefault("change_id", ""),
                    v.GetValueOrDefault("correlation_id", ""),
                    recordId,
                    v.GetValueOrDefault("operation", ""),
                    v.GetValueOrDefault("changed_fields", ""),
                    v.GetValueOrDefault("sensitive_fields_changed", ""),
                    v.GetValueOrDefault("changed_at", ""),
                    v.GetValueOrDefault("app_version", ""),
                    v.GetValueOrDefault("notes", "")))
                .ToList();
        }
        catch (Exception ex) when (ex is not RegistrationAdminException and not OperationCanceledException)
        {
            throw SheetsAccessException.Translate(ex);
        }
    }

    private static async Task AppendChangeLogAsync(SheetsService service, string id, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken ct)
    {
        if (changeLog.Count == 0)
        {
            return;
        }

        var rows = changeLog.Select(c => SheetsApi.ToRow(c.ToSheetRow())).ToList();
        await SheetsApi.AppendAsync(service, id, ManagementSheetNames.ChangeLog, rows, ct).ConfigureAwait(false);
    }

    private static void EnsureAdminHeaders(IReadOnlyList<string> headers)
    {
        var missing = AdminColumns.All.Where(c => !headers.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            throw new SchemaNotInitializedException(
                "_Admin 缺少欄位：" + string.Join(", ", missing) + "。請到「設定與診斷」按「建立／驗證管理分頁」補齊後再試，本次未寫入。");
        }

        var duplicated = headers.Where(h => h.Length > 0).GroupBy(h => h).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicated.Count > 0)
        {
            throw new SchemaNotInitializedException("_Admin 欄名重複：" + string.Join(", ", duplicated) + "。請修正後再試。");
        }
    }

    private async Task<(SheetsService Service, string SpreadsheetId)> ConnectAsync(CancellationToken ct)
    {
        var id = SpreadsheetIdParser.Parse(_settings.SpreadsheetId)
                 ?? throw new SchemaNotInitializedException("尚未設定 Spreadsheet。請到「設定與診斷」貼上回應試算表網址或 ID。");
        var service = await _provider.GetAsync(ct).ConfigureAwait(false);
        return (service, id);
    }
}
