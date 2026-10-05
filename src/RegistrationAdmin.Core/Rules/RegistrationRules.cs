using System.Globalization;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Normalization;

namespace RegistrationAdmin.Core.Rules;

/// <summary>狀態轉換前的業務驗證（規劃 9.2）。</summary>
public static class TransitionValidator
{
    public static IReadOnlyList<ValidationMessage> Validate(
        Registration registration,
        AdminRecord working,
        string target,
        IReadOnlyList<Registration> all,
        EventConfig config)
    {
        var messages = new List<ValidationMessage>();
        var from = working.RegistrationStatus;

        if (string.Equals(from, target, StringComparison.Ordinal))
        {
            messages.Add(new("registration_status", "狀態未變更。"));
            return messages;
        }

        if (!StatusTransitions.IsAllowed(from, target))
        {
            var catalog = LookupCatalog.Default;
            messages.Add(new("registration_status",
                $"不允許由「{catalog.Label(LookupDomains.RegistrationStatus, from)}」轉為「{catalog.Label(LookupDomains.RegistrationStatus, target)}」。"));
            return messages;
        }

        switch (target)
        {
            case RegistrationStatus.ApprovedPendingPayment:
                ValidateApproval(registration, working, all, config, messages);
                break;
            case RegistrationStatus.Confirmed:
                if (working.PaymentStatus != PaymentStatus.Verified)
                {
                    messages.Add(new(AdminColumns.PaymentStatus, "付款狀態必須為「已核帳」才能轉為已確認。"));
                }

                AddPaymentVerifiedChecks(working, messages);
                break;
            case RegistrationStatus.Rejected:
                if (working[AdminColumns.ReviewNotes].Trim().Length == 0)
                {
                    messages.Add(new(AdminColumns.ReviewNotes, "轉為不通過前必須填寫審核備註（理由）。"));
                }

                break;
            case RegistrationStatus.Cancelled:
                if (working[AdminColumns.CancellationReason].Trim().Length == 0)
                {
                    messages.Add(new(AdminColumns.CancellationReason, "轉為已取消前必須填寫取消原因。"));
                }

                break;
        }

        return messages;
    }

    /// <summary>驗證通過後套用狀態與相關時間欄位。</summary>
    public static void Apply(AdminRecord working, string target, IReadOnlyList<Registration> all, DateTimeOffset now)
    {
        var from = working.RegistrationStatus;
        var stamp = TimeFormat.Iso(now);
        working[AdminColumns.RegistrationStatus] = target;

        switch (target)
        {
            case RegistrationStatus.UnderReview:
            case RegistrationStatus.Rejected:
                working[AdminColumns.ReviewedAt] = stamp;
                break;
            case RegistrationStatus.ApprovedPendingPayment:
                working[AdminColumns.ReviewedAt] = stamp;
                working[AdminColumns.SeatReservedAt] = stamp;
                if (working.PaymentStatus is "" or PaymentStatus.NotRequested)
                {
                    working[AdminColumns.PaymentStatus] = PaymentStatus.Pending;
                }

                break;
            case RegistrationStatus.Waitlisted:
                working[AdminColumns.ReviewedAt] = stamp;
                var maxPosition = all
                    .Where(r => r.RecordId != working.RecordId && r.Admin.RegistrationStatus == RegistrationStatus.Waitlisted)
                    .Select(r => int.TryParse(r.Admin[AdminColumns.WaitlistPosition], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0)
                    .DefaultIfEmpty(0)
                    .Max();
                working[AdminColumns.WaitlistPosition] = (maxPosition + 1).ToString(CultureInfo.InvariantCulture);
                break;
            case RegistrationStatus.Cancelled:
                working[AdminColumns.CancelledAt] = stamp;
                working[AdminColumns.SeatReservedAt] = "";
                break;
            case RegistrationStatus.PaymentExpired:
                working[AdminColumns.SeatReservedAt] = "";
                break;
        }

        if (from == RegistrationStatus.Waitlisted && target != RegistrationStatus.Waitlisted)
        {
            working[AdminColumns.WaitlistPosition] = "";
        }
    }

    private static void ValidateApproval(
        Registration registration,
        AdminRecord working,
        IReadOnlyList<Registration> all,
        EventConfig config,
        List<ValidationMessage> messages)
    {
        if (registration.Source is null)
        {
            messages.Add(new("source", "找不到對應的原始表單回應，不能通過審核。請先處理來源關聯。"));
            return;
        }

        if (working.MembershipStatus != MembershipStatus.Verified)
        {
            messages.Add(new(AdminColumns.MembershipStatus, "會員核對狀態必須為「已確認會員」。"));
        }

        if (working.EligibilityStatus != EligibilityStatus.Eligible)
        {
            messages.Add(new(AdminColumns.EligibilityStatus, "資格狀態必須為「符合資格」。"));
        }

        foreach (var (field, label) in LogicalFields.Consents)
        {
            if (!registration.Consent(field))
            {
                messages.Add(new(field, $"缺少確認：{label}。"));
            }
        }

        foreach (var (field, label) in LogicalFields.RequiredForApproval)
        {
            if (registration.Effective(field, working).Trim().Length == 0)
            {
                messages.Add(new(field, $"{label}不可空白。"));
            }
        }

        AddMealChecks(registration, working, messages, requireMeal: true);

        var occupying = config.SeatOccupyingStatuses;
        var occupied = all.Count(r =>
            r.RecordId != registration.RecordId
            && !r.Admin.IsArchived
            && occupying.Contains(r.Admin.RegistrationStatus));
        if (occupied >= config.Capacity)
        {
            messages.Add(new("capacity", $"名額已滿（{occupied}/{config.Capacity}），只能保持審核中或轉候補。"));
        }
    }

    internal static void AddMealChecks(Registration registration, AdminRecord working, List<ValidationMessage> messages, bool requireMeal)
    {
        var meal = registration.EffectiveMealCode(working);
        if (meal.Length == 0)
        {
            if (requireMeal)
            {
                messages.Add(new(LogicalFields.Meal, "午餐需求不可空白。"));
            }

            return;
        }

        if (!MealCode.All.Contains(meal))
        {
            messages.Add(new(AdminColumns.OverrideMealCode, $"午餐代碼「{meal}」不在代碼表中。"));
        }

        if (meal == MealCode.Other
            && registration.EffectiveMealOtherText(working).Length == 0
            && working[AdminColumns.DietaryNotes].Trim().Length == 0)
        {
            messages.Add(new(AdminColumns.MealOtherText, "午餐為「其他」時，其他說明或飲食備註至少需填一項。"));
        }
    }

    internal static void AddPaymentVerifiedChecks(AdminRecord working, List<ValidationMessage> messages)
    {
        if (working.PaymentStatus != PaymentStatus.Verified)
        {
            return;
        }

        if (working[AdminColumns.PaidAt].Trim().Length == 0)
        {
            messages.Add(new(AdminColumns.PaidAt, "付款狀態為已核帳時，付款日期必填。"));
        }

        if (working[AdminColumns.VerifiedAt].Trim().Length == 0)
        {
            messages.Add(new(AdminColumns.VerifiedAt, "付款狀態為已核帳時，核帳日期必填。"));
        }
    }
}

/// <summary>按下儲存前的欄位驗證。</summary>
public static class AdminRecordValidator
{
    public static IReadOnlyList<ValidationMessage> ValidateForSave(Registration registration, AdminRecord working)
    {
        var messages = new List<ValidationMessage>();

        TransitionValidator.AddPaymentVerifiedChecks(working, messages);

        switch (working.RegistrationStatus)
        {
            case RegistrationStatus.Rejected when working[AdminColumns.ReviewNotes].Trim().Length == 0:
                messages.Add(new(AdminColumns.ReviewNotes, "不通過的報名必須有審核備註。"));
                break;
            case RegistrationStatus.Cancelled when working[AdminColumns.CancellationReason].Trim().Length == 0:
                messages.Add(new(AdminColumns.CancellationReason, "已取消的報名必須有取消原因。"));
                break;
            case RegistrationStatus.Confirmed when working.PaymentStatus != PaymentStatus.Verified:
                messages.Add(new(AdminColumns.PaymentStatus, "已確認的報名，付款狀態必須為已核帳。"));
                break;
        }

        if (registration.Source is not null)
        {
            TransitionValidator.AddMealChecks(registration, working, messages, requireMeal: false);
        }

        foreach (var column in AdminColumns.UserDateFields)
        {
            if (!TimeFormat.IsValidUserDate(working[column]))
            {
                messages.Add(new(column, $"「{column}」日期格式無法辨識，請用 yyyy-MM-dd 或 yyyy-MM-dd HH:mm。"));
            }
        }

        var last5 = working[AdminColumns.AccountLast5].Trim();
        if (last5.Length > 0 && (last5.Length > 5 || !last5.All(char.IsAsciiDigit)))
        {
            messages.Add(new(AdminColumns.AccountLast5, "帳號末碼只能是 1–5 位數字（不要輸入完整帳號）。"));
        }

        var overrideEmail = working[AdminColumns.OverrideEmail].Trim();
        if (overrideEmail.Length > 0 && !Normalizers.IsValidEmail(overrideEmail))
        {
            messages.Add(new(AdminColumns.OverrideEmail, "修正後的 Email 格式不正確。"));
        }

        if (working.InvoiceStatus == InvoiceStatus.Issued && working[AdminColumns.InvoiceNumber].Trim().Length == 0)
        {
            messages.Add(new(AdminColumns.InvoiceNumber, "發票狀態為已開立時，發票號碼必填。"));
        }

        if (working.DuplicateStatus == DuplicateStatus.Duplicate)
        {
            var target = working[AdminColumns.DuplicateOfRecordId].Trim();
            if (target.Length == 0 || target == working.RecordId)
            {
                messages.Add(new(AdminColumns.DuplicateOfRecordId, "標為重複時必須指定要保留的另一筆 record_id。"));
            }
        }

        var amount = working[AdminColumns.ExpectedAmount].Trim();
        if (amount.Length > 0 && !decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            messages.Add(new(AdminColumns.ExpectedAmount, "應付金額必須是數字。"));
        }

        return messages;
    }
}
