namespace RegistrationAdmin.Core.Domain;

/// <summary>
/// 程式內部使用的邏輯欄位名稱。程式碼只使用這些名稱，不使用表單的中文標題；
/// 中文標題與邏輯欄位的對照放在 _FieldMap。
/// </summary>
public static class LogicalFields
{
    public const string SubmittedAt = "submitted_at";
    public const string DataUseConsent = "data_use_consent";
    public const string FullAttendanceConsent = "full_attendance_consent";
    public const string FullName = "full_name";
    public const string OrganizationName = "organization_name";
    public const string JobTitle = "job_title";
    public const string Email = "email";
    public const string Phone = "phone";
    public const string FeeAcknowledged = "fee_acknowledged";
    public const string InvoiceTitle = "invoice_title";
    public const string TaxId = "tax_id";
    public const string Meal = "meal";
    public const string ApplicantNotes = "applicant_notes";
    public const string AcquisitionChannels = "acquisition_channels";
    public const string AccuracyConfirmation = "accuracy_confirmation";

    /// <summary>四項確認（資料使用、三日全勤、費用說明、送出資料正確）。</summary>
    public static readonly IReadOnlyList<(string Field, string Label)> Consents = new[]
    {
        (DataUseConsent, "資料使用同意"),
        (FullAttendanceConsent, "三日全程參與同意"),
        (FeeAcknowledged, "報名費說明確認"),
        (AccuracyConfirmation, "資料正確及接受審核確認"),
    };

    /// <summary>進入「通過待付款」前不可缺漏的有效值欄位。</summary>
    public static readonly IReadOnlyList<(string Field, string Label)> RequiredForApproval = new[]
    {
        (FullName, "姓名"),
        (OrganizationName, "服務／所屬單位"),
        (JobTitle, "職稱"),
        (Email, "Email"),
        (Phone, "聯絡電話"),
        (InvoiceTitle, "發票抬頭"),
        (TaxId, "統一編號"),
    };

    public static string Label(string field) => field switch
    {
        SubmittedAt => "時間戳記",
        DataUseConsent => "資料使用同意",
        FullAttendanceConsent => "三日全程參與同意",
        FullName => "姓名",
        OrganizationName => "服務／所屬單位",
        JobTitle => "職稱",
        Email => "Email",
        Phone => "聯絡電話",
        FeeAcknowledged => "報名費說明確認",
        InvoiceTitle => "發票抬頭",
        TaxId => "統一編號",
        Meal => "午餐需求",
        ApplicantNotes => "備註",
        AcquisitionChannels => "得知管道",
        AccuracyConfirmation => "送出前確認",
        _ => field,
    };
}
