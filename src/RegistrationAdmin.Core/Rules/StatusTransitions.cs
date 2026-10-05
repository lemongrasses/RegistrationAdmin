using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.Core.Rules;

/// <summary>
/// 報名狀態轉換。不做背景自動轉換，每次變更都由管理者確認。
/// 規劃 9.1 之外，另允許「候補→取消」與「已確認→取消」，以處理報名者主動退出（待 Phase 0 確認）。
/// </summary>
public static class StatusTransitions
{
    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [RegistrationStatus.Submitted] = new[] { RegistrationStatus.UnderReview },
        [RegistrationStatus.UnderReview] = new[]
        {
            RegistrationStatus.ApprovedPendingPayment, RegistrationStatus.Waitlisted, RegistrationStatus.Rejected,
        },
        [RegistrationStatus.ApprovedPendingPayment] = new[]
        {
            RegistrationStatus.Confirmed, RegistrationStatus.PaymentExpired, RegistrationStatus.Cancelled,
        },
        [RegistrationStatus.Waitlisted] = new[] { RegistrationStatus.ApprovedPendingPayment, RegistrationStatus.Cancelled },
        [RegistrationStatus.Confirmed] = new[] { RegistrationStatus.Cancelled },
    };

    public static bool IsAllowed(string from, string to) =>
        Allowed.TryGetValue(NormalizeFrom(from), out var targets) && targets.Contains(to, StringComparer.Ordinal);

    public static IReadOnlyList<string> NextStatuses(string from) =>
        Allowed.TryGetValue(NormalizeFrom(from), out var targets) ? targets : Array.Empty<string>();

    private static string NormalizeFrom(string from) =>
        string.IsNullOrWhiteSpace(from) ? RegistrationStatus.Submitted : from.Trim();
}
