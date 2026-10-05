using System.Globalization;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Identity;
using RegistrationAdmin.Core.Normalization;

namespace RegistrationAdmin.Core.Sync;

/// <summary>按邏輯欄位解析原始回應，並計算 source_key 與 fingerprint。</summary>
public static class SourceParser
{
    public static IReadOnlyList<SourceResponse> Parse(
        SourceTable table,
        IReadOnlyList<FieldMapping> mappings,
        SchemaHealthResult health,
        string profileId)
    {
        if (!health.IsHealthy)
        {
            throw new InvalidOperationException("來源欄位檢查未通過，不可解析。");
        }

        var active = mappings
            .Where(m => m.Active && health.ColumnByField.ContainsKey(m.LogicalField))
            .GroupBy(m => m.LogicalField, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var timestampColumn = health.ColumnByField[LogicalFields.SubmittedAt];
        var result = new List<SourceResponse>(table.Rows.Count);

        foreach (var row in table.Rows)
        {
            if (row.Formatted.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var answers = new Dictionary<string, string>(StringComparer.Ordinal);
            var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var mapping in active)
            {
                var column = health.ColumnByField[mapping.LogicalField];
                var text = column < row.Formatted.Count ? (row.Formatted[column] ?? "") : "";
                answers[mapping.LogicalField] = text.Trim();
                if (mapping.NormalizerCode == NormalizerCodes.BooleanOption)
                {
                    flags[mapping.LogicalField] = Normalizers.OptionSelected(text, mapping.OptionMatch);
                }
            }

            var serial = ToSerial(timestampColumn < row.Unformatted.Count ? row.Unformatted[timestampColumn] : null);
            var formattedTimestamp = timestampColumn < row.Formatted.Count ? row.Formatted[timestampColumn] ?? "" : "";
            var token = SourceIdentity.TimestampToken(serial, formattedTimestamp);
            var email = Normalizers.Email(answers.GetValueOrDefault(LogicalFields.Email));
            var phone = Normalizers.Phone(answers.GetValueOrDefault(LogicalFields.Phone));
            var (mealCode, mealOther) = Normalizers.Meal(answers.GetValueOrDefault(LogicalFields.Meal));

            result.Add(new SourceResponse
            {
                RowNumber = row.RowNumber,
                TimestampSerial = serial,
                TimestampText = serial is double s
                    ? DateTime.FromOADate(s).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : formattedTimestamp.Trim(),
                Answers = answers,
                OptionFlags = flags,
                NormalizedEmail = email,
                NormalizedPhone = phone,
                MealCode = mealCode,
                MealOtherText = mealOther,
                Channels = Normalizers.MultiChoice(answers.GetValueOrDefault(LogicalFields.AcquisitionChannels)),
                SourceKey = SourceIdentity.ComputeSourceKey(profileId, token, email, phone),
                Fingerprint = SourceIdentity.ComputeFingerprint(token, answers),
            });
        }

        return result;
    }

    internal static double? ToSerial(object? value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        long l => l,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
