namespace RegistrationAdmin.Core.Domain;

/// <summary>_Lookups 的一列。</summary>
public sealed record LookupItem(string Domain, string Code, string LabelZh, int SortOrder, bool Active, string Notes = "")
{
    public static readonly IReadOnlyList<string> SheetColumns = new[]
    {
        "domain", "code", "label_zh", "sort_order", "active", "notes",
    };

    public IReadOnlyList<string> ToSheetRow() => new[]
    {
        Domain, Code, LabelZh, SortOrder.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SheetBool.Format(Active), Notes,
    };

    public static LookupItem? FromSheetRow(IReadOnlyDictionary<string, string> row)
    {
        string Get(string key) => row.TryGetValue(key, out var v) ? v.Trim() : "";
        var domain = Get("domain");
        var code = Get("code");
        if (domain.Length == 0 || code.Length == 0)
        {
            return null;
        }

        _ = int.TryParse(Get("sort_order"), out var sort);
        return new LookupItem(domain, code, Get("label_zh"), sort, SheetBool.Parse(Get("active"), true), Get("notes"));
    }
}

/// <summary>代碼表查詢。缺少的 domain 或代碼會退回內建預設值。</summary>
public sealed class LookupCatalog
{
    private readonly Dictionary<string, List<LookupItem>> _byDomain;

    public LookupCatalog(IEnumerable<LookupItem> items)
    {
        var all = items.ToList();
        foreach (var fallback in DefaultProfile.Lookups)
        {
            if (!all.Any(i => i.Domain == fallback.Domain && i.Code == fallback.Code))
            {
                all.Add(fallback);
            }
        }

        _byDomain = all
            .GroupBy(i => i.Domain, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.SortOrder).ThenBy(i => i.Code, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
    }

    public static LookupCatalog Default { get; } = new(Array.Empty<LookupItem>());

    public IReadOnlyList<LookupItem> Options(string domain, bool activeOnly = true) =>
        _byDomain.TryGetValue(domain, out var list)
            ? list.Where(i => !activeOnly || i.Active).ToList()
            : Array.Empty<LookupItem>();

    public string Label(string domain, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "";
        }

        if (_byDomain.TryGetValue(domain, out var list))
        {
            var hit = list.FirstOrDefault(i => i.Code == code);
            if (hit is not null && hit.LabelZh.Length > 0)
            {
                return hit.LabelZh;
            }
        }

        return code;
    }
}
