using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Issues;
using RegistrationAdmin.Core.Rules;
using RegistrationAdmin.Core.Sync;

namespace RegistrationAdmin.Core.UseCases;

public sealed record SyncReport(
    int SourceRows,
    int Linked,
    int Created,
    int Orphans,
    int Collisions,
    int PendingRelinks,
    int SourceChanged,
    DateTimeOffset SyncedAt);

/// <summary>
/// 報名後台的使用案例：重新整理（完整讀取＋合併）、驗證、儲存、人工重新連結。
/// 完整名單只存在記憶體與 Google Sheets，不建立本機資料庫。
/// </summary>
public sealed class RegistrationWorkspace
{
    private readonly IRegistrationStore _store;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly string _appVersion;
    private List<Registration> _registrations = new();

    public RegistrationWorkspace(IRegistrationStore store, IClock clock, IIdGenerator ids, string appVersion)
    {
        _store = store;
        _clock = clock;
        _ids = ids;
        _appVersion = appVersion;
    }

    public EventConfig Config { get; private set; } = EventConfig.Default;

    public LookupCatalog Lookups { get; private set; } = LookupCatalog.Default;

    public IReadOnlyList<Registration> Registrations => _registrations;

    public IReadOnlyList<PendingRelink> PendingRelinks { get; private set; } = Array.Empty<PendingRelink>();

    public IReadOnlyList<SourceResponse> CollidedSources { get; private set; } = Array.Empty<SourceResponse>();

    public IReadOnlySet<string> CollidedKeys { get; private set; } = new HashSet<string>();

    public SchemaHealthResult? Health { get; private set; }

    public string SourceSheetTitle { get; private set; } = "";

    public DateTimeOffset? LastSyncedAt { get; private set; }

    public bool IsLoaded => LastSyncedAt.HasValue;

    /// <summary>不屬於任何一筆報名的問題（碰撞來源列、待人工重新連結）。</summary>
    public IReadOnlyList<Issue> UnlinkedIssues =>
        IssueDetector.ForCollidedSources(CollidedSources).Concat(IssueDetector.ForPendingRelinks(PendingRelinks)).ToList();

    public IEnumerable<Issue> AllIssues => _registrations.SelectMany(r => r.Issues).Concat(UnlinkedIssues);

    public async Task<SyncReport> RefreshAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var config = snapshot.Config;
        var mappings = snapshot.FieldMappings
            .Where(m => m.ProfileId.Length == 0 || m.ProfileId == config.ProfileId)
            .ToList();
        if (mappings.Count == 0)
        {
            mappings = DefaultProfile.FieldMappings.ToList();
        }

        var health = SchemaHealthCheck.Check(snapshot.Source.Headers, mappings);
        Health = health;
        if (!health.IsHealthy)
        {
            // 欄位改名或缺漏：停止同步，不猜測、不寫 _Admin。
            throw new SchemaHealthException(health);
        }

        var sources = SourceParser.Parse(snapshot.Source, mappings, health, config.ProfileId);
        var now = _clock.Now;
        var plan = SyncMerger.Plan(sources, snapshot.AdminRows, config, now, _ids);

        if (plan.NewAdminRows.Count > 0)
        {
            var correlation = _ids.NewId();
            var log = plan.NewAdminRows
                .Select(r => ChangeLogEntry.Create(_ids.NewId(), correlation, r.RecordId, ChangeOperations.Create,
                    Array.Empty<string>(), TimeFormat.Iso(now), _appVersion, "同步新回應"))
                .ToList();
            await _store.AppendAdminRowsAsync(plan.NewAdminRows, log, cancellationToken).ConfigureAwait(false);
        }

        if (plan.MetadataUpdates.Count > 0)
        {
            await _store.UpdateAdminRowsAsync(plan.MetadataUpdates, Array.Empty<ChangeLogEntry>(), cancellationToken).ConfigureAwait(false);
        }

        Config = config;
        Lookups = new LookupCatalog(snapshot.Lookups);
        SourceSheetTitle = snapshot.Source.SheetTitle;
        PendingRelinks = plan.PendingRelinks;
        CollidedSources = plan.CollidedSources;
        CollidedKeys = plan.CollidedKeys;
        _registrations = plan.Registrations.ToList();
        IssueDetector.Apply(_registrations, CollidedKeys);
        LastSyncedAt = now;

        return new SyncReport(
            sources.Count,
            plan.Registrations.Count(r => r.Source is not null) - plan.NewAdminRows.Count,
            plan.NewAdminRows.Count,
            plan.OrphanCount,
            plan.CollidedSources.Count,
            plan.PendingRelinks.Count,
            _registrations.Count(r => r.SourceChanged),
            now);
    }

    public IReadOnlyList<ValidationMessage> ValidateTransition(Registration registration, AdminRecord working, string target) =>
        TransitionValidator.Validate(registration, working, target, _registrations, Config);

    /// <summary>驗證並在工作副本套用狀態轉換（尚未儲存）。</summary>
    public IReadOnlyList<ValidationMessage> TryApplyTransition(Registration registration, AdminRecord working, string target)
    {
        var messages = ValidateTransition(registration, working, target);
        if (messages.Count == 0)
        {
            TransitionValidator.Apply(working, target, _registrations, _clock.Now);
        }

        return messages;
    }

    public IReadOnlyList<ValidationMessage> ValidateSave(Registration registration, AdminRecord working) =>
        AdminRecordValidator.ValidateForSave(registration, working);

    /// <summary>
    /// 儲存一筆後台資料。寫入前由存取層重讀 row_version；不同時丟出衝突例外並保留未儲存值。
    /// </summary>
    /// <returns>是否有實際變更。</returns>
    public async Task<bool> SaveAsync(Registration registration, AdminRecord working, CancellationToken cancellationToken, string operation = ChangeOperations.Update)
    {
        var errors = ValidateSave(registration, working);
        if (errors.Count > 0)
        {
            throw new BusinessRuleException(errors);
        }

        return await WriteAsync(registration, working, operation, "", cancellationToken).ConfigureAwait(false);
    }

    public async Task RelinkAsync(PendingRelink pending, AdminRecord orphan, CancellationToken cancellationToken)
    {
        var registration = _registrations.FirstOrDefault(r => ReferenceEquals(r.Admin, orphan))
                           ?? throw new InvalidOperationException("找不到要重新連結的管理列，請重新整理。");
        if (registration.Source is not null)
        {
            throw new InvalidOperationException("此管理列已連結到原始回應。");
        }

        var working = SyncMerger.Relink(orphan, pending.Source);
        await WriteAsync(registration, working, ChangeOperations.Relink, $"改連原始回應第 {pending.Source.RowNumber} 列", cancellationToken)
            .ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AcceptSourceChangeAsync(Registration registration, CancellationToken cancellationToken)
    {
        if (registration.Source is null)
        {
            throw new InvalidOperationException("此報名沒有對應的原始回應。");
        }

        var working = registration.Admin.Clone();
        working[AdminColumns.SourceFingerprint] = registration.Source.Fingerprint;
        working[AdminColumns.SourceChanged] = SheetBool.Format(false);
        return WriteAsync(registration, working, ChangeOperations.AcceptSourceChange, "", cancellationToken);
    }

    public Task<bool> AcceptIssueAsync(Registration registration, string issueCode, string note, CancellationToken cancellationToken)
    {
        var working = registration.Admin.Clone();
        var codes = working.AcceptedIssueCodes.ToHashSet(StringComparer.Ordinal);
        codes.Add(issueCode);
        working[AdminColumns.AcceptedIssueCodes] = string.Join(",", codes.OrderBy(c => c, StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(note))
        {
            var existing = working[AdminColumns.IssueNotes].Trim();
            var line = $"{issueCode}: {note.Trim()}";
            working[AdminColumns.IssueNotes] = existing.Length == 0 ? line : existing + "\n" + line;
        }

        return WriteAsync(registration, working, ChangeOperations.AcceptIssue, issueCode, cancellationToken);
    }

    public DashboardSummary Dashboard() => DashboardCalculator.Calculate(_registrations, Config);

    /// <summary>單筆報名的異動紀錄（新到舊）；store 不支援時回傳空清單。</summary>
    public async Task<IReadOnlyList<ChangeLogEntry>> LoadHistoryAsync(string recordId, CancellationToken cancellationToken)
    {
        if (_store is not IChangeLogReader reader)
        {
            return Array.Empty<ChangeLogEntry>();
        }

        var entries = await reader.ReadChangeLogAsync(recordId, cancellationToken).ConfigureAwait(false);
        return entries.OrderByDescending(e => e.ChangedAt, StringComparer.Ordinal).ToList();
    }

    private async Task<bool> WriteAsync(Registration registration, AdminRecord working, string operation, string notes, CancellationToken cancellationToken)
    {
        var changed = working.DiffColumns(registration.Admin)
            .Where(c => !AdminColumns.SystemMaintained.Contains(c))
            .ToList();
        if (changed.Count == 0)
        {
            return false;
        }

        var now = _clock.Now;
        var expected = registration.Admin.RowVersion;
        var toWrite = working.Clone();
        toWrite.RowNumber = registration.Admin.RowNumber;
        toWrite[AdminColumns.RowVersion] = (expected + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        toWrite[AdminColumns.UpdatedAt] = TimeFormat.Iso(now);
        if (operation == ChangeOperations.Update
            && changed.Contains(AdminColumns.RegistrationStatus)
            && registration.Admin.RegistrationStatus != toWrite.RegistrationStatus)
        {
            operation = ChangeOperations.StatusChange;
        }

        var entry = ChangeLogEntry.Create(_ids.NewId(), _ids.NewId(), registration.RecordId, operation, changed,
            TimeFormat.Iso(now), _appVersion, notes);

        await _store.UpdateAdminRowsAsync(new[] { new AdminRowUpdate(toWrite, expected) }, new[] { entry }, cancellationToken)
            .ConfigureAwait(false);

        registration.Admin = toWrite;
        IssueDetector.Apply(_registrations, CollidedKeys);
        return true;
    }
}
