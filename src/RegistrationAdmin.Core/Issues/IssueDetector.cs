using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Normalization;

namespace RegistrationAdmin.Core.Issues;

public sealed record DuplicateCandidate(string RecordId, string OtherRecordId, string Code, string Reason);

/// <summary>
/// 疑似重複只產生候選，不自動合併、封存或覆寫。
/// 規則：相同標準化 Email、相同標準化電話、同名且同單位。同名但聯絡方式不同不列入。
/// </summary>
public static class DuplicateDetector
{
    public static IReadOnlyList<DuplicateCandidate> Find(IReadOnlyList<Registration> registrations)
    {
        var pool = registrations
            .Where(r => r.Source is not null && !r.Admin.IsArchived && r.Admin.DuplicateStatus.Length == 0)
            .ToList();
        var result = new List<DuplicateCandidate>();

        AddGroups(pool, r => r.EffectiveEmailKey(), IssueCodes.DuplicateEmail, "與其他報名使用相同 Email", result);
        AddGroups(pool, r => r.EffectivePhoneKey(), IssueCodes.DuplicatePhone, "與其他報名使用相同電話", result);
        AddGroups(
            pool,
            r =>
            {
                var name = Normalizers.NameKey(r.Effective(LogicalFields.FullName));
                var org = Normalizers.NameKey(r.Effective(LogicalFields.OrganizationName));
                return name.Length == 0 || org.Length == 0 ? "" : name + "\u001F" + org;
            },
            IssueCodes.DuplicateNameOrg,
            "與其他報名同名且同單位",
            result);

        return result;
    }

    private static void AddGroups(
        IReadOnlyList<Registration> pool,
        Func<Registration, string> keySelector,
        string code,
        string reason,
        List<DuplicateCandidate> result)
    {
        foreach (var group in pool.GroupBy(keySelector, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1))
        {
            var members = group.ToList();
            foreach (var a in members)
            {
                foreach (var b in members.Where(b => b.RecordId != a.RecordId))
                {
                    result.Add(new DuplicateCandidate(a.RecordId, b.RecordId, code, reason));
                }
            }
        }
    }
}

/// <summary>依目前原始回應與 _Admin 即時計算資料問題。</summary>
public static class IssueDetector
{
    public static void Apply(IReadOnlyList<Registration> registrations, IReadOnlySet<string> collidedKeys)
    {
        var duplicates = DuplicateDetector.Find(registrations)
            .GroupBy(d => d.RecordId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var adminKeyCounts = registrations
            .Where(r => r.Admin.SourceKey.Length > 0)
            .GroupBy(r => r.Admin.SourceKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byId = registrations
            .GroupBy(r => r.RecordId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var registration in registrations)
        {
            var issues = new List<Issue>();
            var source = registration.Source;

            if (source is null)
            {
                if (collidedKeys.Contains(registration.Admin.SourceKey))
                {
                    issues.Add(new Issue(IssueCodes.SourceKeyCollision, IssueSeverity.Error, "source_key",
                        "原始回應有多列產生相同 source_key 但內容不同，已停止自動關聯，請人工確認。"));
                }
                else if (adminKeyCounts.GetValueOrDefault(registration.Admin.SourceKey) > 1)
                {
                    issues.Add(new Issue(IssueCodes.AdminDuplicateKey, IssueSeverity.Error, "source_key",
                        "_Admin 有多列使用相同 source_key，已只連結第一列；請人工封存多餘的管理列。"));
                }
                else
                {
                    issues.Add(new Issue(IssueCodes.Orphan, IssueSeverity.Error, "source",
                        "找不到對應的原始表單回應（可能被刪除，或時間戳／Email／電話被修改）。管理資料未刪除。"));
                }
            }
            else
            {
                AddSourceIssues(registration, issues);
            }

            if (duplicates.TryGetValue(registration.RecordId, out var dupes))
            {
                foreach (var dupe in dupes)
                {
                    var otherNo = byId.TryGetValue(dupe.OtherRecordId, out var other) ? other.RegistrationNo : dupe.OtherRecordId;
                    issues.Add(new Issue(dupe.Code, IssueSeverity.Warning, "duplicate", $"{dupe.Reason}（{otherNo}），請人工判斷。")
                    {
                        RelatedRecordId = dupe.OtherRecordId,
                    });
                }
            }

            var accepted = registration.Admin.AcceptedIssueCodes;
            registration.Issues = issues
                .Select(i => i with
                {
                    RecordId = registration.RecordId,
                    RegistrationNo = registration.RegistrationNo,
                    SourceRowNumber = source?.RowNumber,
                    Accepted = accepted.Contains(i.Code),
                })
                .ToList();
        }
    }

    /// <summary>source_key 碰撞的原始列（尚未連到任何管理列）。</summary>
    public static IReadOnlyList<Issue> ForCollidedSources(IEnumerable<SourceResponse> collided) =>
        collided.Select(s => new Issue(IssueCodes.SourceKeyCollision, IssueSeverity.Error, "source_key",
                $"原始回應第 {s.RowNumber} 列與其他列的時間戳、Email、電話相同但內容不同，已停止自動關聯。")
            {
                SourceRowNumber = s.RowNumber,
            })
            .ToList();

    public static IReadOnlyList<Issue> ForPendingRelinks(IEnumerable<Sync.PendingRelink> pending) =>
        pending.Select(p => new Issue(IssueCodes.PossibleRelink, IssueSeverity.Warning, "source",
                $"原始回應第 {p.Source.RowNumber} 列可能是來源關聯變更（關鍵欄被修改），未自動建立新管理資料；請到「人工重新連結」處理。")
            {
                SourceRowNumber = p.Source.RowNumber,
            })
            .ToList();

    private static void AddSourceIssues(Registration registration, List<Issue> issues)
    {
        foreach (var (field, label) in LogicalFields.RequiredForApproval)
        {
            if (registration.Effective(field).Trim().Length == 0)
            {
                issues.Add(new Issue(IssueCodes.Missing(field), IssueSeverity.Error, field, $"{label}空白。"));
            }
        }

        foreach (var (field, label) in LogicalFields.Consents)
        {
            if (!registration.Consent(field))
            {
                issues.Add(new Issue(IssueCodes.Consent(field), IssueSeverity.Error, field, $"缺少確認：{label}。"));
            }
        }

        var email = registration.Effective(LogicalFields.Email);
        if (email.Length > 0 && !Normalizers.IsValidEmail(email))
        {
            issues.Add(new Issue(IssueCodes.EmailFormat, IssueSeverity.Warning, LogicalFields.Email, "Email 格式可能不正確。"));
        }

        var phone = registration.Effective(LogicalFields.Phone);
        if (phone.Length > 0 && !Normalizers.IsPlausiblePhone(phone))
        {
            issues.Add(new Issue(IssueCodes.PhoneFormat, IssueSeverity.Warning, LogicalFields.Phone, "電話不是 8–15 位數字，請確認（可能遺失開頭的 0）。"));
        }

        var taxId = registration.Effective(LogicalFields.TaxId);
        if (taxId.Length > 0 && !Normalizers.IsTaxId8Digits(taxId))
        {
            issues.Add(new Issue(IssueCodes.TaxIdFormat, IssueSeverity.Warning, LogicalFields.TaxId,
                "統一編號不是 8 位數字；已保留原值，請確認是否為個人發票。"));
        }

        var meal = registration.EffectiveMealCode();
        if (meal.Length == 0)
        {
            issues.Add(new Issue(IssueCodes.Missing(LogicalFields.Meal), IssueSeverity.Error, LogicalFields.Meal, "午餐需求空白。"));
        }
        else if (meal == MealCode.Other
                 && registration.EffectiveMealOtherText().Length == 0
                 && registration.Admin[AdminColumns.DietaryNotes].Trim().Length == 0)
        {
            issues.Add(new Issue(IssueCodes.MealOtherText, IssueSeverity.Warning, LogicalFields.Meal, "午餐為「其他」但沒有說明。"));
        }

        if (registration.SourceChanged)
        {
            issues.Add(new Issue(IssueCodes.SourceChanged, IssueSeverity.Warning, "source",
                "原始回答在建立管理資料後被修改過（fingerprint 不同）。請檢視原始回應後按「接受來源變更」。"));
        }
    }
}
