using System.Globalization;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;

namespace RegistrationAdmin.Core.Sync;

/// <summary>新來源與某筆找不到來源的管理列可能是同一申請（關鍵欄被修改），需人工決定。</summary>
public sealed record PendingRelink(SourceResponse Source, IReadOnlyList<AdminRecord> Candidates);

public sealed class SyncPlan
{
    /// <summary>已連結、新建立與找不到來源的管理資料。</summary>
    public required IReadOnlyList<Registration> Registrations { get; init; }

    /// <summary>需要附加到 _Admin 的新列（新來源）。</summary>
    public required IReadOnlyList<AdminRecord> NewAdminRows { get; init; }

    /// <summary>新列對應的原始回應（與 NewAdminRows 同順序）。</summary>
    public required IReadOnlyList<SourceResponse> NewAdminSources { get; init; }

    public required IReadOnlyList<SourceResponse> CollidedSources { get; init; }

    public required IReadOnlySet<string> CollidedKeys { get; init; }

    public required IReadOnlyList<PendingRelink> PendingRelinks { get; init; }

    /// <summary>只更新 source_row_hint／source_changed，不檢查 row_version、不遞增版本。</summary>
    public required IReadOnlyList<AdminRowUpdate> MetadataUpdates { get; init; }

    public int OrphanCount => Registrations.Count(r => r.Source is null);
}

/// <summary>
/// 原始回應與 _Admin 合併（規劃 8.2）。
/// 以 source_key 連結，不依賴列號；相同 key 不重複建立；碰撞或可能的關聯變更交人工處理；
/// 找不到來源的管理列標為 orphan，不刪除。
/// </summary>
public static class SyncMerger
{
    public static SyncPlan Plan(
        IReadOnlyList<SourceResponse> sources,
        IReadOnlyList<AdminRecord> adminRows,
        EventConfig config,
        DateTimeOffset now,
        IIdGenerator ids)
    {
        var profileId = config.ProfileId;

        var collidedKeys = sources
            .GroupBy(s => s.SourceKey, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var collided = sources.Where(s => collidedKeys.Contains(s.SourceKey)).ToList();
        var uniqueSources = sources.Where(s => !collidedKeys.Contains(s.SourceKey)).ToList();

        var activeAdmins = adminRows
            .Where(a => a.RecordId.Length > 0)
            .Where(a => a[AdminColumns.ProfileId].Length == 0 || a[AdminColumns.ProfileId] == profileId)
            .ToList();

        var adminByKey = new Dictionary<string, AdminRecord>(StringComparer.Ordinal);
        var seenRecordIds = new HashSet<string>(StringComparer.Ordinal);
        var extraAdmins = new HashSet<AdminRecord>(ReferenceEqualityComparer.Instance);
        foreach (var admin in activeAdmins)
        {
            if (!seenRecordIds.Add(admin.RecordId))
            {
                extraAdmins.Add(admin);
                continue;
            }

            if (admin.SourceKey.Length > 0 && !adminByKey.ContainsKey(admin.SourceKey))
            {
                adminByKey[admin.SourceKey] = admin;
            }
            else
            {
                extraAdmins.Add(admin);
            }
        }

        var registrations = new List<Registration>();
        var linked = new HashSet<AdminRecord>(ReferenceEqualityComparer.Instance);
        var metadata = new List<AdminRowUpdate>();
        var unmatched = new List<SourceResponse>();

        foreach (var source in uniqueSources)
        {
            if (adminByKey.TryGetValue(source.SourceKey, out var admin))
            {
                registrations.Add(new Registration(admin, source));
                linked.Add(admin);

                var columns = new List<string>();
                var hint = source.RowNumber.ToString(CultureInfo.InvariantCulture);
                if (admin[AdminColumns.SourceRowHint] != hint)
                {
                    admin[AdminColumns.SourceRowHint] = hint;
                    columns.Add(AdminColumns.SourceRowHint);
                }

                var changed = SheetBool.Format(admin.SourceFingerprint.Length > 0 && admin.SourceFingerprint != source.Fingerprint);
                if (admin[AdminColumns.SourceChanged] != changed)
                {
                    admin[AdminColumns.SourceChanged] = changed;
                    columns.Add(AdminColumns.SourceChanged);
                }

                if (columns.Count > 0)
                {
                    metadata.Add(new AdminRowUpdate(admin, null, columns));
                }
            }
            else
            {
                unmatched.Add(source);
            }
        }

        var orphans = activeAdmins.Where(a => !linked.Contains(a)).ToList();
        var relinkPool = orphans
            .Where(o => !extraAdmins.Contains(o) && !collidedKeys.Contains(o.SourceKey) && !o.IsArchived)
            .ToList();

        var pending = new List<PendingRelink>();
        var toCreate = new List<SourceResponse>();
        foreach (var source in unmatched)
        {
            var candidates = relinkPool.Where(o => IsRelinkCandidate(o, source)).ToList();
            if (candidates.Count > 0)
            {
                pending.Add(new PendingRelink(source, candidates));
            }
            else
            {
                toCreate.Add(source);
            }
        }

        var nextNumber = NextRegistrationNumber(adminRows, config.RegistrationNoPrefix);
        var newRows = new List<AdminRecord>();
        var newSources = new List<SourceResponse>();
        foreach (var source in toCreate.OrderBy(s => s.TimestampSerial ?? double.MaxValue).ThenBy(s => s.RowNumber))
        {
            var row = CreateAdminRecord(source, config, FormatRegistrationNo(config.RegistrationNoPrefix, nextNumber++), now, ids);
            newRows.Add(row);
            newSources.Add(source);
            registrations.Add(new Registration(row, source));
        }

        registrations.AddRange(orphans.Select(o => new Registration(o, null)));
        registrations.Sort((a, b) => string.CompareOrdinal(a.RegistrationNo, b.RegistrationNo));

        return new SyncPlan
        {
            Registrations = registrations,
            NewAdminRows = newRows,
            NewAdminSources = newSources,
            CollidedSources = collided,
            CollidedKeys = collidedKeys,
            PendingRelinks = pending,
            MetadataUpdates = metadata,
        };
    }

    public static AdminRecord CreateAdminRecord(SourceResponse source, EventConfig config, string registrationNo, DateTimeOffset now, IIdGenerator ids)
    {
        var stamp = TimeFormat.Iso(now);
        var row = new AdminRecord
        {
            [AdminColumns.RecordId] = ids.NewId(),
            [AdminColumns.RegistrationNo] = registrationNo,
            [AdminColumns.ProfileId] = config.ProfileId,
            [AdminColumns.SourceKey] = source.SourceKey,
            [AdminColumns.SourceTimestamp] = source.TimestampText,
            [AdminColumns.SourceEmailSnapshot] = source.NormalizedEmail,
            [AdminColumns.SourceRowHint] = source.RowNumber.ToString(CultureInfo.InvariantCulture),
            [AdminColumns.SourceFingerprint] = source.Fingerprint,
            [AdminColumns.SourceChanged] = SheetBool.Format(false),
            [AdminColumns.MembershipStatus] = MembershipStatus.Unchecked,
            [AdminColumns.EligibilityStatus] = EligibilityStatus.Unchecked,
            [AdminColumns.RegistrationStatus] = RegistrationStatus.Submitted,
            [AdminColumns.ExpectedAmount] = config.FeeAmount.ToString("0.##", CultureInfo.InvariantCulture),
            [AdminColumns.PaymentStatus] = PaymentStatus.NotRequested,
            [AdminColumns.InvoiceStatus] = InvoiceStatus.Pending,
            [AdminColumns.IsArchived] = SheetBool.Format(false),
            [AdminColumns.RowVersion] = "1",
            [AdminColumns.CreatedAt] = stamp,
            [AdminColumns.UpdatedAt] = stamp,
        };
        return row;
    }

    /// <summary>人工重新連結：把找不到來源的管理列改連到新的原始回應。</summary>
    public static AdminRecord Relink(AdminRecord orphan, SourceResponse source)
    {
        var working = orphan.Clone();
        working[AdminColumns.SourceKey] = source.SourceKey;
        working[AdminColumns.SourceTimestamp] = source.TimestampText;
        working[AdminColumns.SourceEmailSnapshot] = source.NormalizedEmail;
        working[AdminColumns.SourceRowHint] = source.RowNumber.ToString(CultureInfo.InvariantCulture);
        working[AdminColumns.SourceFingerprint] = source.Fingerprint;
        working[AdminColumns.SourceChanged] = SheetBool.Format(false);
        return working;
    }

    public static string FormatRegistrationNo(string prefix, int number) =>
        prefix + number.ToString("D4", CultureInfo.InvariantCulture);

    private static bool IsRelinkCandidate(AdminRecord orphan, SourceResponse source)
    {
        var ts = orphan[AdminColumns.SourceTimestamp];
        var email = orphan[AdminColumns.SourceEmailSnapshot];
        return (ts.Length > 0 && ts == source.TimestampText)
               || (email.Length > 0 && email == source.NormalizedEmail);
    }

    private static int NextRegistrationNumber(IEnumerable<AdminRecord> adminRows, string prefix)
    {
        var max = 0;
        foreach (var row in adminRows)
        {
            var no = row.RegistrationNo;
            if (no.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(no[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                max = Math.Max(max, n);
            }
        }

        return max + 1;
    }
}
