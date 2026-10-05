using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Diagnostics;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.GoogleSheets.Auth;

namespace RegistrationAdmin.GoogleSheets;

public sealed record SchemaInitResult(
    string SpreadsheetTitle,
    IReadOnlyList<string> CreatedSheets,
    IReadOnlyList<string> AddedAdminColumns,
    SheetInfo SourceSheet)
{
    public string Describe()
    {
        var lines = new List<string> { $"試算表：{SpreadsheetTitle}", $"來源工作表：{SourceSheet}" };
        lines.Add(CreatedSheets.Count > 0 ? "已建立：" + string.Join("、", CreatedSheets) : "管理分頁皆已存在");
        if (AddedAdminColumns.Count > 0)
        {
            lines.Add("_Admin 補上欄位：" + string.Join(", ", AddedAdminColumns));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 建立／驗證 _Config、_FieldMap、_Lookups、_Admin、_ChangeLog。
/// 不修改原始回應工作表；已存在的管理分頁只補缺少的欄位，不覆寫既有資料（_Config 的來源設定除外）。
/// </summary>
public sealed class SchemaManager
{
    private readonly ISheetsServiceProvider _provider;
    private readonly IConnectionSettings _settings;

    public SchemaManager(ISheetsServiceProvider provider, IConnectionSettings settings)
    {
        _provider = provider;
        _settings = settings;
    }

    public async Task<SpreadsheetInfo> GetSpreadsheetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (service, id) = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            return await SheetsApi.GetSpreadsheetAsync(service, id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RegistrationAdminException and not OperationCanceledException)
        {
            throw SheetsAccessException.Translate(ex);
        }
    }

    public async Task<SchemaInitResult> InitializeAsync(int sourceSheetId, CancellationToken cancellationToken)
    {
        try
        {
            var (service, id) = await ConnectAsync(cancellationToken).ConfigureAwait(false);
            var meta = await SheetsApi.GetSpreadsheetAsync(service, id, cancellationToken).ConfigureAwait(false);
            var source = meta.FindById(sourceSheetId)
                         ?? throw new SchemaNotInitializedException($"找不到 sheetId {sourceSheetId} 的工作表。");
            if (ManagementSheetNames.All.Contains(source.Title))
            {
                throw new SchemaNotInitializedException("來源工作表不可以是程式管理分頁。");
            }

            var toCreate = ManagementSheetNames.All.Where(n => meta.FindByTitle(n) is null).ToList();
            if (toCreate.Count > 0)
            {
                var requests = toCreate.Select(name => new Request
                {
                    AddSheet = new AddSheetRequest
                    {
                        Properties = new SheetProperties
                        {
                            Title = name,
                            GridProperties = new GridProperties
                            {
                                FrozenRowCount = 1,
                                ColumnCount = name == ManagementSheetNames.Admin ? AdminColumns.All.Count + 10 : 26,
                            },
                        },
                    },
                }).ToList();
                await SheetsRetry.RunAsync(
                    () => service.Spreadsheets.BatchUpdate(new BatchUpdateSpreadsheetRequest { Requests = requests }, id).ExecuteAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            var existing = await SheetsApi.BatchGetFormattedAsync(service, id, new[]
            {
                A1.Range(ManagementSheetNames.Config, "A1:C"),
                A1.Range(ManagementSheetNames.FieldMap, "A1:Z1"),
                A1.Range(ManagementSheetNames.Lookups, "A1:F1"),
                A1.Range(ManagementSheetNames.Admin, "A1:ZZ1"),
                A1.Range(ManagementSheetNames.ChangeLog, "A1:Z1"),
            }, cancellationToken).ConfigureAwait(false);

            var data = new List<ValueRange>();

            // _Config：保留既有值，只更新來源工作表設定並補上缺少的鍵。
            var config = ManagementSheetCodec.ParseConfig(existing[0]);
            data.Add(Block(ManagementSheetNames.Config, "A1", ManagementSheetCodec.ConfigRows(config, source.SheetId, source.Title)));

            if (IsEmpty(existing[1]))
            {
                var rows = new List<IList<object>> { SheetsApi.ToRow(FieldMapping.SheetColumns) };
                rows.AddRange(DefaultProfile.FieldMappings.Select(m => SheetsApi.ToRow(m.ToSheetRow())));
                data.Add(Block(ManagementSheetNames.FieldMap, "A1", rows));
            }

            if (IsEmpty(existing[2]))
            {
                var rows = new List<IList<object>> { SheetsApi.ToRow(LookupItem.SheetColumns) };
                rows.AddRange(DefaultProfile.Lookups.Select(l => SheetsApi.ToRow(l.ToSheetRow())));
                data.Add(Block(ManagementSheetNames.Lookups, "A1", rows));
            }

            var added = new List<string>();
            var adminHeaders = HeaderRow(existing[3]);
            if (adminHeaders.Count == 0)
            {
                data.Add(Block(ManagementSheetNames.Admin, "A1", new List<IList<object>> { SheetsApi.ToRow(AdminColumns.All) }));
            }
            else
            {
                // 只在最後面補缺少的欄位，不移動既有欄。
                var missing = AdminColumns.All.Where(c => !adminHeaders.Contains(c)).ToList();
                if (missing.Count > 0)
                {
                    var lastUsed = adminHeaders.FindLastIndex(h => h.Length > 0);
                    var start = A1.Column(lastUsed + 1);
                    data.Add(Block(ManagementSheetNames.Admin, start + "1", new List<IList<object>> { SheetsApi.ToRow(missing) }));
                    added.AddRange(missing);
                }
            }

            if (IsEmpty(existing[4]))
            {
                data.Add(Block(ManagementSheetNames.ChangeLog, "A1", new List<IList<object>> { SheetsApi.ToRow(ChangeLogEntry.SheetColumns) }));
            }

            await SheetsApi.BatchUpdateAsync(service, id, data, cancellationToken).ConfigureAwait(false);
            return new SchemaInitResult(meta.Title, toCreate, added, source);
        }
        catch (Exception ex) when (ex is not RegistrationAdminException and not OperationCanceledException)
        {
            throw SheetsAccessException.Translate(ex);
        }
    }

    private static ValueRange Block(string sheet, string topLeft, IReadOnlyList<IList<object>> rows) =>
        new() { Range = A1.Range(sheet, topLeft), Values = rows.ToList() };

    private static bool IsEmpty(IList<IList<object>> values) => HeaderRow(values).All(h => h.Length == 0);

    private static List<string> HeaderRow(IList<IList<object>> values) =>
        values.Count == 0 ? new List<string>() : values[0].Select((_, i) => SheetsApi.CellText(values[0], i).Trim()).ToList();

    private async Task<(SheetsService Service, string SpreadsheetId)> ConnectAsync(CancellationToken ct)
    {
        var id = SpreadsheetIdParser.Parse(_settings.SpreadsheetId)
                 ?? throw new SchemaNotInitializedException("請先貼上回應試算表網址或 Spreadsheet ID。");
        var service = await _provider.GetAsync(ct).ConfigureAwait(false);
        return (service, id);
    }
}
