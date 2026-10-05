using System.Text;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Normalization;

namespace RegistrationAdmin.Core.Sync;

public sealed class SchemaHealthResult
{
    public required bool IsHealthy { get; init; }

    public required IReadOnlyList<string> MissingRequiredFields { get; init; }

    public required IReadOnlyList<string> MissingOptionalFields { get; init; }

    public required IReadOnlyList<string> AmbiguousFields { get; init; }

    public required IReadOnlyList<string> UnmappedHeaders { get; init; }

    public required IReadOnlyDictionary<string, int> ColumnByField { get; init; }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(IsHealthy ? "來源欄位檢查：通過" : "來源欄位檢查：未通過，已停止同步，未寫入 _Admin");
        if (MissingRequiredFields.Count > 0)
        {
            sb.AppendLine("缺少必要欄位映射：" + string.Join("、", MissingRequiredFields.Select(f => $"{LogicalFields.Label(f)}({f})")));
        }

        if (AmbiguousFields.Count > 0)
        {
            sb.AppendLine("標題重複而無法唯一對應：" + string.Join("、", AmbiguousFields.Select(f => $"{LogicalFields.Label(f)}({f})")));
        }

        if (MissingOptionalFields.Count > 0)
        {
            sb.AppendLine("缺少選填欄位映射：" + string.Join("、", MissingOptionalFields.Select(f => $"{LogicalFields.Label(f)}({f})")));
        }

        if (UnmappedHeaders.Count > 0)
        {
            sb.AppendLine("未使用的來源標題：" + string.Join("、", UnmappedHeaders));
        }

        if (!IsHealthy)
        {
            sb.AppendLine("請在 _FieldMap 修正 source_header 為回應工作表的實際標題後重新整理。");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>依 _FieldMap 的明確標題映射檢查來源工作表。欄位缺漏或無法唯一對應時不猜測。</summary>
public static class SchemaHealthCheck
{
    private static readonly string[] KeyFields = { LogicalFields.SubmittedAt, LogicalFields.Email, LogicalFields.Phone };

    public static SchemaHealthResult Check(IReadOnlyList<string> headers, IEnumerable<FieldMapping> mappings)
    {
        var normalizedHeaders = headers.Select(HeaderText.Normalize).ToList();
        var used = new HashSet<int>();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var missingRequired = new List<string>();
        var missingOptional = new List<string>();
        var ambiguous = new List<string>();
        var requiredAmbiguous = false;
        var activeMappings = mappings.Where(m => m.Active).ToList();

        foreach (var mapping in activeMappings)
        {
            var alternatives = mapping.HeaderAlternatives.Select(HeaderText.Normalize).ToHashSet(StringComparer.Ordinal);
            var matches = Enumerable.Range(0, normalizedHeaders.Count)
                .Where(i => normalizedHeaders[i].Length > 0 && alternatives.Contains(normalizedHeaders[i]))
                .ToList();

            if (matches.Count == 0)
            {
                (mapping.Required ? missingRequired : missingOptional).Add(mapping.LogicalField);
                continue;
            }

            if (matches.Count > 1)
            {
                ambiguous.Add(mapping.LogicalField);
                requiredAmbiguous |= mapping.Required;
                continue;
            }

            columns[mapping.LogicalField] = matches[0];
            used.Add(matches[0]);
        }

        foreach (var key in KeyFields)
        {
            if (!columns.ContainsKey(key) && !missingRequired.Contains(key) && !ambiguous.Contains(key))
            {
                missingRequired.Add(key);
            }
        }

        var unmapped = Enumerable.Range(0, headers.Count)
            .Where(i => !used.Contains(i) && normalizedHeaders[i].Length > 0)
            .Select(i => headers[i])
            .ToList();

        return new SchemaHealthResult
        {
            IsHealthy = missingRequired.Count == 0 && !requiredAmbiguous,
            MissingRequiredFields = missingRequired,
            MissingOptionalFields = missingOptional,
            AmbiguousFields = ambiguous,
            UnmappedHeaders = unmapped,
            ColumnByField = columns,
        };
    }
}
