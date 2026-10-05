using System.Globalization;

namespace RegistrationAdmin.Core.Domain;

/// <summary>_Config 工作表（key／value）對應的活動設定。</summary>
public sealed class EventConfig
{
    public static class Keys
    {
        public const string SchemaVersion = "schema_version";
        public const string ProfileId = "profile_id";
        public const string FormId = "form_id";
        public const string FormSchemaVersion = "form_schema_version";
        public const string SourceSheetId = "source_sheet_id";
        public const string SourceSheetName = "source_sheet_name";
        public const string AdminSheetName = "admin_sheet_name";
        public const string TimeZone = "timezone";
        public const string EventName = "event_name";
        public const string EventStartDate = "event_start_date";
        public const string EventEndDate = "event_end_date";
        public const string DailyStartTime = "daily_start_time";
        public const string DailyEndTime = "daily_end_time";
        public const string Capacity = "capacity";
        public const string FeeAmount = "fee_amount";
        public const string Currency = "currency";
        public const string RegistrationCloseAt = "registration_close_at";
        public const string MembersOnly = "members_only";
        public const string FullAttendanceRequired = "full_attendance_required";
        public const string ConsentVersion = "consent_version";
        public const string RegistrationNoPrefix = "registration_no_prefix";
        public const string SeatOccupyingStatuses = "seat_occupying_statuses";
    }

    private readonly Dictionary<string, string> _values;

    public EventConfig(IReadOnlyDictionary<string, string> values)
    {
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in DefaultProfile.ConfigDefaults)
        {
            _values[k] = v;
        }

        foreach (var (k, v) in values)
        {
            if (!string.IsNullOrWhiteSpace(k))
            {
                _values[k.Trim()] = v?.Trim() ?? "";
            }
        }
    }

    public static EventConfig Default { get; } = new(new Dictionary<string, string>());

    public IReadOnlyDictionary<string, string> Values => _values;

    public string Get(string key) => _values.TryGetValue(key, out var v) ? v : "";

    public string SchemaVersion => Get(Keys.SchemaVersion);
    public string ProfileId => Get(Keys.ProfileId);
    public string FormSchemaVersion => Get(Keys.FormSchemaVersion);
    public string EventName => Get(Keys.EventName);
    public string AdminSheetName => Get(Keys.AdminSheetName);
    public string SourceSheetName => Get(Keys.SourceSheetName);
    public string Currency => Get(Keys.Currency);
    public string RegistrationNoPrefix => Get(Keys.RegistrationNoPrefix);

    public int? SourceSheetId =>
        int.TryParse(Get(Keys.SourceSheetId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

    public int Capacity =>
        int.TryParse(Get(Keys.Capacity), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) && c >= 0 ? c : 20;

    public decimal FeeAmount =>
        decimal.TryParse(Get(Keys.FeeAmount), NumberStyles.Number, CultureInfo.InvariantCulture, out var f) ? f : 0m;

    public bool MembersOnly => SheetBool.Parse(Get(Keys.MembersOnly), true);

    /// <summary>占用名額的報名狀態。Phase 0 預設：通過待付款與已確認。</summary>
    public IReadOnlySet<string> SeatOccupyingStatuses
    {
        get
        {
            var raw = Get(Keys.SeatOccupyingStatuses);
            var set = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            if (set.Count == 0)
            {
                set.Add(RegistrationStatus.ApprovedPendingPayment);
                set.Add(RegistrationStatus.Confirmed);
            }

            return set;
        }
    }
}
