using System.Globalization;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Export;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.GoogleSheets.Auth;

/// <summary>
/// 其餘 UAT（04、05、06、09、10、12、14、15、16、17 與 row_version 衝突）。
/// 會暫時修改測試回應頁（排序、改標題、改值、加碰撞列），每一項結束後還原，最後以 UAT-19 逐欄比對確認還原。
/// </summary>
internal sealed class MoreUat(ISheetsServiceProvider auth, GoogleSheetsRegistrationStore store, string spreadsheetId, string? only = null)
{
    private readonly List<(string Id, bool Pass, string Detail)> _results = new();
    private SheetsService _service = null!;
    private SourceTable _source = null!;
    private CancellationToken _ct;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        _ct = ct;
        _service = await auth.GetAsync(ct);
        var ws = await NewWorkspaceAsync();

        await Step("UAT-04", () => NewResponsesAsync(ws));
        var baseline = await store.LoadAsync(ct);
        await Step("UAT-10", () => CapacityAsync(ws));
        await Step("UAT-05", () => SortAsync(ws));
        await Step("UAT-06", () => RenameHeaderAsync(ws));
        await Step("UAT-09", () => ClearConfirmationAsync(ws));
        await Step("UAT-12", () => OverrideAsync(ws));
        await Step("UAT-14", () => NotesExportAsync(ws));
        await Step("UAT-15", () => CollisionAsync(ws));
        await Step("UAT-16", () => RelinkAsync(ws));
        await Step("UAT-17", () => NetworkFailureAsync());
        await Step("CONFLICT", () => ConflictAsync());

        var after = await store.LoadAsync(ct);
        var beforeRows = NonEmpty(baseline.Source);
        var afterRows = NonEmpty(after.Source);
        var same = baseline.Source.Headers.SequenceEqual(after.Source.Headers) && beforeRows.Count == afterRows.Count &&
                   beforeRows.Zip(afterRows).All(p => p.First.SequenceEqual(p.Second));
        Check("UAT-19", same, $"還原後原始回應頁 {afterRows.Count} 列、標題與逐欄內容{(same ? "與測試前一致" : "不一致")}");

        Console.WriteLine($"結果：{_results.Count(r => r.Pass)} PASS／{_results.Count(r => !r.Pass)} FAIL");
        return _results.All(r => r.Pass) ? 0 : 1;
    }

    /// <summary>中斷後的復原：還原 Email 標題、依時間戳排序、清除碰撞測試列。</summary>
    public async Task<int> RestoreAsync(CancellationToken ct)
    {
        _ct = ct;
        _service = await auth.GetAsync(ct);
        await ReloadSourceAsync();
        var email = _source.Headers.Select((h, i) => (h, i)).FirstOrDefault(x => x.h.Contains("Email", StringComparison.Ordinal) || x.h.Contains("電子郵件", StringComparison.Ordinal));
        await WriteRawAsync($"{Col(email.i)}1", new object[] { "Email (用於接收報名確認及活動資訊)" });
        foreach (var row in _source.Rows.Where(r => r.Formatted.Contains("碰撞測試單位")))
        {
            await Quota(() => _service.Spreadsheets.Values.Clear(new ClearValuesRequest(), spreadsheetId, $"'{_source.SheetTitle}'!A{row.RowNumber}:{Col(_source.Headers.Count - 1)}{row.RowNumber}").ExecuteAsync(ct));
        }

        await ReloadSourceAsync();
        await SortSourceAsync(0, descending: false);
        await ReloadSourceAsync();
        Console.WriteLine("標題：" + string.Join("｜", _source.Headers));
        Console.WriteLine("前 3 列姓名：" + string.Join("、", _source.Rows.Take(3).Select(r => r.Formatted[2])) + $"；共 {_source.Rows.Count} 列");
        return 0;
    }

    /// <summary>診斷：列出未連結的管理列、待人工連結與碰撞。</summary>
    public async Task<int> DiagnoseAsync(CancellationToken ct)
    {
        _ct = ct;
        var ws = await NewWorkspaceAsync();
        foreach (var r in ws.Registrations.Where(r => r.Source is null))
        {
            Console.WriteLine($"未連結：{r.RegistrationNo} {r.RecordId} status={r.Admin.RegistrationStatus} row_hint={r.Admin[AdminColumns.SourceRowHint]} email={r.Admin[AdminColumns.SourceEmailSnapshot]}");
        }

        foreach (var p in ws.PendingRelinks)
        {
            Console.WriteLine($"待連結：來源第 {p.Source.RowNumber} 列 {p.Source.Get(LogicalFields.FullName)} 候選 {string.Join(",", p.Candidates.Select(c => c.RegistrationNo))}");
        }

        foreach (var s in ws.CollidedSources)
        {
            Console.WriteLine($"碰撞：來源第 {s.RowNumber} 列 {s.Get(LogicalFields.FullName)}");
        }

        var row = _source.Rows.FirstOrDefault(r => r.Formatted.ElementAtOrDefault(2) == "名額04");
        Console.WriteLine(row is null ? "來源沒有名額04" : $"來源名額04：第 {row.RowNumber} 列 " + string.Join("｜", row.Formatted));
        return 0;
    }

    // ── UAT-04：新增回應只建立一次 ──
    private async Task NewResponsesAsync(RegistrationWorkspace ws)
    {
        var before = ws.Registrations.ToDictionary(r => r.RecordId, r => r.Admin.RegistrationStatus);
        var names = _source.Rows.Select(r => r.Formatted.ElementAtOrDefault(2) ?? "").ToHashSet();
        var add = Seed.Pool(6, 21).Where(p => !names.Contains(p.Name)).ToList();
        if (add.Count > 0)
        {
            await WriteAnswersAsync(NextRow(), add);
        }

        var first = await ws.RefreshAsync(_ct);
        var second = await ws.RefreshAsync(_ct);
        var unchanged = before.All(kv => ws.Registrations.Any(r => r.RecordId == kv.Key && r.Admin.RegistrationStatus == kv.Value));
        Check("UAT-04", first.Created == add.Count && second.Created == 0 && unchanged,
            $"新增 {add.Count} 筆回應 → 建立 {first.Created} 筆管理列，再次同步建立 {second.Created} 筆；原有 {before.Count} 筆 ID 與狀態{(unchanged ? "不變" : "改變")}");
        await ReloadSourceAsync();
    }

    // ── UAT-10：20 人名額與候補 ──
    private async Task CapacityAsync(RegistrationWorkspace ws)
    {
        var pool = ws.Registrations.Where(r => r.Raw(LogicalFields.FullName).StartsWith("名額", StringComparison.Ordinal))
            .OrderBy(r => r.RegistrationNo, StringComparer.Ordinal).ToList();
        var capacity = ws.Dashboard().Capacity;
        var queue = new Queue<Registration>(pool.Where(r => r.Admin.RegistrationStatus is RegistrationStatus.Submitted or RegistrationStatus.UnderReview));
        while (ws.Dashboard().Capacity.Occupied < capacity.Capacity && queue.Count > 0)
        {
            var r = queue.Dequeue();
            await ToUnderReviewAsync(ws, r, MembershipStatus.Verified, EligibilityStatus.Eligible);
            var working = r.Admin.Clone();
            var messages = ws.TryApplyTransition(r, working, RegistrationStatus.ApprovedPendingPayment);
            if (messages.Count > 0)
            {
                throw new InvalidOperationException($"{r.Raw(LogicalFields.FullName)} 無法通過：" + string.Join("；", messages.Select(m => m.Message)));
            }

            await ws.SaveAsync(r, working, _ct);
        }

        var full = ws.Dashboard().Capacity;
        var blockedOk = true;
        var positions = new List<string>();
        foreach (var r in queue.Take(2))
        {
            await ToUnderReviewAsync(ws, r, MembershipStatus.Verified, EligibilityStatus.Eligible);
            blockedOk &= ws.ValidateTransition(r, r.Admin.Clone(), RegistrationStatus.ApprovedPendingPayment).Any(m => m.Field == "capacity");
            var wait = r.Admin.Clone();
            if (ws.TryApplyTransition(r, wait, RegistrationStatus.Waitlisted).Count == 0)
            {
                await ws.SaveAsync(r, wait, _ct);
            }

            positions.Add($"{r.Raw(LogicalFields.FullName)}→候補 {r.Admin[AdminColumns.WaitlistPosition]}");
        }

        // 重跑時名額池已在上一輪轉為候補：改以既有候補驗證「滿額不可占位」與候補順位。
        foreach (var r in pool.Where(r => r.Admin.RegistrationStatus == RegistrationStatus.Waitlisted).Take(2 - positions.Count))
        {
            blockedOk &= ws.ValidateTransition(r, r.Admin.Clone(), RegistrationStatus.ApprovedPendingPayment).Any(m => m.Field == "capacity");
            positions.Add($"{r.Raw(LogicalFields.FullName)}→候補 {r.Admin[AdminColumns.WaitlistPosition]}（上一輪）");
        }

        Check("UAT-10", full.Occupied == full.Capacity && full.Available == 0 && blockedOk && positions.Count == 2,
            $"占用 {full.Occupied}／{full.Capacity}（已確認 {full.Confirmed}、待付款 {full.ApprovedPendingPayment}）；滿額後不可占位：{(blockedOk ? "是" : "否")}；{string.Join("、", positions)}");
    }

    // ── UAT-05：來源排序後仍連到正確申請 ──
    private async Task SortAsync(RegistrationWorkspace ws)
    {
        var before = ws.Registrations.Where(r => r.Source is not null)
            .ToDictionary(r => r.RecordId, r => (Name: r.Raw(LogicalFields.Email) + r.Raw(LogicalFields.Phone), Row: r.Source!.RowNumber, Key: r.Admin[AdminColumns.SourceKey]));
        await SortSourceAsync(2, descending: true);
        try
        {
            await ws.RefreshAsync(_ct);
            var linked = ws.Registrations.Where(r => r.Source is not null).ToList();
            var sameLink = linked.Count == before.Count && linked.All(r =>
                before.TryGetValue(r.RecordId, out var b) && b.Name == r.Raw(LogicalFields.Email) + r.Raw(LogicalFields.Phone) && b.Key == r.Admin[AdminColumns.SourceKey]);
            var moved = linked.Count(r => before[r.RecordId].Row != r.Source!.RowNumber);
            Check("UAT-05", sameLink && moved > 0, $"依姓名反向排序後 {moved} 筆換列；{linked.Count} 筆 record_id／source_key 仍連到同一申請：{(sameLink ? "是" : "否")}");
        }
        finally
        {
            await SortSourceAsync(0, descending: false);
            await ws.RefreshAsync(_ct);
        }
    }

    // ── UAT-06：必要欄位改名時停止同步 ──
    private async Task RenameHeaderAsync(RegistrationWorkspace ws)
    {
        var column = HeaderIndex("Email");
        var original = _source.Headers[column];
        var adminBefore = (await store.LoadAsync(_ct)).AdminRows.Count;
        await WriteRawAsync($"{Col(column)}1", new object[] { "電子郵件（測試改名）" });
        try
        {
            string detail;
            var stopped = false;
            try
            {
                await ws.RefreshAsync(_ct);
                detail = "未停止同步";
            }
            catch (SchemaHealthException ex)
            {
                stopped = ex.Health.MissingRequiredFields.Contains(LogicalFields.Email);
                detail = ex.Message.Split('\n')[0..2].Aggregate((a, b) => a + " " + b);
            }

            var adminAfter = (await store.LoadAsync(_ct)).AdminRows.Count;
            Check("UAT-06", stopped && adminAfter == adminBefore, $"{detail.Trim()}；_Admin {adminBefore}→{adminAfter} 列");
        }
        finally
        {
            await WriteRawAsync($"{Col(column)}1", new object[] { original });
            await ws.RefreshAsync(_ct);
        }
    }

    // ── UAT-09：缺少確認項目時不能通過 ──
    private async Task ClearConfirmationAsync(RegistrationWorkspace ws)
    {
        var target = ws.Registrations.Single(r => r.Raw(LogicalFields.FullName) == "名額21");
        var column = HeaderIndex("送出前確認");
        var row = target.Source!.RowNumber;
        var original = _source.Rows.Single(r => r.RowNumber == row).Formatted[column];
        await WriteRawAsync($"{Col(column)}{row}", new object[] { "" });
        try
        {
            await ws.RefreshAsync(_ct);
            var again = ws.Registrations.Single(r => r.RecordId == target.RecordId);
            var messages = ws.ValidateTransition(again, again.Admin.Clone(), RegistrationStatus.ApprovedPendingPayment);
            Check("UAT-09", messages.Any(m => m.Field == LogicalFields.AccuracyConfirmation),
                "清空「送出前確認」→ " + string.Join("；", messages.Select(m => m.Message)));
        }
        finally
        {
            await WriteRawAsync($"{Col(column)}{row}", new object[] { original });
            await ws.RefreshAsync(_ct);
            var restored = ws.Registrations.Single(r => r.RecordId == target.RecordId);
            if (restored.SourceChanged)
            {
                await ws.AcceptSourceChangeAsync(restored, _ct);
            }
        }
    }

    // ── UAT-12：override 發票抬頭，原始值不變 ──
    private async Task OverrideAsync(RegistrationWorkspace ws)
    {
        var a = ws.Registrations.Single(r => r.Raw(LogicalFields.FullName) == "測試甲" && r.Raw(LogicalFields.OrganizationName) == "甲公司");
        var working = a.Admin.Clone();
        working[AdminColumns.OverrideInvoiceTitle] = "甲公司（UAT 修正抬頭）";
        await ws.SaveAsync(a, working, _ct);
        var reread = await NewWorkspaceAsync();
        var again = reread.Registrations.Single(r => r.RecordId == a.RecordId);
        Check("UAT-12", again.Effective(LogicalFields.InvoiceTitle) == "甲公司（UAT 修正抬頭）" && again.Raw(LogicalFields.InvoiceTitle) == "甲公司" &&
                        again.Raw(LogicalFields.TaxId) == "01234567",
            $"有效抬頭「{again.Effective(LogicalFields.InvoiceTitle)}」、原始抬頭「{again.Raw(LogicalFields.InvoiceTitle)}」、統編「{again.Raw(LogicalFields.TaxId)}」");
    }

    // ── UAT-14：申請人備註與得知管道 ──
    private Task NotesExportAsync(RegistrationWorkspace ws)
    {
        var all = Texts(ExportTemplates.AllRegistrations, ws, null);
        var review = Texts(ExportTemplates.PendingReview, ws, false);
        const string notes = "不吃牛肉，其餘皆可";
        Check("UAT-14", all.Contains(notes) && all.Any(t => t.Contains("同事/朋友推薦", StringComparison.Ordinal)) && !review.Contains(notes),
            $"完整匯出含備註：{all.Contains(notes)}、含得知管道：{all.Any(t => t.Contains("同事/朋友推薦", StringComparison.Ordinal))}；待審核名單不帶備註：{!review.Contains(notes)}");
        return Task.CompletedTask;
    }

    // ── UAT-15：source_key 碰撞 ──
    private async Task CollisionAsync(RegistrationWorkspace ws)
    {
        var victim = ws.Registrations.Single(r => r.Raw(LogicalFields.FullName) == "名額05");
        var sourceRow = _source.Rows.Single(r => r.RowNumber == victim.Source!.RowNumber);
        var adminBefore = (await store.LoadAsync(_ct)).AdminRows.Count;
        var row = NextRow();
        var values = new List<object> { sourceRow.Unformatted[0]! };
        values.AddRange(sourceRow.Formatted.Skip(1).Select((v, i) => (object)(i + 1 == HeaderIndex("服務/所屬單位") ? "碰撞測試單位" : v)));
        await WriteRawAsync($"A{row}", values.ToArray());
        try
        {
            var report = await ws.RefreshAsync(_ct);
            var adminAfter = (await store.LoadAsync(_ct)).AdminRows.Count;
            var error = ws.AllIssues.Any(i => i.Code == IssueCodes.SourceKeyCollision && i.Severity == IssueSeverity.Error);
            Check("UAT-15", report.Collisions >= 1 && error && adminAfter == adminBefore,
                $"同時間戳／Email／電話、其他答案不同 → 碰撞 {report.Collisions} 列、error：{error}；_Admin {adminBefore}→{adminAfter} 列");
        }
        finally
        {
            await Quota(() => _service.Spreadsheets.Values.Clear(new ClearValuesRequest(), spreadsheetId, $"'{_source.SheetTitle}'!A{row}:{Col(_source.Headers.Count - 1)}{row}").ExecuteAsync(_ct));
            await ws.RefreshAsync(_ct);
            await ReloadSourceAsync();
        }
    }

    // ── UAT-16：關鍵來源欄被修改 → 人工重新連結 ──
    private async Task RelinkAsync(RegistrationWorkspace ws)
    {
        // 以管理列的 Email 快照找目標（中斷過的測試可能讓它暫時未連結），並先確保電話為原值且已連結。
        var target = ws.Registrations.Single(r => r.Admin[AdminColumns.SourceEmailSnapshot] == "p04@example.test");
        var column = HeaderIndex("聯絡電話");
        var row = _source.Rows.Single(r => r.Formatted.ElementAtOrDefault(2) == "名額04").RowNumber;
        const string original = "0922000004";
        if (_source.Rows.Single(r => r.RowNumber == row).Formatted[column] != original)
        {
            await WriteRawAsync($"{Col(column)}{row}", new object[] { original });
            await ws.RefreshAsync(_ct);
        }

        await RelinkOnceAsync(ws, target.RecordId);
        await ReloadSourceAsync();
        await WriteRawAsync($"{Col(column)}{row}", new object[] { "0922009999" });
        string detail;
        bool pass;
        try
        {
            var report = await ws.RefreshAsync(_ct);
            var relinked = await RelinkOnceAsync(ws, target.RecordId);
            pass = report.Created == 0 && report.PendingRelinks >= 1 && relinked;
            detail = $"改電話後：待人工連結 {report.PendingRelinks}、新建立 {report.Created}；人工重新連結回原 record_id：{relinked}";
        }
        finally
        {
            await WriteRawAsync($"{Col(column)}{row}", new object[] { original });
            await ws.RefreshAsync(_ct);
        }

        var restored = await RelinkOnceAsync(ws, target.RecordId);
        Check("UAT-16", pass && restored, detail + $"；還原電話後再連結：{restored}");
    }

    // ── UAT-17：網路中斷時儲存失敗、輸入保留、重試只寫一次 ──
    private async Task NetworkFailureAsync()
    {
        var flaky = new FlakyStore(store);
        var ws = new RegistrationWorkspace(flaky, new TaipeiClock(), new GuidIdGenerator(), "uat");
        await ws.RefreshAsync(_ct);
        var b = ws.Registrations.Single(r => r.Raw(LogicalFields.FullName) == "測試乙");
        var working = b.Admin.Clone();
        var note = "UAT-17 網路中斷測試 " + DateTime.Now.ToString("HHmmss");
        working[AdminColumns.AdminNotes] = note;
        flaky.FailNextWrite = true;
        var failed = false;
        try
        {
            await ws.SaveAsync(b, working, _ct);
        }
        catch (Exception)
        {
            failed = true;
        }

        var kept = working[AdminColumns.AdminNotes] == note && b.Admin[AdminColumns.AdminNotes] != note;
        var adminBefore = (await store.LoadAsync(_ct)).AdminRows.Count;
        await ws.SaveAsync(b, working, _ct);
        var cloud = await store.LoadAsync(_ct);
        var saved = cloud.AdminRows.Single(r => r.RecordId == b.RecordId)[AdminColumns.AdminNotes] == note;
        Check("UAT-17", failed && kept && saved && cloud.AdminRows.Count == adminBefore,
            $"第一次儲存失敗：{failed}、輸入保留：{kept}；重試後寫入：{saved}、_Admin 列數不變：{cloud.AdminRows.Count == adminBefore}");
    }

    // ── 單一寫入者：兩個工作階段同時修改 → 第二個出現衝突 ──
    private async Task ConflictAsync()
    {
        var first = await NewWorkspaceAsync();
        var second = await NewWorkspaceAsync();
        var r1 = first.Registrations.Single(r => r.Raw(LogicalFields.FullName) == "測試己");
        var r2 = second.Registrations.Single(r => r.RecordId == r1.RecordId);
        var w1 = r1.Admin.Clone();
        // 每輪用不同文字：若與上一輪留下的值相同，第一台不會寫入，第二台自然不會衝突。
        var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        w1[AdminColumns.AdminNotes] = $"電腦 1 的修改 {stamp}";
        await first.SaveAsync(r1, w1, _ct);
        var w2 = r2.Admin.Clone();
        w2[AdminColumns.AdminNotes] = $"電腦 2 的修改 {stamp}";
        string detail;
        var conflict = false;
        try
        {
            await second.SaveAsync(r2, w2, _ct);
            detail = "第二台未被阻擋";
        }
        catch (ConcurrencyConflictException ex)
        {
            conflict = true;
            detail = ex.Message;
        }

        var cloud = (await store.LoadAsync(_ct)).AdminRows.Single(r => r.RecordId == r1.RecordId)[AdminColumns.AdminNotes];
        Check("UAT-20 衝突", conflict && cloud == $"電腦 1 的修改 {stamp}", $"{detail}；雲端保留「{cloud}」");
    }

    // ── 工具 ──
    private async Task<bool> RelinkOnceAsync(RegistrationWorkspace ws, string recordId)
    {
        var pending = ws.PendingRelinks.FirstOrDefault(p => p.Candidates.Any(c => c.RecordId == recordId));
        if (pending is null)
        {
            return ws.Registrations.Any(r => r.RecordId == recordId && r.Source is not null);
        }

        await ws.RelinkAsync(pending, pending.Candidates.First(c => c.RecordId == recordId), _ct);
        return ws.Registrations.Any(r => r.RecordId == recordId && r.Source is not null);
    }

    private static async Task ToUnderReviewAsync(RegistrationWorkspace ws, Registration r, string membership, string eligibility)
    {
        var working = r.Admin.Clone();
        if (working.RegistrationStatus == RegistrationStatus.Submitted)
        {
            ws.TryApplyTransition(r, working, RegistrationStatus.UnderReview);
        }

        working[AdminColumns.MembershipStatus] = membership;
        working[AdminColumns.EligibilityStatus] = eligibility;
        if (working.DiffColumns(r.Admin).Count > 0)
        {
            await ws.SaveAsync(r, working, CancellationToken.None);
        }
    }

    private static List<string> Texts(ExportTemplate template, RegistrationWorkspace ws, bool? notes) =>
        ExportTableBuilder.Build(new ExportRequest
            {
                Template = template,
                Registrations = ws.Registrations.ToList(),
                Lookups = ws.Lookups,
                IncludeApplicantNotes = notes,
                GeneratedAt = DateTimeOffset.Now,
            })
            .Rows.SelectMany(r => r).ToList();

    private async Task<RegistrationWorkspace> NewWorkspaceAsync()
    {
        var ws = new RegistrationWorkspace(store, new TaipeiClock(), new GuidIdGenerator(), "uat");
        await ws.RefreshAsync(_ct);
        await ReloadSourceAsync();
        return ws;
    }

    private async Task ReloadSourceAsync() => _source = (await store.LoadAsync(_ct)).Source;

    private int NextRow() => (_source.Rows.Count == 0 ? 1 : _source.Rows.Max(r => r.RowNumber)) + 1;

    private int HeaderIndex(string prefix) =>
        _source.Headers.Select((h, i) => (h, i)).First(x => x.h.Trim().StartsWith(prefix, StringComparison.Ordinal)).i;

    private static string Col(int zeroBased)
    {
        var index = zeroBased + 1;
        var name = "";
        while (index > 0)
        {
            var m = (index - 1) % 26;
            name = (char)('A' + m) + name;
            index = (index - m - 1) / 26;
        }

        return name;
    }

    private async Task WriteRawAsync(string start, object[] values)
    {
        var request = _service.Spreadsheets.Values.Update(
            new ValueRange { Values = new List<IList<object>> { values.ToList() } }, spreadsheetId, $"'{_source.SheetTitle}'!{start}");
        request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await Quota(() => request.ExecuteAsync(_ct));
    }

    private async Task WriteAnswersAsync(int firstRow, IReadOnlyList<Seed> answers)
    {
        var lastRow = firstRow + answers.Count - 1;
        var stamps = answers.Select(a => (IList<object>)new List<object> { a.Timestamp }).ToList();
        var stampRequest = _service.Spreadsheets.Values.Update(new ValueRange { Values = stamps }, spreadsheetId, $"'{_source.SheetTitle}'!A{firstRow}:A{lastRow}");
        stampRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
        await Quota(() => stampRequest.ExecuteAsync(_ct));
        var values = answers.Select(a => (IList<object>)_source.Headers.Skip(1).Select(h => (object)a.ValueFor(h)).ToList()).ToList();
        var valueRequest = _service.Spreadsheets.Values.Update(new ValueRange { Values = values }, spreadsheetId,
            $"'{_source.SheetTitle}'!B{firstRow}:{Col(_source.Headers.Count - 1)}{lastRow}");
        valueRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await Quota(() => valueRequest.ExecuteAsync(_ct));
    }

    private async Task SortSourceAsync(int column, bool descending)
    {
        var request = new BatchUpdateSpreadsheetRequest
        {
            Requests = new List<Request>
            {
                new()
                {
                    SortRange = new SortRangeRequest
                    {
                        Range = new GridRange
                        {
                            SheetId = _source.SheetId, StartRowIndex = 1, EndRowIndex = NextRow() - 1,
                            StartColumnIndex = 0, EndColumnIndex = _source.Headers.Count,
                        },
                        SortSpecs = new List<SortSpec> { new() { DimensionIndex = column, SortOrder = descending ? "DESCENDING" : "ASCENDING" } },
                    },
                },
            },
        };
        await Quota(() => _service.Spreadsheets.BatchUpdate(request, spreadsheetId).ExecuteAsync(_ct));
    }

    /// <summary>測試工具自己的直接寫入也要遵守每分鐘寫入配額。</summary>
    private static async Task<T> Quota<T>(Func<Task<T>> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Google.GoogleApiException ex) when ((int)ex.HttpStatusCode == 429 && attempt < 8)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static List<IReadOnlyList<string>> NonEmpty(SourceTable table) =>
        table.Rows.Where(r => r.Formatted.Any(v => v.Length > 0)).Select(r => (IReadOnlyList<string>)r.Formatted.ToList()).ToList();

    private async Task Step(string id, Func<Task> action)
    {
        if (only is not null && !only.Split(',').Contains(id))
        {
            return;
        }

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Check(id, false, $"例外 {ex.GetType().Name}：{ex.Message}");
        }
    }

    private void Check(string id, bool pass, string detail)
    {
        _results.Add((id, pass, detail));
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {id}：{detail}");
    }

    /// <summary>模擬網路中斷：下一次寫入丟出 HttpRequestException。</summary>
    private sealed class FlakyStore(IRegistrationStore inner) : IRegistrationStore
    {
        public bool FailNextWrite { get; set; }

        public Task<WorkbookSnapshot> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);

        public Task AppendAdminRowsAsync(IReadOnlyList<AdminRecord> rows, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            return inner.AppendAdminRowsAsync(rows, changeLog, cancellationToken);
        }

        public Task UpdateAdminRowsAsync(IReadOnlyList<AdminRowUpdate> updates, IReadOnlyList<ChangeLogEntry> changeLog, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            return inner.UpdateAdminRowsAsync(updates, changeLog, cancellationToken);
        }

        private void ThrowIfFailing()
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new HttpRequestException("模擬網路中斷");
            }
        }
    }
}
