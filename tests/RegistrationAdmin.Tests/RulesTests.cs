using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Search;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.Tests;

public class RulesTests
{
    private static async Task<(RegistrationWorkspace Ws, FakeStore Store)> LoadAsync(IEnumerable<Fixtures.Answer> answers)
    {
        var store = new FakeStore(Fixtures.Table(answers));
        var ws = new RegistrationWorkspace(store, new FixedClock(), new SequentialIds(), "test");
        await ws.RefreshAsync(CancellationToken.None);
        return (ws, store);
    }

    private static Registration ByName(RegistrationWorkspace ws, string name, string org) =>
        ws.Registrations.Single(r => r.Raw(LogicalFields.FullName) == name && r.Raw(LogicalFields.OrganizationName) == org);

    /// <summary>把一筆推進到「審核中」並設定會員／資格，回傳已儲存的報名。</summary>
    private static async Task<Registration> PrepareAsync(RegistrationWorkspace ws, Registration r, string membership, string eligibility)
    {
        var working = r.Admin.Clone();
        Assert.Empty(ws.TryApplyTransition(r, working, RegistrationStatus.UnderReview));
        working[AdminColumns.MembershipStatus] = membership;
        working[AdminColumns.EligibilityStatus] = eligibility;
        await ws.SaveAsync(r, working, CancellationToken.None);
        return r;
    }

    [Fact]
    public async Task UAT08_membership_and_eligibility_gate_approval()
    {
        var (ws, _) = await LoadAsync(Fixtures.AtoE);
        var a = await PrepareAsync(ws, ByName(ws, "測試甲", "甲公司"), MembershipStatus.Verified, EligibilityStatus.Eligible);
        var b = await PrepareAsync(ws, ByName(ws, "測試乙", "乙大學"), MembershipStatus.Unchecked, EligibilityStatus.NeedsInformation);

        var aWorking = a.Admin.Clone();
        Assert.Empty(ws.TryApplyTransition(a, aWorking, RegistrationStatus.ApprovedPendingPayment));
        Assert.Equal(RegistrationStatus.ApprovedPendingPayment, aWorking.RegistrationStatus);
        Assert.Equal(PaymentStatus.Pending, aWorking.PaymentStatus);

        var bMessages = ws.ValidateTransition(b, b.Admin.Clone(), RegistrationStatus.ApprovedPendingPayment);
        Assert.Contains(bMessages, m => m.Field == AdminColumns.MembershipStatus);
        Assert.Contains(bMessages, m => m.Field == AdminColumns.EligibilityStatus);
    }

    [Theory]
    [InlineData(LogicalFields.DataUseConsent)]
    [InlineData(LogicalFields.FullAttendanceConsent)]
    [InlineData(LogicalFields.FeeAcknowledged)]
    [InlineData(LogicalFields.AccuracyConfirmation)]
    public async Task UAT09_each_missing_confirmation_blocks_approval(string missing)
    {
        var answer = missing switch
        {
            LogicalFields.DataUseConsent => Fixtures.A with { Consents = Fixtures.ConsentAttendance },
            LogicalFields.FullAttendanceConsent => Fixtures.A with { Consents = Fixtures.ConsentData },
            LogicalFields.FeeAcknowledged => Fixtures.A with { FeeText = "" },
            _ => Fixtures.A with { AccuracyText = "" },
        };
        var (ws, _) = await LoadAsync(new[] { answer });
        var r = await PrepareAsync(ws, ws.Registrations.Single(), MembershipStatus.Verified, EligibilityStatus.Eligible);

        var messages = ws.ValidateTransition(r, r.Admin.Clone(), RegistrationStatus.ApprovedPendingPayment);

        var only = Assert.Single(messages);
        Assert.Equal(missing, only.Field);
    }

    [Fact]
    public async Task UAT10_capacity_of_twenty_is_enforced()
    {
        var (ws, _) = await LoadAsync(Fixtures.Pool(21));
        var ordered = ws.Registrations.OrderBy(r => r.RegistrationNo, StringComparer.Ordinal).ToList();

        foreach (var r in ordered.Take(20))
        {
            await PrepareAsync(ws, r, MembershipStatus.Verified, EligibilityStatus.Eligible);
            var working = r.Admin.Clone();
            Assert.Empty(ws.TryApplyTransition(r, working, RegistrationStatus.ApprovedPendingPayment));
            await ws.SaveAsync(r, working, CancellationToken.None);
        }

        var capacity = ws.Dashboard().Capacity;
        Assert.Equal(20, capacity.Occupied);
        Assert.Equal(0, capacity.Available);

        var p21 = await PrepareAsync(ws, ordered[20], MembershipStatus.Verified, EligibilityStatus.Eligible);
        var blocked = ws.ValidateTransition(p21, p21.Admin.Clone(), RegistrationStatus.ApprovedPendingPayment);
        Assert.Contains(blocked, m => m.Field == "capacity");

        var waitlist = p21.Admin.Clone();
        Assert.Empty(ws.TryApplyTransition(p21, waitlist, RegistrationStatus.Waitlisted));
        Assert.Equal("1", waitlist[AdminColumns.WaitlistPosition]);
    }

    [Fact]
    public async Task UAT11_confirmation_requires_verified_payment()
    {
        var (ws, store) = await LoadAsync(Fixtures.AtoE);
        var a = await PrepareAsync(ws, ByName(ws, "測試甲", "甲公司"), MembershipStatus.Verified, EligibilityStatus.Eligible);
        var working = a.Admin.Clone();
        Assert.Empty(ws.TryApplyTransition(a, working, RegistrationStatus.ApprovedPendingPayment));
        await ws.SaveAsync(a, working, CancellationToken.None);

        var first = ws.ValidateTransition(a, a.Admin.Clone(), RegistrationStatus.Confirmed);
        Assert.Contains(first, m => m.Field == AdminColumns.PaymentStatus);

        var paid = a.Admin.Clone();
        paid[AdminColumns.PaymentStatus] = PaymentStatus.Verified;
        var missingDates = ws.ValidateSave(a, paid);
        Assert.Contains(missingDates, m => m.Field == AdminColumns.PaidAt);
        Assert.Contains(missingDates, m => m.Field == AdminColumns.VerifiedAt);

        paid[AdminColumns.PaidAt] = "2026-10-05";
        paid[AdminColumns.VerifiedAt] = "2026-10-06";
        Assert.Empty(ws.TryApplyTransition(a, paid, RegistrationStatus.Confirmed));
        await ws.SaveAsync(a, paid, CancellationToken.None);

        Assert.Equal(RegistrationStatus.Confirmed, store.AdminRows.Single(x => x.RecordId == a.RecordId).RegistrationStatus);
        Assert.Contains(store.ChangeLog, c => c.RecordId == a.RecordId && c.Operation == ChangeOperations.StatusChange);
    }

    [Fact]
    public async Task Rejection_and_cancellation_require_reason()
    {
        var (ws, _) = await LoadAsync(Fixtures.AtoE);
        var b = await PrepareAsync(ws, ByName(ws, "測試乙", "乙大學"), MembershipStatus.NotMember, EligibilityStatus.Ineligible);

        var working = b.Admin.Clone();
        Assert.Contains(ws.ValidateTransition(b, working, RegistrationStatus.Rejected), m => m.Field == AdminColumns.ReviewNotes);
        working[AdminColumns.ReviewNotes] = "非會員";
        Assert.Empty(ws.TryApplyTransition(b, working, RegistrationStatus.Rejected));
    }

    [Fact]
    public async Task Disallowed_transition_is_reported()
    {
        var (ws, _) = await LoadAsync(Fixtures.AtoE);
        var a = ByName(ws, "測試甲", "甲公司");
        var messages = ws.ValidateTransition(a, a.Admin.Clone(), RegistrationStatus.Confirmed);
        Assert.Contains(messages, m => m.Field == "registration_status");
    }

    [Fact]
    public async Task UAT07_same_name_is_not_duplicate_but_same_email_is_candidate()
    {
        var (ws, store) = await LoadAsync(Fixtures.AtoE);
        var a = ByName(ws, "測試甲", "甲公司");
        var c = ByName(ws, "測試甲", "丙機構");
        var e = ByName(ws, "測試戊", "丁公司");

        var found = RegistrationFilter.Apply(ws.Registrations, new RegistrationQuery { Keyword = "測試甲" });
        Assert.Equal(new[] { a.RecordId, c.RecordId }.OrderBy(x => x), found.Select(r => r.RecordId).OrderBy(x => x));
        Assert.NotEqual(a.RecordId, c.RecordId);

        Assert.DoesNotContain(c.Issues, i => IssueCodes.IsDuplicate(i.Code));
        Assert.Contains(e.Issues, i => i.Code == IssueCodes.DuplicateEmail && i.RelatedRecordId == a.RecordId);
        Assert.Contains(a.Issues, i => i.Code == IssueCodes.DuplicateEmail && i.RelatedRecordId == e.RecordId);
        Assert.Equal(5, store.AdminRows.Count);
        Assert.All(store.AdminRows, r => Assert.False(r.IsArchived));
    }

    [Fact]
    public async Task UAT12_override_is_effective_and_tax_id_warning_does_not_block()
    {
        var (ws, store) = await LoadAsync(Fixtures.AtoE);
        var a = ByName(ws, "測試甲", "甲公司");
        var d = ByName(ws, "測試丁", "甲公司");

        var working = a.Admin.Clone();
        working[AdminColumns.OverrideInvoiceTitle] = "甲公司股份有限公司";
        await ws.SaveAsync(a, working, CancellationToken.None);

        Assert.Equal("甲公司股份有限公司", a.Effective(LogicalFields.InvoiceTitle));
        Assert.Equal("甲公司", a.Raw(LogicalFields.InvoiceTitle));
        Assert.Equal("01234567", a.Effective(LogicalFields.TaxId));
        Assert.DoesNotContain(a.Issues, i => i.Code == IssueCodes.TaxIdFormat);

        Assert.Contains(d.Issues, i => i.Code == IssueCodes.TaxIdFormat && i.Severity == IssueSeverity.Warning);
        Assert.Equal(RegistrationStatus.Submitted, d.Admin.RegistrationStatus);

        var log = store.ChangeLog.Last();
        Assert.Contains(AdminColumns.OverrideInvoiceTitle, log.SensitiveFieldsChanged);
        Assert.DoesNotContain("甲公司股份有限公司", string.Join("|", log.ToSheetRow()));
    }

    [Fact]
    public async Task UAT13_meal_codes_and_free_text()
    {
        var (ws, _) = await LoadAsync(Fixtures.AtoE);
        string MealOf(string name, string org) => ByName(ws, name, org).EffectiveMealCode();

        Assert.Equal(MealCode.Meat, MealOf("測試甲", "甲公司"));
        Assert.Equal(MealCode.Vegetarian, MealOf("測試乙", "乙大學"));
        Assert.Equal(MealCode.None, MealOf("測試甲", "丙機構"));
        Assert.Equal(MealCode.Other, MealOf("測試丁", "甲公司"));
        Assert.Equal("不吃牛", ByName(ws, "測試丁", "甲公司").EffectiveMealOtherText());

        var others = RegistrationFilter.Apply(ws.Registrations, new RegistrationQuery { MealCode = MealCode.Other });
        Assert.Equal("測試丁", Assert.Single(others).Raw(LogicalFields.FullName));
        var meat = RegistrationFilter.Apply(ws.Registrations, new RegistrationQuery { MealCode = MealCode.Meat });
        Assert.Equal(2, meat.Count);
    }

    [Fact]
    public async Task UAT14_applicant_notes_and_channels_are_kept()
    {
        var (ws, _) = await LoadAsync(Fixtures.AtoE);
        var d = ByName(ws, "測試丁", "甲公司");
        Assert.Equal("不吃牛肉，其餘皆可", d.Raw(LogicalFields.ApplicantNotes));
        Assert.Equal(new[] { "同事/朋友推薦", "其他" }, d.Source!.Channels);
    }

    [Fact]
    public async Task UAT18_organization_filter_returns_A_and_D()
    {
        var (ws, _) = await LoadAsync(Fixtures.AtoE);
        var result = RegistrationFilter.Apply(ws.Registrations, new RegistrationQuery { Keyword = "甲公司" });
        Assert.Equal(new[] { "測試甲", "測試丁" }, result.Select(r => r.Raw(LogicalFields.FullName)).ToArray());
    }

    [Fact]
    public async Task UAT17_failed_save_keeps_input_and_retry_writes_once()
    {
        var (ws, store) = await LoadAsync(Fixtures.AtoE);
        var a = ByName(ws, "測試甲", "甲公司");
        var working = a.Admin.Clone();
        working[AdminColumns.AdminNotes] = "待確認會員編號";

        store.FailNextWrite = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => ws.SaveAsync(a, working, CancellationToken.None));
        Assert.Equal("", a.Admin[AdminColumns.AdminNotes]);
        Assert.Equal("待確認會員編號", working[AdminColumns.AdminNotes]);

        Assert.True(await ws.SaveAsync(a, working, CancellationToken.None));
        Assert.Equal(5, store.AdminRows.Count);
        Assert.Equal("待確認會員編號", store.AdminRows.Single(r => r.RecordId == a.RecordId)[AdminColumns.AdminNotes]);
        Assert.Equal(2, a.Admin.RowVersion);
    }

    [Fact]
    public async Task UAT20_concurrent_edit_is_detected_and_not_overwritten()
    {
        var (ws, store) = await LoadAsync(Fixtures.AtoE);
        var a = ByName(ws, "測試甲", "甲公司");
        store.TouchFromElsewhere(a.RecordId);

        var working = a.Admin.Clone();
        working[AdminColumns.AdminNotes] = "本機修改";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => ws.SaveAsync(a, working, CancellationToken.None));
        Assert.Equal("", store.AdminRows.Single(r => r.RecordId == a.RecordId)[AdminColumns.AdminNotes]);
    }

    [Fact]
    public async Task Accepting_issue_marks_it_accepted()
    {
        var (ws, _) = await LoadAsync(Fixtures.AtoE);
        var d = ByName(ws, "測試丁", "甲公司");
        await ws.AcceptIssueAsync(d, IssueCodes.TaxIdFormat, "個人報名，不需統編", CancellationToken.None);

        var issue = d.Issues.Single(i => i.Code == IssueCodes.TaxIdFormat);
        Assert.True(issue.Accepted);
        Assert.Contains("個人報名", d.Admin[AdminColumns.IssueNotes]);
    }

    [Fact]
    public async Task Dashboard_counts_meals_and_statuses()
    {
        var (ws, _) = await LoadAsync(Fixtures.AtoE);
        var summary = ws.Dashboard();
        Assert.Equal(5, summary.Total);
        Assert.Equal(5, summary.ByStatus[RegistrationStatus.Submitted]);
        Assert.Equal(2, summary.MealCounts[MealCode.Meat]);
        Assert.Equal(5, summary.MembershipUnchecked);
        Assert.Equal(20, summary.Capacity.Available);
    }
}
