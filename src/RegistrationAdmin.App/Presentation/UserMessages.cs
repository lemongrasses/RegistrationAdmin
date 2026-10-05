using System.IO;
using System.Net;
using System.Net.Http;
using Google;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.GoogleSheets;

namespace RegistrationAdmin.App.Presentation;

/// <summary>後台欄位的中文名稱。一般模式不顯示英文欄名。</summary>
public static class FieldLabels
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        [AdminColumns.MembershipStatus] = "會員核對",
        [AdminColumns.MembershipReference] = "會員編號或名冊位置",
        [AdminColumns.MembershipNotes] = "會員核對備註",
        [AdminColumns.EligibilityStatus] = "資格判定",
        [AdminColumns.EligibilityNotes] = "資格備註",
        [AdminColumns.RegistrationStatus] = "報名狀態",
        [AdminColumns.ReviewNotes] = "審核備註",
        [AdminColumns.ReviewedAt] = "審核時間",
        [AdminColumns.SeatReservedAt] = "保留名額時間",
        [AdminColumns.WaitlistPosition] = "候補順位",
        [AdminColumns.CancelledAt] = "取消時間",
        [AdminColumns.CancellationReason] = "取消原因",
        [AdminColumns.ExpectedAmount] = "應付金額",
        [AdminColumns.PaymentStatus] = "付款狀態",
        [AdminColumns.PaymentDeadlineAt] = "付款期限",
        [AdminColumns.PayerName] = "匯款人",
        [AdminColumns.BankName] = "銀行",
        [AdminColumns.AccountLast5] = "帳號末五碼",
        [AdminColumns.PaymentReference] = "付款參考",
        [AdminColumns.PaymentReportedAt] = "回報匯款日期",
        [AdminColumns.PaidAt] = "付款日期",
        [AdminColumns.VerifiedAt] = "核帳日期",
        [AdminColumns.FinanceNotes] = "財務備註",
        [AdminColumns.InvoiceStatus] = "發票狀態",
        [AdminColumns.InvoiceNumber] = "發票號碼",
        [AdminColumns.InvoiceIssuedAt] = "發票開立日期",
        [AdminColumns.InvoiceNotes] = "發票備註",
        [AdminColumns.OverrideName] = "姓名（修正）",
        [AdminColumns.OverrideOrganization] = "服務單位（修正）",
        [AdminColumns.OverrideJobTitle] = "職稱（修正）",
        [AdminColumns.OverrideEmail] = "Email（修正）",
        [AdminColumns.OverridePhone] = "聯絡電話（修正）",
        [AdminColumns.OverrideInvoiceTitle] = "發票抬頭（修正）",
        [AdminColumns.OverrideTaxId] = "統一編號（修正）",
        [AdminColumns.OverrideMealCode] = "午餐（修正）",
        [AdminColumns.MealOtherText] = "午餐其他說明",
        [AdminColumns.DietaryNotes] = "飲食備註",
        [AdminColumns.DuplicateStatus] = "重複判定",
        [AdminColumns.DuplicateOfRecordId] = "保留的另一筆報名",
        [AdminColumns.AcceptedIssueCodes] = "已確認的例外",
        [AdminColumns.IssueNotes] = "例外說明",
        [AdminColumns.AdminNotes] = "後台備註",
        [AdminColumns.IsArchived] = "封存",
        [AdminColumns.SourceChanged] = "原始答案變更確認",
        [AdminColumns.SourceKey] = "原始資料連結",
    };

    /// <summary>系統自動維護的欄位：不出現在「將變更」摘要。</summary>
    private static readonly HashSet<string> SystemColumns = new(StringComparer.Ordinal)
    {
        AdminColumns.ReviewedAt, AdminColumns.SeatReservedAt, AdminColumns.CancelledAt, AdminColumns.RowVersion,
        AdminColumns.UpdatedAt, AdminColumns.CreatedAt, AdminColumns.SourceFingerprint, AdminColumns.SourceRowHint,
    };

    public static string Label(string column)
    {
        if (Labels.TryGetValue(column, out var label))
        {
            return label;
        }

        var logical = LogicalFields.Label(column);
        return logical != column ? logical : "其他欄位";
    }

    public static bool IsSystem(string column) => SystemColumns.Contains(column);

    /// <summary>異動紀錄中逗號分隔的欄名 → 中文。</summary>
    public static string Describe(string columns) =>
        string.Join("、", columns.Split(new[] { ',', '，', ';', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !IsSystem(c))
            .Select(Label)
            .Distinct());
}

/// <summary>把例外與驗證訊息轉成一般使用者看得懂、可以採取行動的中文。</summary>
public static class UserMessages
{
    public const string Conflict = "資料已在其他地方更新，請先按「更新名單」重新整理，再重新修改這筆報名。";
    public const string NoPermission = "目前登入的 Google 帳號沒有這份試算表的編輯權限。請改用有權限的帳號重新連線，或請試算表擁有者共用給您。";
    public const string NotFound = "找不到這份試算表，請檢查連結是否正確。";
    public const string AuthExpired = "Google 登入已過期，請到「設定」按「重新連線」。";
    public const string Network = "網路連線失敗，資料沒有儲存。請確認網路後再試一次。";

    public static string Describe(Exception ex) => ex switch
    {
        ConcurrencyConflictException => Conflict,
        BusinessRuleException rules => string.Join(Environment.NewLine, rules.Messages.Select(Validation)),
        SchemaHealthException => "報名表的欄位和系統設定對不上，暫時無法讀取名單。請管理員到「設定 → 進階（管理員）」查看欄位檢查結果。",
        SchemaNotInitializedException => "這份試算表還沒有完成設定。請到「設定」確認連線；第一次使用時需要由管理員「建立管理資料區」。",
        SheetsAccessException access => Google(access.InnerException) ?? "無法存取 Google 試算表，請稍後再試。",
        GoogleApiException api => Google(api) ?? "Google 暫時無法處理這次的要求，請稍後再試。",
        HttpRequestException => Network,
        FileNotFoundException => "找不到 Google 登入設定檔，程式可能沒有安裝完整。請聯絡管理員。",
        OperationCanceledException => "操作已取消或等候逾時，資料沒有變更。",
        RegistrationAdminException known => known.Message,
        _ => "發生未預期的問題，資料沒有變更。需要協助時，請到「設定」建立診斷資料給管理員。",
    };

    private static string? Google(Exception? inner) => inner switch
    {
        GoogleApiException { HttpStatusCode: HttpStatusCode.Forbidden } => NoPermission,
        GoogleApiException { HttpStatusCode: HttpStatusCode.NotFound } => NotFound,
        GoogleApiException { HttpStatusCode: HttpStatusCode.Unauthorized } => AuthExpired,
        GoogleApiException api when (int)api.HttpStatusCode == 429 => "Google 目前太忙碌，請等一分鐘後再試。",
        GoogleApiException => "Google 無法處理這次的要求。請稍後再試；若持續發生，請聯絡管理員。",
        HttpRequestException => Network,
        _ => null,
    };

    /// <summary>驗證訊息：把內部欄名換成中文。</summary>
    public static string Validation(ValidationMessage message)
    {
        if (AdminColumns.UserDateFields.Contains(message.Field))
        {
            return $"「{FieldLabels.Label(message.Field)}」的日期看不懂，請輸入像 2026-10-15 的格式。";
        }

        return message.Field switch
        {
            AdminColumns.DuplicateOfRecordId => "標記為重複時，請選擇要保留的另一筆報名。",
            _ => message.Message,
        };
    }
}
