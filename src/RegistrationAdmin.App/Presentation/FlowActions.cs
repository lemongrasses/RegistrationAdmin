using System.Globalization;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Rules;
using RegistrationAdmin.Core.UseCases;

namespace RegistrationAdmin.App.Presentation;

public enum FlowKind
{
    /// <summary>依序套用一或多個合法的狀態轉換。</summary>
    Transition,
    MarkRefunded,
    Archive,
    Unarchive,
}

/// <param name="Path">依序套用的目標狀態（每一步都必須是 <see cref="StatusTransitions"/> 允許的轉換）。</param>
/// <param name="ReasonColumn">需要填寫理由時寫入的欄位；null 代表不需理由。</param>
public sealed record FlowAction(
    string Id,
    string Label,
    string Description,
    FlowKind Kind,
    IReadOnlyList<string> Path,
    bool RequiresConfirm = false,
    string? ReasonColumn = null,
    string ReasonLabel = "")
{
    public override string ToString() => Label;
}

/// <summary>
/// 詳細頁的「下一步」按鈕。只提供目前合法的步驟；不修改狀態機，只是把常見的
/// 多個欄位設定（例如審核通過時的會員與資格）合併成一個動作，儲存前仍會列出所有變更。
/// </summary>
public static class FlowActions
{
    public static FlowAction? Primary(AdminRecord working)
    {
        var status = Status(working);
        return status switch
        {
            RegistrationStatus.Submitted when IsUnchecked(working.MembershipStatus) => new FlowAction(
                "start_review", "確認會員並開始審核",
                "會員核對改為「已確認會員」，報名狀態改為「審核中」。",
                FlowKind.Transition, new[] { RegistrationStatus.UnderReview }),
            RegistrationStatus.Submitted => new FlowAction(
                "start_review", "開始審核", "報名狀態改為「審核中」。",
                FlowKind.Transition, new[] { RegistrationStatus.UnderReview }),
            RegistrationStatus.UnderReview => new FlowAction(
                "approve", "通過並等待付款", ApproveDescription(working),
                FlowKind.Transition, new[] { RegistrationStatus.ApprovedPendingPayment }),
            RegistrationStatus.Waitlisted => new FlowAction(
                "approve", "從候補改為通過並等待付款", ApproveDescription(working),
                FlowKind.Transition, new[] { RegistrationStatus.ApprovedPendingPayment }),
            RegistrationStatus.ApprovedPendingPayment => new FlowAction(
                "confirm", "確認已付款，完成報名",
                "付款狀態改為「已核帳」（付款與核帳日期未填時填入今天），報名狀態改為「已確認」。",
                FlowKind.Transition, new[] { RegistrationStatus.Confirmed }),
            _ => null,
        };
    }

    /// <summary>低頻或高風險的動作，放在「其他動作…」，執行前一律確認。</summary>
    public static IReadOnlyList<FlowAction> Others(AdminRecord working)
    {
        var status = Status(working);
        var list = new List<FlowAction>();

        void Reject(params string[] path) => list.Add(new FlowAction(
            "reject", "不通過…", "報名狀態改為「不通過」。需要填寫理由，理由會記在審核備註。",
            FlowKind.Transition, path, true, AdminColumns.ReviewNotes, "不通過的理由"));

        void Cancel() => list.Add(new FlowAction(
            "cancel", "取消報名…", "報名狀態改為「已取消」並釋出名額。需要填寫取消原因。",
            FlowKind.Transition, new[] { RegistrationStatus.Cancelled }, true, AdminColumns.CancellationReason, "取消原因"));

        switch (status)
        {
            case RegistrationStatus.Submitted:
                Reject(RegistrationStatus.UnderReview, RegistrationStatus.Rejected);
                break;
            case RegistrationStatus.UnderReview:
                list.Add(new FlowAction("waitlist", "改為候補", "報名狀態改為「候補」，系統會排入下一個候補順位。",
                    FlowKind.Transition, new[] { RegistrationStatus.Waitlisted }, true));
                Reject(RegistrationStatus.Rejected);
                break;
            case RegistrationStatus.ApprovedPendingPayment:
                list.Add(new FlowAction("expire", "標記付款逾期", "報名狀態改為「付款逾期」並釋出名額。系統不會自動通知報名者。",
                    FlowKind.Transition, new[] { RegistrationStatus.PaymentExpired }, true));
                Cancel();
                break;
            case RegistrationStatus.Waitlisted:
            case RegistrationStatus.Confirmed:
                Cancel();
                break;
        }

        if (working.PaymentStatus == PaymentStatus.Verified
            && status is RegistrationStatus.Confirmed or RegistrationStatus.Cancelled)
        {
            list.Add(new FlowAction("refund", "標記為已退款", "付款狀態改為「已退款」。系統不會實際退款，請先完成退款再標記。",
                FlowKind.MarkRefunded, Array.Empty<string>(), true));
        }

        list.Add(working.IsArchived
            ? new FlowAction("unarchive", "取消封存", "這筆報名會重新出現在名單中。", FlowKind.Unarchive, Array.Empty<string>(), true)
            : new FlowAction("archive", "封存這筆報名", "封存後預設名單不再顯示這筆，但資料不會刪除，之後可以取消封存。",
                FlowKind.Archive, Array.Empty<string>(), true));

        return list;
    }

    /// <summary>在工作副本套用動作；任何一步驗證失敗就整個還原並回傳原因。</summary>
    public static IReadOnlyList<ValidationMessage> Execute(
        FlowAction action,
        Registration registration,
        AdminRecord working,
        RegistrationWorkspace workspace,
        DateTime today,
        string? reason = null)
    {
        var snapshot = working.Clone();
        var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (action.ReasonColumn is { } column && !string.IsNullOrWhiteSpace(reason))
        {
            var existing = working[column].Trim();
            working[column] = existing.Length == 0 ? reason.Trim() : existing + "\n" + reason.Trim();
        }

        switch (action.Kind)
        {
            case FlowKind.MarkRefunded:
                working[AdminColumns.PaymentStatus] = PaymentStatus.Refunded;
                return Array.Empty<ValidationMessage>();
            case FlowKind.Archive:
                working[AdminColumns.IsArchived] = SheetBool.Format(true);
                return Array.Empty<ValidationMessage>();
            case FlowKind.Unarchive:
                working[AdminColumns.IsArchived] = SheetBool.Format(false);
                return Array.Empty<ValidationMessage>();
        }

        foreach (var target in action.Path)
        {
            Prefill(working, target, todayText);
            var messages = workspace.TryApplyTransition(registration, working, target);
            if (messages.Count > 0)
            {
                Restore(working, snapshot);
                return messages;
            }
        }

        return Array.Empty<ValidationMessage>();
    }

    private static void Prefill(AdminRecord working, string target, string today)
    {
        switch (target)
        {
            case RegistrationStatus.UnderReview when IsUnchecked(working.MembershipStatus):
                working[AdminColumns.MembershipStatus] = MembershipStatus.Verified;
                break;
            case RegistrationStatus.ApprovedPendingPayment:
                if (IsUnchecked(working.MembershipStatus))
                {
                    working[AdminColumns.MembershipStatus] = MembershipStatus.Verified;
                }

                if (IsUnchecked(working.EligibilityStatus))
                {
                    working[AdminColumns.EligibilityStatus] = EligibilityStatus.Eligible;
                }

                break;
            case RegistrationStatus.Confirmed:
                working[AdminColumns.PaymentStatus] = PaymentStatus.Verified;
                if (working[AdminColumns.PaidAt].Trim().Length == 0)
                {
                    working[AdminColumns.PaidAt] = today;
                }

                if (working[AdminColumns.VerifiedAt].Trim().Length == 0)
                {
                    working[AdminColumns.VerifiedAt] = today;
                }

                break;
        }
    }

    private static string ApproveDescription(AdminRecord working)
    {
        var parts = new List<string>();
        if (IsUnchecked(working.MembershipStatus))
        {
            parts.Add("會員核對改為「已確認會員」");
        }

        if (IsUnchecked(working.EligibilityStatus))
        {
            parts.Add("資格改為「符合資格」");
        }

        parts.Add("保留一個名額，付款狀態改為「待付款」");
        return string.Join("，", parts) + "。";
    }

    private static void Restore(AdminRecord working, AdminRecord snapshot)
    {
        foreach (var key in working.Values.Keys.Union(snapshot.Values.Keys, StringComparer.Ordinal).ToList())
        {
            working[key] = snapshot[key];
        }
    }

    private static bool IsUnchecked(string code) => code is "" or "unchecked";

    private static string Status(AdminRecord working) =>
        string.IsNullOrWhiteSpace(working.RegistrationStatus) ? RegistrationStatus.Submitted : working.RegistrationStatus;
}
