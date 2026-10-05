using System.Diagnostics;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Search;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.Tests;

public class PerformanceAndHistoryTests
{
    /// <summary>規劃 12：2,000 筆回應內，載入後搜尋／篩選通常於 300 毫秒內更新。</summary>
    [Fact]
    public async Task Search_and_filter_on_2000_rows_stays_under_300ms()
    {
        var store = new FakeStore(Fixtures.Table(Fixtures.Pool(2000)));
        var ws = new RegistrationWorkspace(store, new FixedClock(), new SequentialIds(), "test");
        await ws.RefreshAsync(CancellationToken.None);
        Assert.Equal(2000, ws.Registrations.Count);

        var queries = new[]
        {
            new RegistrationQuery { Keyword = "名額1999" },
            new RegistrationQuery { Keyword = "p42@example.test" },
            new RegistrationQuery { Keyword = "0922000077" },
            new RegistrationQuery { MealCode = MealCode.Meat },
            new RegistrationQuery { Keyword = "名額單位", MealCode = MealCode.Meat },
        };

        RegistrationFilter.Apply(ws.Registrations, queries[0]); // 暖機（JIT）
        foreach (var query in queries)
        {
            var watch = Stopwatch.StartNew();
            var result = RegistrationFilter.Apply(ws.Registrations, query);
            watch.Stop();
            Assert.NotEmpty(result);
            Assert.True(watch.ElapsedMilliseconds < 300, $"篩選耗時 {watch.ElapsedMilliseconds} ms（{query.Keyword}）");
        }
    }

    [Fact]
    public async Task History_is_empty_when_store_cannot_read_changelog()
    {
        var store = new FakeStore(Fixtures.Table(Fixtures.AtoE));
        var ws = new RegistrationWorkspace(store, new FixedClock(), new SequentialIds(), "test");
        await ws.RefreshAsync(CancellationToken.None);

        Assert.Empty(await ws.LoadHistoryAsync(ws.Registrations[0].RecordId, CancellationToken.None));
    }

    [Fact]
    public async Task History_returns_only_this_record_newest_first()
    {
        var store = new HistoryStore(Fixtures.Table(Fixtures.AtoE));
        var ws = new RegistrationWorkspace(store, new FixedClock(), new SequentialIds(), "test");
        await ws.RefreshAsync(CancellationToken.None);
        var a = ws.Registrations[0];
        var working = a.Admin.Clone();
        Assert.Empty(ws.TryApplyTransition(a, working, RegistrationStatus.UnderReview));
        await ws.SaveAsync(a, working, CancellationToken.None);

        var history = await ws.LoadHistoryAsync(a.RecordId, CancellationToken.None);

        Assert.NotEmpty(history);
        Assert.All(history, h => Assert.Equal(a.RecordId, h.RecordId));
        Assert.Equal(history.OrderByDescending(h => h.ChangedAt, StringComparer.Ordinal), history);
        Assert.Contains(history, h => h.Operation == ChangeOperations.StatusChange);
    }

    private sealed class HistoryStore : IRegistrationStore, IChangeLogReader
    {
        private readonly FakeStore _inner;

        public HistoryStore(SourceTable source) => _inner = new FakeStore(source);

        public Task<WorkbookSnapshot> LoadAsync(CancellationToken cancellationToken) => _inner.LoadAsync(cancellationToken);

        public Task AppendAdminRowsAsync(IReadOnlyList<AdminRecord> rows, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken) =>
            _inner.AppendAdminRowsAsync(rows, changeLog, cancellationToken);

        public Task UpdateAdminRowsAsync(IReadOnlyList<AdminRowUpdate> updates, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken) =>
            _inner.UpdateAdminRowsAsync(updates, changeLog, cancellationToken);

        public Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(string recordId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChangeLogEntry>>(_inner.ChangeLog.Where(c => c.RecordId == recordId).ToList());
    }
}
