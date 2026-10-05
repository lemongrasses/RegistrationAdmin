using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Normalization;

namespace RegistrationAdmin.Core.Search;

public enum IssueFilter
{
    Any = 0,
    HasError = 1,
    HasErrorOrWarning = 2,
    HasDuplicate = 3,
    NoOpenIssues = 4,
}

/// <summary>清單搜尋與組合篩選條件。空字串代表不限。</summary>
public sealed record RegistrationQuery
{
    public string Keyword { get; init; } = "";
    public string RegistrationStatus { get; init; } = "";
    public string MembershipStatus { get; init; } = "";
    public string EligibilityStatus { get; init; } = "";
    public string PaymentStatus { get; init; } = "";
    public string InvoiceStatus { get; init; } = "";
    public string MealCode { get; init; } = "";
    public IssueFilter Issues { get; init; } = IssueFilter.Any;
    public bool IncludeArchived { get; init; }

    public string Describe(LookupCatalog catalog)
    {
        var parts = new List<string>();
        if (Keyword.Trim().Length > 0)
        {
            parts.Add($"關鍵字「{Keyword.Trim()}」");
        }

        void Add(string label, string domain, string code)
        {
            if (code.Length > 0)
            {
                parts.Add($"{label}={catalog.Label(domain, code)}");
            }
        }

        Add("報名", LookupDomains.RegistrationStatus, RegistrationStatus);
        Add("會員", LookupDomains.MembershipStatus, MembershipStatus);
        Add("資格", LookupDomains.EligibilityStatus, EligibilityStatus);
        Add("付款", LookupDomains.PaymentStatus, PaymentStatus);
        Add("發票", LookupDomains.InvoiceStatus, InvoiceStatus);
        Add("午餐", LookupDomains.MealCode, MealCode);
        if (Issues != IssueFilter.Any)
        {
            parts.Add("問題=" + Issues switch
            {
                IssueFilter.HasError => "有錯誤",
                IssueFilter.HasErrorOrWarning => "有錯誤或警告",
                IssueFilter.HasDuplicate => "疑似重複",
                IssueFilter.NoOpenIssues => "無待處理問題",
                _ => "",
            });
        }

        parts.Add(IncludeArchived ? "含封存" : "不含封存");
        return string.Join("；", parts);
    }
}

/// <summary>記憶體內搜尋／篩選，不呼叫 API。</summary>
public static class RegistrationFilter
{
    public static IReadOnlyList<Registration> Apply(IEnumerable<Registration> registrations, RegistrationQuery query)
    {
        var keyword = Normalizers.Nfkc(query.Keyword).ToLowerInvariant();
        var phoneKeyword = Normalizers.Phone(query.Keyword);

        return registrations.Where(r =>
                (query.IncludeArchived || !r.Admin.IsArchived)
                && Matches(query.RegistrationStatus, r.Admin.RegistrationStatus)
                && Matches(query.MembershipStatus, r.Admin.MembershipStatus)
                && Matches(query.EligibilityStatus, r.Admin.EligibilityStatus)
                && Matches(query.PaymentStatus, r.Admin.PaymentStatus)
                && Matches(query.InvoiceStatus, r.Admin.InvoiceStatus)
                && Matches(query.MealCode, r.EffectiveMealCode())
                && MatchesIssues(query.Issues, r)
                && MatchesKeyword(keyword, phoneKeyword, r))
            .ToList();
    }

    private static bool Matches(string wanted, string actual) =>
        wanted.Length == 0 || string.Equals(wanted, actual, StringComparison.Ordinal);

    private static bool MatchesIssues(IssueFilter filter, Registration r)
    {
        var open = r.Issues.Where(i => !i.Accepted).ToList();
        return filter switch
        {
            IssueFilter.HasError => open.Any(i => i.Severity == IssueSeverity.Error),
            IssueFilter.HasErrorOrWarning => open.Any(i => i.Severity is IssueSeverity.Error or IssueSeverity.Warning),
            IssueFilter.HasDuplicate => open.Any(i => IssueCodes.IsDuplicate(i.Code)),
            IssueFilter.NoOpenIssues => open.Count == 0,
            _ => true,
        };
    }

    private static bool MatchesKeyword(string keyword, string phoneKeyword, Registration r)
    {
        if (keyword.Length == 0)
        {
            return true;
        }

        bool Has(string? value) => Normalizers.Nfkc(value).ToLowerInvariant().Contains(keyword, StringComparison.Ordinal);

        return Has(r.Effective(LogicalFields.FullName))
               || Has(r.Raw(LogicalFields.FullName))
               || Has(r.Effective(LogicalFields.Email))
               || Has(r.Effective(LogicalFields.OrganizationName))
               || Has(r.Raw(LogicalFields.OrganizationName))
               || Has(r.RegistrationNo)
               || Has(r.Effective(LogicalFields.Phone))
               || (phoneKeyword.Length >= 3 && r.EffectivePhoneKey().Contains(phoneKeyword, StringComparison.Ordinal));
    }
}
