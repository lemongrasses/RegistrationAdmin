namespace RegistrationAdmin.Core.Domain;

/// <summary>
/// 2026 示範活動的預設設定檔。只在初始化管理分頁時寫入 Google Sheets；
/// 之後以 Sheets 上的 _Config／_FieldMap／_Lookups 為準。
/// </summary>
public static class DefaultProfile
{
    public const string ProfileId = "DEMO-2026";
    public const string FormSchemaVersion = "DEMO-2026-v1";
    public const string ManagementSchemaVersion = "1";
    public const string FormId = "YOUR_GOOGLE_FORM_ID";

    public static readonly IReadOnlyList<KeyValuePair<string, string>> ConfigDefaults = new[]
    {
        Kv(EventConfig.Keys.SchemaVersion, ManagementSchemaVersion),
        Kv(EventConfig.Keys.ProfileId, ProfileId),
        Kv(EventConfig.Keys.FormId, FormId),
        Kv(EventConfig.Keys.FormSchemaVersion, FormSchemaVersion),
        Kv(EventConfig.Keys.SourceSheetId, ""),
        Kv(EventConfig.Keys.SourceSheetName, ""),
        Kv(EventConfig.Keys.AdminSheetName, ManagementSheetNames.Admin),
        Kv(EventConfig.Keys.TimeZone, "Asia/Taipei"),
        Kv(EventConfig.Keys.EventName, "2026 示範活動工作坊"),
        Kv(EventConfig.Keys.EventStartDate, "2026-11-01"),
        Kv(EventConfig.Keys.EventEndDate, "2026-11-03"),
        Kv(EventConfig.Keys.DailyStartTime, "09:00"),
        Kv(EventConfig.Keys.DailyEndTime, "16:10"),
        Kv(EventConfig.Keys.Capacity, "20"),
        Kv(EventConfig.Keys.FeeAmount, "3000"),
        Kv(EventConfig.Keys.Currency, "TWD"),
        Kv(EventConfig.Keys.RegistrationCloseAt, "2026-10-21"),
        Kv(EventConfig.Keys.MembersOnly, "TRUE"),
        Kv(EventConfig.Keys.FullAttendanceRequired, "TRUE"),
        Kv(EventConfig.Keys.ConsentVersion, "CONSENT-2026-DEMO-v1"),
        Kv(EventConfig.Keys.RegistrationNoPrefix, "RD26-"),
        Kv(EventConfig.Keys.SeatOccupyingStatuses, "approved_pending_payment,confirmed"),
    };

    public static readonly IReadOnlyDictionary<string, string> ConfigNotes = new Dictionary<string, string>
    {
        [EventConfig.Keys.SourceSheetId] = "原始回應工作表的數字 sheetId；分頁改名後仍可定位",
        [EventConfig.Keys.RegistrationCloseAt] = "截止時間（時分）待確認",
        [EventConfig.Keys.SeatOccupyingStatuses] = "占用 20 人名額的狀態，逗號分隔；Phase 0 確認",
        [EventConfig.Keys.RegistrationNoPrefix] = "可讀報名編號前綴",
    };

    /// <summary>
    /// 預設欄位映射。標題取自 2026-10-02 匯出的表單 PDF；若實際回應工作表標題不同，
    /// 健康檢查會停止同步並列出缺少的欄位，請直接修改 Sheets 上的 _FieldMap。
    /// </summary>
    public static readonly IReadOnlyList<FieldMapping> FieldMappings = new[]
    {
        Map(LogicalFields.SubmittedAt, "時間戳記|Timestamp", ResponseTypes.DateTime, true, NormalizerCodes.None),
        Map(LogicalFields.DataUseConsent, "請詳閱活動說明後再行勾選", ResponseTypes.Multi, true, NormalizerCodes.BooleanOption, "同意主辦單位"),
        Map(LogicalFields.FullAttendanceConsent, "請詳閱活動說明後再行勾選", ResponseTypes.Multi, true, NormalizerCodes.BooleanOption, "三日全勤"),
        Map(LogicalFields.FullName, "姓名", ResponseTypes.Text, true, NormalizerCodes.None),
        Map(LogicalFields.OrganizationName, "服務/所屬單位 (如：某大學某學系)", ResponseTypes.Text, true, NormalizerCodes.None),
        Map(LogicalFields.JobTitle, "職稱", ResponseTypes.Text, true, NormalizerCodes.None),
        Map(LogicalFields.Email, "Email (用於接收報名確認及活動資訊)", ResponseTypes.Text, true, NormalizerCodes.Email),
        Map(LogicalFields.Phone, "聯絡電話(僅供活動聯繫使用)", ResponseTypes.Text, true, NormalizerCodes.Phone),
        Map(LogicalFields.FeeAcknowledged, "報名費說明確認", ResponseTypes.Single, true, NormalizerCodes.BooleanOption, "我已了解"),
        Map(LogicalFields.InvoiceTitle, "發票抬頭", ResponseTypes.Text, true, NormalizerCodes.None),
        Map(LogicalFields.TaxId, "統一編號", ResponseTypes.Text, true, NormalizerCodes.TaxId),
        Map(LogicalFields.Meal, "午餐需求", ResponseTypes.Single, true, NormalizerCodes.Meal),
        Map(LogicalFields.ApplicantNotes, "備註 (如：特殊飲食需求等)", ResponseTypes.Text, false, NormalizerCodes.None),
        Map(LogicalFields.AcquisitionChannels, "您是如何得知本次活動？", ResponseTypes.Multi, false, NormalizerCodes.MultiChoice),
        Map(LogicalFields.AccuracyConfirmation, "送出前確認", ResponseTypes.Multi, true, NormalizerCodes.BooleanOption, "資料填寫正確"),
    };

    public static readonly IReadOnlyList<LookupItem> Lookups = new[]
    {
        L(LookupDomains.RegistrationStatus, RegistrationStatus.Submitted, "新報名", 10, "預設"),
        L(LookupDomains.RegistrationStatus, RegistrationStatus.UnderReview, "審核中", 20),
        L(LookupDomains.RegistrationStatus, RegistrationStatus.ApprovedPendingPayment, "通過待付款", 30, "占位規則待確認"),
        L(LookupDomains.RegistrationStatus, RegistrationStatus.Waitlisted, "候補", 40),
        L(LookupDomains.RegistrationStatus, RegistrationStatus.Rejected, "不通過", 50),
        L(LookupDomains.RegistrationStatus, RegistrationStatus.Confirmed, "已確認", 60, "原則上需付款核對"),
        L(LookupDomains.RegistrationStatus, RegistrationStatus.PaymentExpired, "付款逾期", 70, "不自動轉換"),
        L(LookupDomains.RegistrationStatus, RegistrationStatus.Cancelled, "已取消", 80),

        L(LookupDomains.MembershipStatus, MembershipStatus.Unchecked, "未核對", 10),
        L(LookupDomains.MembershipStatus, MembershipStatus.Verified, "已確認會員", 20),
        L(LookupDomains.MembershipStatus, MembershipStatus.NotMember, "非會員", 30),
        L(LookupDomains.MembershipStatus, MembershipStatus.Unverifiable, "無法核對", 40),

        L(LookupDomains.EligibilityStatus, EligibilityStatus.Unchecked, "未判定", 10),
        L(LookupDomains.EligibilityStatus, EligibilityStatus.Eligible, "符合資格", 20),
        L(LookupDomains.EligibilityStatus, EligibilityStatus.Ineligible, "不符資格", 30),
        L(LookupDomains.EligibilityStatus, EligibilityStatus.NeedsInformation, "需補資料", 40),

        L(LookupDomains.PaymentStatus, PaymentStatus.NotRequested, "未通知付款", 10),
        L(LookupDomains.PaymentStatus, PaymentStatus.Pending, "待付款", 20),
        L(LookupDomains.PaymentStatus, PaymentStatus.Reported, "已回報匯款", 30),
        L(LookupDomains.PaymentStatus, PaymentStatus.Verified, "已核帳", 40),
        L(LookupDomains.PaymentStatus, PaymentStatus.Overdue, "逾期", 50),
        L(LookupDomains.PaymentStatus, PaymentStatus.Issue, "付款異常", 60),
        L(LookupDomains.PaymentStatus, PaymentStatus.Refunded, "已退款", 70),

        L(LookupDomains.InvoiceStatus, InvoiceStatus.Pending, "待開立", 10),
        L(LookupDomains.InvoiceStatus, InvoiceStatus.Issued, "已開立", 20),
        L(LookupDomains.InvoiceStatus, InvoiceStatus.Cancelled, "已作廢", 30),

        L(LookupDomains.MealCode, MealCode.Meat, "葷食", 10),
        L(LookupDomains.MealCode, MealCode.Vegetarian, "素食", 20),
        L(LookupDomains.MealCode, MealCode.None, "不需午餐", 30),
        L(LookupDomains.MealCode, MealCode.Other, "其他", 40),

        L(LookupDomains.IssueSeverity, "error", "錯誤", 10),
        L(LookupDomains.IssueSeverity, "warning", "警告", 20),
        L(LookupDomains.IssueSeverity, "info", "提示", 30),

        L(LookupDomains.DuplicateStatus, DuplicateStatus.NotDuplicate, "確認非重複", 10),
        L(LookupDomains.DuplicateStatus, DuplicateStatus.Duplicate, "重複（保留另一筆）", 20),
    };

    private static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);

    private static FieldMapping Map(string field, string header, string type, bool required, string normalizer, string optionMatch = "") =>
        new(ProfileId, field, header, type, required, normalizer, "", optionMatch, true, FormSchemaVersion);

    private static LookupItem L(string domain, string code, string label, int sort, string notes = "") =>
        new(domain, code, label, sort, true, notes);
}

public static class ManagementSheetNames
{
    public const string Config = "_Config";
    public const string FieldMap = "_FieldMap";
    public const string Lookups = "_Lookups";
    public const string Admin = "_Admin";
    public const string ChangeLog = "_ChangeLog";

    public static readonly IReadOnlyList<string> All = new[] { Config, FieldMap, Lookups, Admin, ChangeLog };
}
