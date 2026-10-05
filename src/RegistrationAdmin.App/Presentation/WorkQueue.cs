using System.Globalization;
using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.App.Presentation;

/// <summary>狀態標籤的色調。色彩只是輔助，標籤一律帶文字。</summary>
public enum ChipTone
{
    Neutral,
    Info,
    Warning,
    Success,
    Danger,
}

public static class StatusTones
{
    public static ChipTone Registration(string code) => code switch
    {
        RegistrationStatus.Submitted or "" => ChipTone.Info,
        RegistrationStatus.UnderReview => ChipTone.Warning,
        RegistrationStatus.ApprovedPendingPayment => ChipTone.Warning,
        RegistrationStatus.Confirmed => ChipTone.Success,
        _ => ChipTone.Neutral,
    };

    public static ChipTone Membership(string code) => code switch
    {
        MembershipStatus.Verified => ChipTone.Success,
        MembershipStatus.Unchecked or "" => ChipTone.Warning,
        _ => ChipTone.Neutral,
    };

    public static ChipTone Eligibility(string code) => code switch
    {
        EligibilityStatus.Eligible => ChipTone.Success,
        EligibilityStatus.Unchecked or EligibilityStatus.NeedsInformation or "" => ChipTone.Warning,
        _ => ChipTone.Neutral,
    };

    public static ChipTone Payment(string code) => code switch
    {
        PaymentStatus.Verified => ChipTone.Success,
        PaymentStatus.Pending => ChipTone.Warning,
        PaymentStatus.Reported => ChipTone.Info,
        PaymentStatus.Overdue or PaymentStatus.Issue => ChipTone.Danger,
        _ => ChipTone.Neutral,
    };
}

/// <summary>名單上方的快速篩選籤。</summary>
public enum QuickFilter
{
    All,
    Pending,
    Errors,
    Duplicates,
}

/// <summary>
/// 「待處理」的判斷與提示文字。只依目前狀態與資料問題計算，不改變任何狀態。
/// </summary>
public static class WorkQueue
{
    private static readonly HashSet<string> Closed = new(StringComparer.Ordinal)
    {
        RegistrationStatus.Rejected, RegistrationStatus.Cancelled, RegistrationStatus.PaymentExpired,
    };

    public static bool IsClosed(Registration r) => Closed.Contains(r.Admin.RegistrationStatus);

    /// <summary>需要處理的審核步驟與資料問題（不含等待付款）。顯示在名單「問題」欄。</summary>
    public static IReadOnlyList<string> Problems(Registration r)
    {
        var items = new List<string>();
        if (r.Admin.IsArchived || IsClosed(r))
        {
            return items;
        }

        foreach (var issue in r.Issues.Where(i => !i.Accepted).OrderBy(i => i.Severity))
        {
            items.Add(ShortIssue(issue));
        }

        var status = r.Admin.RegistrationStatus;
        if (status is RegistrationStatus.Submitted or RegistrationStatus.UnderReview or "")
        {
            if (r.Admin.MembershipStatus is MembershipStatus.Unchecked or "")
            {
                items.Add("會員未核對");
            }

            switch (r.Admin.EligibilityStatus)
            {
                case EligibilityStatus.Unchecked or "":
                    items.Add("資格待判定");
                    break;
                case EligibilityStatus.NeedsInformation:
                    items.Add("資格需補資料");
                    break;
            }

            if (items.Count == 0)
            {
                items.Add(status == RegistrationStatus.UnderReview ? "可完成審核" : "尚未開始審核");
            }
        }

        return items;
    }

    /// <summary>「待處理」頁的全部事項：審核步驟、資料問題，以及等待付款核帳。</summary>
    public static IReadOnlyList<string> TodoItems(Registration r)
    {
        var items = Problems(r).ToList();
        if (!r.Admin.IsArchived && r.Admin.RegistrationStatus == RegistrationStatus.ApprovedPendingPayment
                                && r.Admin.PaymentStatus != PaymentStatus.Verified)
        {
            items.Add(r.Admin.PaymentStatus == PaymentStatus.Reported ? "已回報匯款，待核帳" : "等待付款");
        }

        return items;
    }

    public static bool NeedsAttention(Registration r) => TodoItems(r).Count > 0;

    public static bool HasOpenError(Registration r) =>
        r.Issues.Any(i => !i.Accepted && i.Severity == IssueSeverity.Error);

    public static bool HasOpenDuplicate(Registration r) =>
        r.Issues.Any(i => !i.Accepted && IssueCodes.IsDuplicate(i.Code));

    public static bool Matches(QuickFilter filter, Registration r) => filter switch
    {
        QuickFilter.Pending => NeedsAttention(r),
        QuickFilter.Errors => HasOpenError(r),
        QuickFilter.Duplicates => HasOpenDuplicate(r),
        _ => true,
    };

    /// <summary>名單「問題」欄的一行摘要。</summary>
    public static string ProblemSummary(IReadOnlyList<string> problems) => problems.Count switch
    {
        0 => "—",
        1 => problems[0],
        _ => $"{problems.Count} 項待處理",
    };

    /// <summary>給一般使用者看的短問題文字（不含代碼或內部欄名）。</summary>
    public static string ShortIssue(Issue issue)
    {
        var code = issue.Code;
        if (code.StartsWith(IssueCodes.MissingPrefix, StringComparison.Ordinal))
        {
            return $"{LogicalFields.Label(issue.Field)}尚未填寫";
        }

        if (code.StartsWith(IssueCodes.ConsentPrefix, StringComparison.Ordinal))
        {
            return $"未勾選「{LogicalFields.Label(issue.Field)}」";
        }

        return code switch
        {
            IssueCodes.EmailFormat => "Email 格式需確認",
            IssueCodes.PhoneFormat => "電話不完整",
            IssueCodes.TaxIdFormat => "統一編號需確認",
            IssueCodes.MealOtherText => "午餐「其他」未說明",
            IssueCodes.DuplicateEmail or IssueCodes.DuplicatePhone or IssueCodes.DuplicateNameOrg => "疑似重複報名",
            IssueCodes.SourceChanged => "報名者修改過原始答案",
            IssueCodes.Orphan => "找不到原始報名資料",
            _ => "資料需要確認",
        };
    }

    /// <summary>給一般使用者看的完整問題說明。</summary>
    public static string LongIssue(Issue issue) => issue.Code switch
    {
        IssueCodes.SourceChanged => "報名者在建立後台資料後修改過 Google Form 的答案。請確認左側的最新內容，確認無誤後按「我已確認最新內容」。",
        IssueCodes.Orphan => "找不到對應的 Google Form 原始回應，可能已被刪除或修改。後台資料會保留，請聯絡管理員處理。",
        IssueCodes.SourceKeyCollision or IssueCodes.AdminDuplicateKey or IssueCodes.PossibleRelink =>
            "原始回應與後台資料的對應有問題，需要管理員到「設定 → 進階（管理員）」處理。",
        _ when issue.Code.StartsWith(IssueCodes.MissingPrefix, StringComparison.Ordinal) =>
            $"{LogicalFields.Label(issue.Field)}尚未填寫。可在「修正報名者資料」補上，或聯絡報名者確認。",
        _ => issue.Message,
    };
}

/// <summary>日期顯示：今天／昨天／月日。</summary>
public static class FriendlyTime
{
    public static string Format(DateTime t, DateTime now)
    {
        var time = t.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (t.Date == now.Date)
        {
            return "今天 " + time;
        }

        if (t.Date == now.Date.AddDays(-1))
        {
            return "昨天 " + time;
        }

        return t.Year == now.Year
            ? t.ToString("MM/dd ", CultureInfo.InvariantCulture) + time
            : t.ToString("yyyy/MM/dd ", CultureInfo.InvariantCulture) + time;
    }

    public static string Format(DateTimeOffset value, DateTime now) => Format(value.LocalDateTime, now);

    /// <summary>空白值顯示「尚未填寫」。</summary>
    public static string OrBlank(string? value) => string.IsNullOrWhiteSpace(value) ? "尚未填寫" : value.Trim();
}
