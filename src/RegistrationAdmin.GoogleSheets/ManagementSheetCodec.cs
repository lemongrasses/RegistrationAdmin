using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.GoogleSheets;

/// <summary>管理分頁以第一列欄名讀寫，欄位調序不影響解析。</summary>
internal static class ManagementSheetCodec
{
    public sealed record Table(IReadOnlyList<string> Headers, IReadOnlyList<(int RowNumber, Dictionary<string, string> Values)> Rows);

    public static Table ParseTable(IList<IList<object>> values)
    {
        if (values.Count == 0)
        {
            return new Table(Array.Empty<string>(), Array.Empty<(int, Dictionary<string, string>)>());
        }

        var headers = values[0].Select((_, i) => SheetsApi.CellText(values[0], i).Trim()).ToList();
        var rows = new List<(int, Dictionary<string, string>)>();
        for (var r = 1; r < values.Count; r++)
        {
            var row = values[r];
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            var any = false;
            for (var c = 0; c < headers.Count; c++)
            {
                if (headers[c].Length == 0)
                {
                    continue;
                }

                var text = SheetsApi.CellText(row, c);
                dict[headers[c]] = text;
                any |= text.Length > 0;
            }

            if (any)
            {
                rows.Add((r + 1, dict));
            }
        }

        return new Table(headers, rows);
    }

    public static EventConfig ParseConfig(IList<IList<object>> values)
    {
        var table = ParseTable(values);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, row) in table.Rows)
        {
            var key = row.GetValueOrDefault("key", "").Trim();
            if (key.Length > 0)
            {
                dict[key] = row.GetValueOrDefault("value", "").Trim();
            }
        }

        return new EventConfig(dict);
    }

    public static IReadOnlyList<FieldMapping> ParseFieldMap(IList<IList<object>> values) =>
        ParseTable(values).Rows
            .Select(r => FieldMapping.FromSheetRow(r.Values))
            .Where(m => m.LogicalField.Length > 0)
            .ToList();

    public static IReadOnlyList<LookupItem> ParseLookups(IList<IList<object>> values) =>
        ParseTable(values).Rows
            .Select(r => LookupItem.FromSheetRow(r.Values))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();

    public static (IReadOnlyList<string> Headers, IReadOnlyList<AdminRecord> Records) ParseAdmin(IList<IList<object>> values)
    {
        var table = ParseTable(values);
        var records = table.Rows
            .Select(r => AdminRecord.FromValues(r.Values, r.RowNumber))
            .Where(a => a.RecordId.Length > 0)
            .ToList();
        return (table.Headers, records);
    }

    public static IReadOnlyList<IList<object>> ConfigRows(EventConfig config, int sourceSheetId, string sourceTitle)
    {
        var rows = new List<IList<object>> { SheetsApi.ToRow(new[] { "key", "value", "notes" }) };
        foreach (var (key, defaultValue) in DefaultProfile.ConfigDefaults)
        {
            var value = key switch
            {
                EventConfig.Keys.SourceSheetId => sourceSheetId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                EventConfig.Keys.SourceSheetName => sourceTitle,
                _ => config.Values.TryGetValue(key, out var v) ? v : defaultValue,
            };
            rows.Add(SheetsApi.ToRow(new[] { key, value, DefaultProfile.ConfigNotes.GetValueOrDefault(key, "") }));
        }

        // 保留使用者自行新增的設定鍵
        foreach (var (key, value) in config.Values.Where(kv => DefaultProfile.ConfigDefaults.All(d => d.Key != kv.Key)))
        {
            rows.Add(SheetsApi.ToRow(new[] { key, value, "" }));
        }

        return rows;
    }
}
