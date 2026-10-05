using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.Tests;

public class SyncTests
{
    private static RegistrationWorkspace NewWorkspace(FakeStore store, SequentialIds? ids = null) =>
        new(store, new FixedClock(), ids ?? new SequentialIds(), "test");

    private static Dictionary<string, string> RecordIdByName(RegistrationWorkspace ws) =>
        ws.Registrations
            .Where(r => r.Source is not null)
            .ToDictionary(r => r.Source!.NormalizedEmail + "|" + r.Raw(LogicalFields.FullName), r => r.RecordId);

    private static void AssertSameMap(Dictionary<string, string> expected, Dictionary<string, string> actual) =>
        Assert.Equal(
            expected.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value),
            actual.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

    [Fact]
    public async Task UAT02_first_sync_creates_one_admin_row_per_response()
    {
        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE));
        var ws = NewWorkspace(store);

        var report = await ws.RefreshAsync(CancellationToken.None);

        Assert.Equal(5, ws.Registrations.Count);
        Assert.Equal(5, store.AdminRows.Count);
        Assert.Equal(5, report.Created);
        Assert.Equal(5, store.AdminRows.Select(a => a.RecordId).Distinct().Count());
        Assert.Equal(5, store.AdminRows.Select(a => a.RegistrationNo).Distinct().Count());
        Assert.Equal(5, store.AdminRows.Select(a => a.SourceKey).Distinct().Count());
        Assert.All(store.AdminRows, a =>
        {
            Assert.Equal(RegistrationStatus.Submitted, a.RegistrationStatus);
            Assert.Equal(MembershipStatus.Unchecked, a.MembershipStatus);
            Assert.Equal(EligibilityStatus.Unchecked, a.EligibilityStatus);
            Assert.Equal(PaymentStatus.NotRequested, a.PaymentStatus);
            Assert.Equal(1, a.RowVersion);
        });
        Assert.Equal(new[] { "RD26-0001", "RD26-0002", "RD26-0003", "RD26-0004", "RD26-0005" },
            ws.Registrations.Select(r => r.RegistrationNo).ToArray());
    }

    [Fact]
    public async Task UAT03_refresh_is_idempotent_across_restarts()
    {
        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE));
        var ws = NewWorkspace(store);
        await ws.RefreshAsync(CancellationToken.None);
        var before = RecordIdByName(ws);

        await ws.RefreshAsync(CancellationToken.None);
        await ws.RefreshAsync(CancellationToken.None);
        await ws.RefreshAsync(CancellationToken.None);

        var restarted = NewWorkspace(store, new SequentialIds());
        await restarted.RefreshAsync(CancellationToken.None);

        Assert.Equal(5, store.AdminRows.Count);
        Assert.Equal(1, store.AppendCalls);
        AssertSameMap(before, RecordIdByName(restarted));
    }

    [Fact]
    public async Task UAT04_new_response_adds_exactly_one_row()
    {
        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE));
        var ws = NewWorkspace(store);
        await ws.RefreshAsync(CancellationToken.None);
        var before = RecordIdByName(ws);

        store.Source = Fixtures.Table(Fixtures.AtoE.Append(Fixtures.F));
        var report = await ws.RefreshAsync(CancellationToken.None);

        Assert.Equal(6, ws.Registrations.Count);
        Assert.Equal(6, store.AdminRows.Count);
        Assert.Equal(1, report.Created);
        var after = RecordIdByName(ws);
        foreach (var (key, id) in before)
        {
            Assert.Equal(id, after[key]);
        }

        Assert.Equal("RD26-0006", ws.Registrations.Single(r => r.Raw(LogicalFields.FullName) == "測試己").RegistrationNo);
    }

    [Fact]
    public async Task UAT05_sorting_source_sheet_keeps_links()
    {
        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE.Append(Fixtures.F)));
        var ws = NewWorkspace(store);
        await ws.RefreshAsync(CancellationToken.None);
        var before = RecordIdByName(ws);
        var a = ws.Registrations.Single(r => r.Raw(LogicalFields.Email) == "alpha@example.test" && r.Raw(LogicalFields.FullName) == "測試甲");
        var working = a.Admin.Clone();
        working[AdminColumns.AdminNotes] = "A 的備註";
        await ws.SaveAsync(a, working, CancellationToken.None);

        // 依姓名重新排序（列號全部改變）
        store.Source = Fixtures.Table(Fixtures.AtoE.Append(Fixtures.F).OrderByDescending(x => x.Name, StringComparer.Ordinal));
        await ws.RefreshAsync(CancellationToken.None);

        AssertSameMap(before, RecordIdByName(ws));
        Assert.Equal(6, store.AdminRows.Count);
        var aAfter = ws.Registrations.Single(r => r.RecordId == a.RecordId);
        Assert.Equal("A 的備註", aAfter.Admin[AdminColumns.AdminNotes]);
        Assert.Equal("測試甲", aAfter.Raw(LogicalFields.FullName));
        Assert.Equal(aAfter.Source!.RowNumber.ToString(), store.AdminRows.Single(r => r.RecordId == a.RecordId)[AdminColumns.SourceRowHint]);
    }

    [Fact]
    public async Task UAT06_renamed_header_stops_sync_without_writing()
    {
        var headers = Fixtures.Headers.ToArray();
        headers[5] = "未知標題";
        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE, headers));
        var ws = NewWorkspace(store);

        var ex = await Assert.ThrowsAsync<SchemaHealthException>(() => ws.RefreshAsync(CancellationToken.None));

        Assert.Contains(LogicalFields.Email, ex.Health.MissingRequiredFields);
        Assert.Empty(store.AdminRows);
        Assert.Equal(0, store.AppendCalls);
    }

    [Fact]
    public async Task UAT15_source_key_collision_is_not_auto_linked()
    {
        var twin = Fixtures.A with { Name = "測試甲二", JobTitle = "經理" };
        var store = new FakeStore(Fixtures.Table(new[] { Fixtures.A, twin, Fixtures.B }));
        var ws = NewWorkspace(store);

        await ws.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, store.AdminRows.Count); // 只有 B
        Assert.Equal(2, ws.CollidedSources.Count);
        Assert.Contains(ws.UnlinkedIssues, i => i.Code == IssueCodes.SourceKeyCollision && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public async Task UAT15_collision_after_link_detaches_existing_admin()
    {
        var store = new FakeStore(Fixtures.Table(new[] { Fixtures.A, Fixtures.B }));
        var ws = NewWorkspace(store);
        await ws.RefreshAsync(CancellationToken.None);

        var twin = Fixtures.A with { Name = "測試甲二" };
        store.Source = Fixtures.Table(new[] { Fixtures.A, Fixtures.B, twin });
        await ws.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, store.AdminRows.Count);
        var aAdmin = ws.Registrations.Single(r => r.Admin[AdminColumns.SourceEmailSnapshot] == "alpha@example.test");
        Assert.Null(aAdmin.Source);
        Assert.Contains(aAdmin.Issues, i => i.Code == IssueCodes.SourceKeyCollision);
    }

    [Fact]
    public async Task UAT16_modified_key_field_requires_manual_relink()
    {
        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE));
        var ws = NewWorkspace(store);
        await ws.RefreshAsync(CancellationToken.None);
        var originalA = ws.Registrations.Single(r => r.Raw(LogicalFields.FullName) == "測試甲" && r.Raw(LogicalFields.OrganizationName) == "甲公司");

        var changedA = Fixtures.A with { Email = "alpha.new@example.test" };
        store.Source = Fixtures.Table(new[] { changedA, Fixtures.B, Fixtures.C, Fixtures.D, Fixtures.E });
        await ws.RefreshAsync(CancellationToken.None);

        Assert.Equal(5, store.AdminRows.Count); // 未靜默新增第二筆
        var pending = Assert.Single(ws.PendingRelinks);
        Assert.Contains(pending.Candidates, c => c.RecordId == originalA.RecordId);
        Assert.Contains(ws.UnlinkedIssues, i => i.Code == IssueCodes.PossibleRelink);
        Assert.Contains(ws.Registrations.Single(r => r.RecordId == originalA.RecordId).Issues, i => i.Code == IssueCodes.Orphan);

        await ws.RelinkAsync(pending, pending.Candidates.Single(c => c.RecordId == originalA.RecordId), CancellationToken.None);

        Assert.Equal(5, store.AdminRows.Count);
        Assert.Empty(ws.PendingRelinks);
        var relinked = ws.Registrations.Single(r => r.RecordId == originalA.RecordId);
        Assert.NotNull(relinked.Source);
        Assert.Equal("alpha.new@example.test", relinked.Source!.NormalizedEmail);
        Assert.Contains(store.ChangeLog, c => c.Operation == ChangeOperations.Relink && c.RecordId == originalA.RecordId);
    }

    [Fact]
    public async Task Edited_non_key_answer_marks_source_changed()
    {
        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE));
        var ws = NewWorkspace(store);
        await ws.RefreshAsync(CancellationToken.None);

        store.Source = Fixtures.Table(new[] { Fixtures.A with { JobTitle = "總經理" }, Fixtures.B, Fixtures.C, Fixtures.D, Fixtures.E });
        await ws.RefreshAsync(CancellationToken.None);

        var a = ws.Registrations.Single(r => r.Raw(LogicalFields.JobTitle) == "總經理");
        Assert.True(a.SourceChanged);
        Assert.Contains(a.Issues, i => i.Code == IssueCodes.SourceChanged);
        Assert.Equal("TRUE", store.AdminRows.Single(r => r.RecordId == a.RecordId)[AdminColumns.SourceChanged]);

        await ws.AcceptSourceChangeAsync(a, CancellationToken.None);
        Assert.False(ws.Registrations.Single(r => r.RecordId == a.RecordId).SourceChanged);
    }

    [Fact]
    public async Task UAT19_store_has_no_way_to_write_source_responses()
    {
        var methods = typeof(IRegistrationStore).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain(methods, m => m.Contains("Source", StringComparison.OrdinalIgnoreCase));

        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE));
        var original = store.Source;
        var ws = NewWorkspace(store);
        await ws.RefreshAsync(CancellationToken.None);
        Assert.Same(original, store.Source);
    }
}
