using RegistrationAdmin.Core.Normalization;

namespace RegistrationAdmin.Core.Domain;

/// <summary>
/// 畫面上的一筆報名：原始回應（唯讀）＋ _Admin 後台資料。
/// 有效值規則：override 有值則優先，否則使用原始回應。
/// </summary>
public sealed class Registration
{
    private static readonly IReadOnlyDictionary<string, string> OverrideColumnByField = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LogicalFields.FullName] = AdminColumns.OverrideName,
        [LogicalFields.OrganizationName] = AdminColumns.OverrideOrganization,
        [LogicalFields.JobTitle] = AdminColumns.OverrideJobTitle,
        [LogicalFields.Email] = AdminColumns.OverrideEmail,
        [LogicalFields.Phone] = AdminColumns.OverridePhone,
        [LogicalFields.InvoiceTitle] = AdminColumns.OverrideInvoiceTitle,
        [LogicalFields.TaxId] = AdminColumns.OverrideTaxId,
    };

    public Registration(AdminRecord admin, SourceResponse? source)
    {
        Admin = admin;
        Source = source;
    }

    public AdminRecord Admin { get; internal set; }

    /// <summary>對應的原始回應；找不到（orphan）或 source_key 碰撞時為 null。</summary>
    public SourceResponse? Source { get; internal set; }

    public IReadOnlyList<Issue> Issues { get; internal set; } = Array.Empty<Issue>();

    public string RecordId => Admin.RecordId;

    public string RegistrationNo => Admin.RegistrationNo;

    public bool IsOrphan => Source is null;

    public bool SourceChanged =>
        Source is not null
        && Admin.SourceFingerprint.Length > 0
        && !string.Equals(Admin.SourceFingerprint, Source.Fingerprint, StringComparison.Ordinal);

    public static string? OverrideColumnFor(string logicalField) =>
        OverrideColumnByField.TryGetValue(logicalField, out var c) ? c : null;

    public static IEnumerable<string> OverridableFields => OverrideColumnByField.Keys;

    public string Raw(string logicalField) => Source?.Get(logicalField) ?? "";

    public string Effective(string logicalField) => Effective(logicalField, Admin);

    /// <summary>以指定的（可能尚未儲存的）後台資料計算有效值。</summary>
    public string Effective(string logicalField, AdminRecord admin)
    {
        var column = OverrideColumnFor(logicalField);
        if (column is not null)
        {
            var overridden = admin[column].Trim();
            if (overridden.Length > 0)
            {
                return overridden;
            }
        }

        return Raw(logicalField);
    }

    public bool Consent(string logicalField) => Source?.Flag(logicalField) ?? false;

    public string EffectiveMealCode(AdminRecord? admin = null)
    {
        var a = admin ?? Admin;
        var overridden = a[AdminColumns.OverrideMealCode].Trim();
        return overridden.Length > 0 ? overridden : Source?.MealCode ?? "";
    }

    public string EffectiveMealOtherText(AdminRecord? admin = null)
    {
        var a = admin ?? Admin;
        var text = a[AdminColumns.MealOtherText].Trim();
        return text.Length > 0 ? text : Source?.MealOtherText ?? "";
    }

    public string EffectiveEmailKey(AdminRecord? admin = null) => Normalizers.Email(Effective(LogicalFields.Email, admin ?? Admin));

    public string EffectivePhoneKey(AdminRecord? admin = null) => Normalizers.Phone(Effective(LogicalFields.Phone, admin ?? Admin));
}
