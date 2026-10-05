using RegistrationAdmin.App.Presentation;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Rules;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.App.Tests;

/// <summary>詳細頁的「下一步」按鈕：只用合法轉換、失敗時整個還原。</summary>
public sealed class WorkflowTests
{
    private static readonly DateTime Today = new(2026, 10, 2);

    [Fact]
    public async Task Submitted_PrimaryAction_ConfirmsMembershipAndStartsReview()
    {
        var h = await Harness.CreateAsync();
        var b = h.ByName("測試乙", "乙大學");
        var working = b.Admin.Clone();

        var action = FlowActions.Primary(working)!;
        Assert.Equal("確認會員並開始審核", action.Label);

        Assert.Empty(FlowActions.Execute(action, b, working, h.Workspace, Today));
        Assert.Equal(RegistrationStatus.UnderReview, working.RegistrationStatus);
        Assert.Equal(MembershipStatus.Verified, working.MembershipStatus);
        Assert.Equal(RegistrationStatus.Submitted, b.Admin.RegistrationStatus);
    }

    [Fact]
    public async Task UnderReview_Approve_SetsEligibility_ReservesSeat_AndAsksForPayment()
    {
        var h = await Harness.CreateAsync();
        var b = h.ByName("測試乙", "乙大學");
        var working = b.Admin.Clone();
        FlowActions.Execute(FlowActions.Primary(working)!, b, working, h.Workspace, Today);

        var approve = FlowActions.Primary(working)!;
        Assert.Equal("通過並等待付款", approve.Label);
        Assert.Empty(FlowActions.Execute(approve, b, working, h.Workspace, Today));

        Assert.Equal(RegistrationStatus.ApprovedPendingPayment, working.RegistrationStatus);
        Assert.Equal(EligibilityStatus.Eligible, working.EligibilityStatus);
        Assert.Equal(PaymentStatus.Pending, working.PaymentStatus);
        Assert.NotEmpty(working[AdminColumns.SeatReservedAt]);
    }

    [Fact]
    public async Task Approve_WhenConsentMissing_FailsAndRestoresEverything()
    {
        var noConsent = Fixtures.B with { Consents = "" };
        var h = await Harness.CreateAsync(new[] { noConsent });
        var b = h.Workspace.Registrations.Single();
        var working = b.Admin.Clone();
        working[AdminColumns.RegistrationStatus] = RegistrationStatus.UnderReview;
        var before = working.Clone();

        var messages = FlowActions.Execute(FlowActions.Primary(working)!, b, working, h.Workspace, Today);

        Assert.NotEmpty(messages);
        Assert.Empty(working.DiffColumns(before));
    }

    [Fact]
    public async Task ApprovedPendingPayment_Confirm_FillsPaymentDatesWithToday()
    {
        var h = await Harness.CreateAsync();
        var b = h.ByName("測試乙", "乙大學");
        var working = b.Admin.Clone();
        working[AdminColumns.RegistrationStatus] = RegistrationStatus.ApprovedPendingPayment;
        working[AdminColumns.PaymentStatus] = PaymentStatus.Reported;

        var confirm = FlowActions.Primary(working)!;
        Assert.Equal("確認已付款，完成報名", confirm.Label);
        Assert.Empty(FlowActions.Execute(confirm, b, working, h.Workspace, Today));

        Assert.Equal(RegistrationStatus.Confirmed, working.RegistrationStatus);
        Assert.Equal(PaymentStatus.Verified, working.PaymentStatus);
        Assert.Equal("2026-10-02", working[AdminColumns.PaidAt]);
        Assert.Equal("2026-10-02", working[AdminColumns.VerifiedAt]);
    }

    [Fact]
    public async Task Reject_FromSubmitted_NeedsReason_AndGoesThroughReview()
    {
        var h = await Harness.CreateAsync();
        var b = h.ByName("測試乙", "乙大學");
        var working = b.Admin.Clone();

        var reject = FlowActions.Others(working).Single(a => a.Id == "reject");
        Assert.True(reject.RequiresConfirm);
        Assert.Equal(AdminColumns.ReviewNotes, reject.ReasonColumn);

        Assert.Empty(FlowActions.Execute(reject, b, working, h.Workspace, Today, "非會員"));
        Assert.Equal(RegistrationStatus.Rejected, working.RegistrationStatus);
        Assert.Equal("非會員", working[AdminColumns.ReviewNotes]);
    }

    [Theory]
    [InlineData(RegistrationStatus.Submitted)]
    [InlineData(RegistrationStatus.UnderReview)]
    [InlineData(RegistrationStatus.ApprovedPendingPayment)]
    [InlineData(RegistrationStatus.Waitlisted)]
    [InlineData(RegistrationStatus.Confirmed)]
    [InlineData(RegistrationStatus.Rejected)]
    [InlineData(RegistrationStatus.Cancelled)]
    [InlineData(RegistrationStatus.PaymentExpired)]
    public void OfferedSteps_AreAlwaysLegalTransitions(string status)
    {
        var working = new AdminRecord { [AdminColumns.RegistrationStatus] = status };
        var actions = FlowActions.Others(working).ToList();
        if (FlowActions.Primary(working) is { } primary)
        {
            actions.Add(primary);
        }

        foreach (var action in actions.Where(a => a.Kind == FlowKind.Transition))
        {
            var from = status;
            foreach (var to in action.Path)
            {
                Assert.True(StatusTransitions.IsAllowed(from, to), $"{action.Label}：{from} → {to}");
                from = to;
            }
        }

        // 高風險動作一律要確認；封存永遠放在「其他動作」。
        Assert.All(FlowActions.Others(working), a => Assert.True(a.RequiresConfirm));
        Assert.Contains(FlowActions.Others(working), a => a.Kind == FlowKind.Archive);
    }

    [Theory]
    [InlineData(RegistrationStatus.Confirmed)]
    [InlineData(RegistrationStatus.Rejected)]
    [InlineData(RegistrationStatus.Cancelled)]
    [InlineData(RegistrationStatus.PaymentExpired)]
    public void FinalStates_HaveNoPrimaryStep(string status)
    {
        Assert.Null(FlowActions.Primary(new AdminRecord { [AdminColumns.RegistrationStatus] = status }));
    }
}
