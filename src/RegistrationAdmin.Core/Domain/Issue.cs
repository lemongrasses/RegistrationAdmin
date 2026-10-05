namespace RegistrationAdmin.Core.Domain;

/// <summary>由目前原始回應與 _Admin 即時計算的資料問題（不另存 _Issues）。</summary>
public sealed record Issue(string Code, IssueSeverity Severity, string Field, string Message)
{
    public string RecordId { get; init; } = "";

    public string RegistrationNo { get; init; } = "";

    public string? RelatedRecordId { get; init; }

    public int? SourceRowNumber { get; init; }

    /// <summary>管理者已在 accepted_issue_codes 接受此例外。</summary>
    public bool Accepted { get; init; }
}

public static class IssueCodes
{
    public const string MissingPrefix = "MISSING_";
    public const string ConsentPrefix = "CONSENT_";
    public const string EmailFormat = "EMAIL_FORMAT";
    public const string PhoneFormat = "PHONE_FORMAT";
    public const string TaxIdFormat = "TAX_ID_FORMAT";
    public const string MealOtherText = "MEAL_OTHER_TEXT";
    public const string DuplicateEmail = "DUP_EMAIL";
    public const string DuplicatePhone = "DUP_PHONE";
    public const string DuplicateNameOrg = "DUP_NAME_ORG";
    public const string SourceChanged = "SOURCE_CHANGED";
    public const string SourceKeyCollision = "SOURCE_KEY_COLLISION";
    public const string Orphan = "ORPHAN_ADMIN";
    public const string AdminDuplicateKey = "ADMIN_DUPLICATE_KEY";
    public const string PossibleRelink = "POSSIBLE_RELINK";

    public static string Missing(string field) => MissingPrefix + field.ToUpperInvariant();

    public static string Consent(string field) => ConsentPrefix + field.ToUpperInvariant();

    public static bool IsDuplicate(string code) =>
        code is DuplicateEmail or DuplicatePhone or DuplicateNameOrg;
}

public sealed record ValidationMessage(string Field, string Message)
{
    public override string ToString() => Message;
}
