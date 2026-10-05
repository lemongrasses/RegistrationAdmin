using RegistrationAdmin.App.Presentation;
using RegistrationAdmin.App.ViewModels;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.App.Tests;

/// <summary>主視窗與詳細頁的畫面狀態：導航、待處理、未儲存確認、儲存結果。</summary>
public sealed class ScreenStateTests
{
    private static readonly string[] ManagementNames = { "_Config", "_FieldMap", "_Lookups", "_Admin", "_ChangeLog", "row_version", "source_key", "record_id" };

    [Fact]
    public async Task AfterRefresh_HomeIsPendingQueue_WithFourSummaryCards()
    {
        var h = await Harness.CreateAsync();

        Assert.Equal(NavSection.Pending, h.Main.Nav);
        Assert.Equal(AppPage.List, h.Main.CurrentPage);
        Assert.Equal(new[] { "全部報名", "待審核", "待付款", "已確認" }, h.Main.Cards.Select(c => c.Title));
        Assert.Equal("5", h.Main.Cards[0].Value);
        Assert.All(h.Main.Rows, r => Assert.True(WorkQueue.NeedsAttention(r.Registration)));
        Assert.Contains("待處理", h.Main.ResultText);
        Assert.Equal("5", h.Main.PendingBadge);
    }

    [Fact]
    public async Task GeneralMode_ShowsNoInternalSheetOrColumnNames()
    {
        var h = await Harness.CreateAsync();
        var texts = new List<string> { h.Main.EventTitle, h.Main.EventSubtitle, h.Main.ListTitle, h.Main.ListSubtitle, h.Main.ResultText, h.Main.Notice };
        texts.AddRange(h.Main.Cards.SelectMany(c => new[] { c.Title, c.Caption }));
        texts.AddRange(h.Main.Rows.SelectMany(r => r.Problems.Append(r.Status).Append(r.Membership).Append(r.Payment)));
        foreach (var registration in h.Workspace.Registrations)
        {
            var editor = h.Editor(registration);
            texts.AddRange(editor.OpenIssues.SelectMany(i => new[] { i.Title, i.Detail }));
            texts.AddRange(editor.RawGroups.SelectMany(g => g.Fields.Select(f => f.Label)));
            texts.Add(editor.PrimaryAction?.Description ?? "");
        }

        foreach (var text in texts)
        {
            Assert.DoesNotContain(ManagementNames, n => text.Contains(n, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task QuickFilters_SwitchBetweenPendingAndAllList()
    {
        var h = await Harness.CreateAsync();

        h.Main.QuickFilter = QuickFilter.All;
        Assert.Equal(NavSection.All, h.Main.Nav);
        Assert.Equal(5, h.Main.Rows.Count);

        h.Main.QuickFilter = QuickFilter.Duplicates;
        Assert.All(h.Main.Rows, r => Assert.True(WorkQueue.HasOpenDuplicate(r.Registration)));
        Assert.Equal("疑似重複的報名", h.Main.ListTitle);

        h.Main.Nav = NavSection.Pending;
        Assert.Equal(QuickFilter.Pending, h.Main.QuickFilter);
    }

    [Fact]
    public async Task Search_WithNoMatch_ExplainsWhyListIsEmpty()
    {
        var h = await Harness.CreateAsync();
        h.Main.QuickFilter = QuickFilter.All;
        h.Main.Keyword = "完全不存在的人";

        Assert.True(h.Main.IsEmpty);
        Assert.Equal("找不到符合條件的報名", h.Main.EmptyTitle);
    }

    [Fact]
    public async Task OpenRow_ShowsDetail_AndBackReturnsToList()
    {
        var h = await Harness.CreateAsync();
        var row = h.Main.Rows.First();

        h.Main.OpenRowCommand.Execute(row);
        Assert.Equal(AppPage.Detail, h.Main.CurrentPage);
        Assert.Equal(row.RecordId, h.Main.Editor!.Registration.RecordId);

        h.Main.BackToListCommand.Execute(null);
        Assert.Equal(AppPage.List, h.Main.CurrentPage);
        Assert.Null(h.Main.Editor);
    }

    [Fact]
    public async Task LeavingRecordWithUnsavedChanges_AsksFirst()
    {
        var h = await Harness.CreateAsync();
        h.Main.OpenRowCommand.Execute(h.Main.Rows.First());
        h.Main.Editor!.Edit[AdminColumns.AdminNotes] = "電話確認中";
        Assert.True(h.Main.HasUnsavedChanges);

        h.Dialogs.ConfirmResult = false;
        Assert.False(h.Main.Navigate(NavSection.Export));
        Assert.Equal(AppPage.Detail, h.Main.CurrentPage);
        Assert.Equal("電話確認中", h.Main.Editor!.Working[AdminColumns.AdminNotes]);

        h.Dialogs.ConfirmResult = true;
        Assert.True(h.Main.Navigate(NavSection.Export));
        Assert.Equal(AppPage.Export, h.Main.CurrentPage);
        Assert.Contains("要離開這筆報名嗎？", h.Dialogs.Confirms);
    }

    [Fact]
    public async Task ReviewFlow_OpenApproveSave_InThreeMainSteps()
    {
        var h = await Harness.CreateAsync();
        var b = h.Main.Rows.Single(r => r.Name == "測試乙");

        h.Main.OpenRowCommand.Execute(b);                       // 1. 開啟
        var editor = h.Main.Editor!;
        editor.RunPrimaryCommand.Execute(null);                 // 2. 確認會員並開始審核
        Assert.Contains("報名狀態：新報名 → 審核中", editor.PendingChanges);
        Assert.Contains("會員核對：未核對 → 已確認會員", editor.PendingChanges);
        Assert.All(editor.PendingChanges, line => Assert.DoesNotContain("_", line));
        await h.Main.SaveCommand.ExecuteAsync(null);            // 3. 儲存

        Assert.Equal(NoticeKind.Success, h.Main.NoticeKind);
        Assert.StartsWith("已儲存「測試乙」的變更", h.Main.Notice);
        var saved = h.Store.AdminRows.Single(a => a.RecordId == b.RecordId);
        Assert.Equal(RegistrationStatus.UnderReview, saved.RegistrationStatus);
        Assert.False(h.Main.Editor!.IsDirty);
        Assert.Equal("通過並等待付款", h.Main.Editor!.PrimaryAction!.Label);
    }

    [Fact]
    public async Task SaveConflict_KeepsInput_AndShowsFriendlyMessage()
    {
        var h = await Harness.CreateAsync();
        var row = h.Main.Rows.First();
        h.Main.OpenRowCommand.Execute(row);
        h.Main.Editor!.Edit[AdminColumns.AdminNotes] = "我的備註";
        h.Store.TouchFromElsewhere(row.RecordId);

        await h.Main.SaveCommand.ExecuteAsync(null);

        Assert.Equal(NoticeKind.Error, h.Main.NoticeKind);
        Assert.Equal(UserMessages.Conflict, h.Main.Notice);
        Assert.Equal("我的備註", h.Main.Editor!.Working[AdminColumns.AdminNotes]);
        Assert.True(h.Main.Editor.IsDirty);
        Assert.Contains(h.Main.Editor.Messages, m => m.Contains("輸入的內容仍保留"));
    }

    [Fact]
    public async Task NetworkFailure_OnSave_KeepsInput()
    {
        var h = await Harness.CreateAsync();
        h.Main.OpenRowCommand.Execute(h.Main.Rows.First());
        h.Main.Editor!.Edit[AdminColumns.AdminNotes] = "斷線前輸入";
        h.Store.FailNextWrite = true;

        await h.Main.SaveCommand.ExecuteAsync(null);

        Assert.Equal(UserMessages.Network, h.Main.Notice);
        Assert.Equal("斷線前輸入", h.Main.Editor!.Working[AdminColumns.AdminNotes]);
    }

    [Fact]
    public async Task InvalidDate_IsShownNextToField_AndNothingIsWritten()
    {
        var h = await Harness.CreateAsync();
        var row = h.Main.Rows.First();
        h.Main.OpenRowCommand.Execute(row);
        var editor = h.Main.Editor!;
        editor.Edit[AdminColumns.PaidAt] = "下週二";
        var before = h.Store.AdminRows.Single(a => a.RecordId == row.RecordId).RowVersion;

        await h.Main.SaveCommand.ExecuteAsync(null);

        Assert.Contains("付款日期", editor.Errors[AdminColumns.PaidAt]);
        Assert.DoesNotContain("paid_at", editor.Errors[AdminColumns.PaidAt]);
        Assert.True(editor.HasMessages);
        Assert.Equal(NoticeKind.Warning, h.Main.NoticeKind);
        Assert.Equal(before, h.Store.AdminRows.Single(a => a.RecordId == row.RecordId).RowVersion);
    }

    [Fact]
    public async Task RiskyAction_CancelledInDialog_ChangesNothing()
    {
        var h = await Harness.CreateAsync();
        var editor = h.Editor(h.ByName("測試乙", "乙大學"));
        h.Dialogs.ReasonResult = null;

        editor.RunOtherCommand.Execute(editor.OtherActions.Single(a => a.Id == "reject"));

        Assert.False(editor.IsDirty);
        Assert.Equal("新報名", editor.StatusLabel);
    }

    [Fact]
    public async Task RiskyAction_WithReason_IsAppliedButNotSavedYet()
    {
        var h = await Harness.CreateAsync();
        var editor = h.Editor(h.ByName("測試乙", "乙大學"));
        h.Dialogs.ReasonResult = "不符資格";

        editor.RunOtherCommand.Execute(editor.OtherActions.Single(a => a.Id == "reject"));

        Assert.True(editor.IsDirty);
        Assert.Equal("不通過", editor.StatusLabel);
        Assert.Equal("不符資格", editor.Working[AdminColumns.ReviewNotes]);
        Assert.Contains(editor.PendingChanges, l => l.StartsWith("報名狀態：新報名 → 不通過", StringComparison.Ordinal));
        Assert.Equal(RegistrationStatus.Submitted, h.Store.AdminRows.Single(a => a.RecordId == editor.Registration.RecordId).RegistrationStatus);
    }

    [Fact]
    public async Task FailedStep_ShowsReasons_AndKeepsStatus()
    {
        var h = await Harness.CreateAsync();
        var b = h.ByName("測試乙", "乙大學");
        b.Admin[AdminColumns.RegistrationStatus] = RegistrationStatus.UnderReview;
        var editor = h.Editor(b);
        editor.Edit[AdminColumns.EligibilityStatus] = EligibilityStatus.NeedsInformation;

        editor.RunPrimaryCommand.Execute(null);

        Assert.Equal("審核中", editor.StatusLabel);
        Assert.Contains(editor.Messages, m => m.Contains("符合資格"));
        Assert.NotEmpty(editor.Errors[AdminColumns.EligibilityStatus]);
    }

    [Fact]
    public async Task RecordPage_SeparatesReadOnlyAnswers_AndMarksBlanks()
    {
        var noTitle = Fixtures.B with { JobTitle = "" };
        var h = await Harness.CreateAsync(new[] { noTitle });
        var editor = h.Editor(h.Workspace.Registrations.Single());

        var job = editor.RawGroups.SelectMany(g => g.Fields).Single(f => f.Label == "職稱");
        Assert.True(job.IsBlank);
        Assert.Equal("尚未填寫", job.Value);
        Assert.Equal(new[] { "基本資料", "聯絡方式", "發票資訊", "其他資訊" }, editor.RawGroups.Select(g => g.Title));
        Assert.All(editor.Consents, c => Assert.True(c.Checked));
    }

    [Fact]
    public async Task DuplicateRecords_OfferTheOtherRecordToKeep()
    {
        var h = await Harness.CreateAsync();
        var a = h.ByName("測試甲", "甲公司");
        var e = h.ByName("測試戊", "丁公司");
        var editor = h.Editor(a);

        Assert.True(editor.ShowDuplicateSection);
        Assert.Contains(editor.DuplicateCandidates, c => c.Code == e.RecordId && c.Label.Contains(e.RegistrationNo));
    }
}
