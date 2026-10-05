using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.Core.Rules;

public sealed record CapacitySummary(int Capacity, int Confirmed, int ApprovedPendingPayment, int Waitlisted, int Occupied)
{
    public int Available => Math.Max(0, Capacity - Occupied);
}

public sealed record DashboardSummary(
    CapacitySummary Capacity,
    IReadOnlyDictionary<string, int> ByStatus,
    int MembershipUnchecked,
    int EligibilityUnchecked,
    int PaymentPending,
    int RecordsWithErrors,
    int RecordsWithWarnings,
    int DuplicateCandidates,
    IReadOnlyDictionary<string, int> MealCounts,
    int Total,
    int Archived);

public static class DashboardCalculator
{
    private static readonly HashSet<string> InactiveStatuses = new(StringComparer.Ordinal)
    {
        RegistrationStatus.Rejected, RegistrationStatus.Cancelled, RegistrationStatus.PaymentExpired,
    };

    public static CapacitySummary Capacity(IReadOnlyList<Registration> registrations, EventConfig config)
    {
        var active = registrations.Where(r => !r.Admin.IsArchived).ToList();
        var occupying = config.SeatOccupyingStatuses;
        return new CapacitySummary(
            config.Capacity,
            active.Count(r => r.Admin.RegistrationStatus == RegistrationStatus.Confirmed),
            active.Count(r => r.Admin.RegistrationStatus == RegistrationStatus.ApprovedPendingPayment),
            active.Count(r => r.Admin.RegistrationStatus == RegistrationStatus.Waitlisted),
            active.Count(r => occupying.Contains(r.Admin.RegistrationStatus)));
    }

    public static DashboardSummary Calculate(IReadOnlyList<Registration> registrations, EventConfig config)
    {
        var active = registrations.Where(r => !r.Admin.IsArchived).ToList();
        var byStatus = active
            .GroupBy(r => r.Admin.RegistrationStatus, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // 午餐統計：排除不通過、取消、付款逾期
        var meals = active
            .Where(r => !InactiveStatuses.Contains(r.Admin.RegistrationStatus))
            .GroupBy(r => r.EffectiveMealCode(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new DashboardSummary(
            Capacity(registrations, config),
            byStatus,
            active.Count(r => r.Admin.MembershipStatus is "" or MembershipStatus.Unchecked),
            active.Count(r => r.Admin.EligibilityStatus is "" or EligibilityStatus.Unchecked or EligibilityStatus.NeedsInformation),
            active.Count(r => r.Admin.RegistrationStatus == RegistrationStatus.ApprovedPendingPayment
                              && r.Admin.PaymentStatus != PaymentStatus.Verified),
            active.Count(r => r.Issues.Any(i => !i.Accepted && i.Severity == IssueSeverity.Error)),
            active.Count(r => r.Issues.Any(i => !i.Accepted && i.Severity == IssueSeverity.Warning)),
            active.Count(r => r.Issues.Any(i => !i.Accepted && IssueCodes.IsDuplicate(i.Code))),
            meals,
            active.Count,
            registrations.Count - active.Count);
    }
}
