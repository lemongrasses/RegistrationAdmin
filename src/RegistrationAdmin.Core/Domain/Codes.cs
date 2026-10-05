namespace RegistrationAdmin.Core.Domain;

/// <summary>報名狀態代碼（與 _Lookups 的 registration_status 對應）。</summary>
public static class RegistrationStatus
{
    public const string Submitted = "submitted";
    public const string UnderReview = "under_review";
    public const string ApprovedPendingPayment = "approved_pending_payment";
    public const string Waitlisted = "waitlisted";
    public const string Rejected = "rejected";
    public const string Confirmed = "confirmed";
    public const string PaymentExpired = "payment_expired";
    public const string Cancelled = "cancelled";
}

public static class MembershipStatus
{
    public const string Unchecked = "unchecked";
    public const string Verified = "verified";
    public const string NotMember = "not_member";
    public const string Unverifiable = "unverifiable";
}

public static class EligibilityStatus
{
    public const string Unchecked = "unchecked";
    public const string Eligible = "eligible";
    public const string Ineligible = "ineligible";
    public const string NeedsInformation = "needs_information";
}

public static class PaymentStatus
{
    public const string NotRequested = "not_requested";
    public const string Pending = "pending";
    public const string Reported = "reported";
    public const string Verified = "verified";
    public const string Overdue = "overdue";
    public const string Issue = "issue";
    public const string Refunded = "refunded";
}

public static class InvoiceStatus
{
    public const string Pending = "pending";
    public const string Issued = "issued";
    public const string Cancelled = "cancelled";
}

public static class MealCode
{
    public const string Meat = "meat";
    public const string Vegetarian = "vegetarian";
    public const string None = "none";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Meat, Vegetarian, None, Other,
    };
}

public static class DuplicateStatus
{
    /// <summary>尚未判定（空白）。</summary>
    public const string Undecided = "";
    public const string NotDuplicate = "not_duplicate";
    public const string Duplicate = "duplicate";
}

public enum IssueSeverity
{
    Error = 0,
    Warning = 1,
    Info = 2,
}

public static class LookupDomains
{
    public const string RegistrationStatus = "registration_status";
    public const string MembershipStatus = "membership_status";
    public const string EligibilityStatus = "eligibility_status";
    public const string PaymentStatus = "payment_status";
    public const string InvoiceStatus = "invoice_status";
    public const string MealCode = "meal_code";
    public const string IssueSeverity = "issue_severity";
    public const string DuplicateStatus = "duplicate_status";
}
