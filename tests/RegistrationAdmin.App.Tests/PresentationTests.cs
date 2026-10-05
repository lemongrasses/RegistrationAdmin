using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Google;
using RegistrationAdmin.App.Presentation;
using RegistrationAdmin.App.ViewModels;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.App.Tests;

public sealed class PresentationTests
{
    /// <summary>一般模式文字不能出現內部欄名（底線命名或英文代碼）。使用者熟悉的 Email、Google Form、Excel 允許。</summary>
    private static void AssertPlainChinese(string text)
    {
        Assert.DoesNotContain("_", text);
        var english = Regex.Matches(text, "[A-Za-z]{2,}").Select(m => m.Value).Where(w => w is not ("Email" or "Google" or "Form" or "Excel")).ToList();
        Assert.True(english.Count == 0, $"出現英文內部名稱：{string.Join(",", english)}（{text}）");
    }

    [Fact]
    public async Task NewRegistration_IsPending_WithReviewSteps()
    {
        var h = await Harness.CreateAsync();
        var b = h.ByName("測試乙", "乙大學");

        var problems = WorkQueue.Problems(b);

        Assert.Contains("會員未核對", problems);
        Assert.Contains("資格待判定", problems);
        Assert.True(WorkQueue.NeedsAttention(b));
    }

    [Fact]
    public async Task ConfirmedRegistration_WithoutIssues_IsNotPending()
    {
        var h = await Harness.CreateAsync();
        var b = h.ByName("測試乙", "乙大學");
        b.Admin[AdminColumns.RegistrationStatus] = RegistrationStatus.Confirmed;
        b.Admin[AdminColumns.MembershipStatus] = MembershipStatus.Verified;
        b.Admin[AdminColumns.EligibilityStatus] = EligibilityStatus.Eligible;
        b.Admin[AdminColumns.PaymentStatus] = PaymentStatus.Verified;

        Assert.Empty(WorkQueue.TodoItems(b));
        Assert.False(WorkQueue.NeedsAttention(b));
    }

    [Fact]
    public async Task ApprovedAwaitingPayment_IsPending_ButHasNoProblem()
    {
        var h = await Harness.CreateAsync();
        var b = h.ByName("測試乙", "乙大學");
        b.Admin[AdminColumns.RegistrationStatus] = RegistrationStatus.ApprovedPendingPayment;
        b.Admin[AdminColumns.PaymentStatus] = PaymentStatus.Pending;

        Assert.Empty(WorkQueue.Problems(b));
        Assert.Equal(new[] { "等待付款" }, WorkQueue.TodoItems(b));
        Assert.Equal("—", WorkQueue.ProblemSummary(WorkQueue.Problems(b)));
    }

    [Fact]
    public async Task CancelledRegistration_IsNeverPending_EvenWithDuplicateWarning()
    {
        var h = await Harness.CreateAsync();
        var a = h.ByName("測試甲", "甲公司");
        Assert.True(WorkQueue.HasOpenDuplicate(a));
        a.Admin[AdminColumns.RegistrationStatus] = RegistrationStatus.Cancelled;

        Assert.False(WorkQueue.NeedsAttention(a));
    }

    [Theory]
    [InlineData(IssueCodes.EmailFormat)]
    [InlineData(IssueCodes.PhoneFormat)]
    [InlineData(IssueCodes.TaxIdFormat)]
    [InlineData(IssueCodes.MealOtherText)]
    [InlineData(IssueCodes.DuplicateEmail)]
    [InlineData(IssueCodes.DuplicatePhone)]
    [InlineData(IssueCodes.DuplicateNameOrg)]
    [InlineData(IssueCodes.SourceChanged)]
    [InlineData(IssueCodes.Orphan)]
    [InlineData(IssueCodes.SourceKeyCollision)]
    [InlineData("MISSING_PHONE")]
    [InlineData("CONSENT_DATA_USE_CONSENT")]
    public void IssueText_ForGeneralUsers_HasNoCodesOrColumnNames(string code)
    {
        var field = code switch
        {
            "MISSING_PHONE" => LogicalFields.Phone,
            "CONSENT_DATA_USE_CONSENT" => LogicalFields.DataUseConsent,
            _ => "source",
        };
        var issue = new Issue(code, IssueSeverity.Warning, field, "內部說明");

        AssertPlainChinese(WorkQueue.ShortIssue(issue));
        AssertPlainChinese(WorkQueue.LongIssue(issue));
    }

    [Fact]
    public void ProblemSummary_ShowsSingleTextOrCount()
    {
        Assert.Equal("—", WorkQueue.ProblemSummary(Array.Empty<string>()));
        Assert.Equal("電話不完整", WorkQueue.ProblemSummary(new[] { "電話不完整" }));
        Assert.Equal("2 項待處理", WorkQueue.ProblemSummary(new[] { "會員未核對", "電話不完整" }));
    }

    [Fact]
    public void FriendlyTime_UsesTodayYesterdayAndBlankText()
    {
        var now = new DateTime(2026, 10, 2, 15, 0, 0);
        Assert.Equal("今天 09:42", FriendlyTime.Format(new DateTime(2026, 10, 2, 9, 42, 0), now));
        Assert.Equal("昨天 16:21", FriendlyTime.Format(new DateTime(2026, 10, 1, 16, 21, 0), now));
        Assert.Equal("09/28 11:36", FriendlyTime.Format(new DateTime(2026, 9, 28, 11, 36, 0), now));
        Assert.Equal("尚未填寫", FriendlyTime.OrBlank("  "));
    }

    [Fact]
    public void EventDates_AreShownInChinese()
    {
        Assert.Equal("11 月 1–3 日", MainViewModel.DateRange("2026-11-01", "2026-11-03"));
        Assert.Equal("10 月 30 日–11 月 2 日", MainViewModel.DateRange("2026-10-30", "2026-11-02"));
        Assert.Equal("", MainViewModel.DateRange("", ""));
    }

    [Fact]
    public void GoogleErrors_BecomeActionableChinese()
    {
        static GoogleApiException Api(HttpStatusCode code) => new("Sheets", "boom") { HttpStatusCode = code };

        Assert.Equal(UserMessages.NoPermission, UserMessages.Describe(new SheetsAccessException("Google 回應 403", Api(HttpStatusCode.Forbidden))));
        Assert.Equal(UserMessages.NotFound, UserMessages.Describe(new SheetsAccessException("Google 回應 404", Api(HttpStatusCode.NotFound))));
        Assert.Equal(UserMessages.Network, UserMessages.Describe(new HttpRequestException("socket")));
        Assert.Equal(UserMessages.Conflict, UserMessages.Describe(new ConcurrencyConflictException(new[] { "x" })));

        var unknown = UserMessages.Describe(new NullReferenceException("Object reference"));
        Assert.DoesNotContain("Exception", unknown);
        Assert.DoesNotContain("403", UserMessages.Describe(new SheetsAccessException("Google 回應 403", Api(HttpStatusCode.Forbidden))));
    }

    [Fact]
    public void ValidationMessages_UseChineseFieldNames()
    {
        var text = UserMessages.Validation(new ValidationMessage(AdminColumns.PaidAt, "「paid_at」日期格式無法辨識"));
        AssertPlainChinese(text);
        Assert.Contains("付款日期", text);

        AssertPlainChinese(UserMessages.Validation(new ValidationMessage(AdminColumns.DuplicateOfRecordId, "必須指定 record_id")));
    }

    [Fact]
    public void EveryEditableColumn_HasChineseLabel()
    {
        var internalOnly = new[]
        {
            AdminColumns.RecordId, AdminColumns.RegistrationNo, AdminColumns.ProfileId, AdminColumns.SourceTimestamp,
            AdminColumns.SourceEmailSnapshot, AdminColumns.SourceRowHint, AdminColumns.SourceFingerprint,
            AdminColumns.RowVersion, AdminColumns.CreatedAt, AdminColumns.UpdatedAt,
        };
        foreach (var column in AdminColumns.All.Except(internalOnly))
        {
            AssertPlainChinese(FieldLabels.Label(column));
        }

        Assert.Equal("會員核對、付款日期", FieldLabels.Describe("membership_status,paid_at,updated_at"));
    }

    [Fact]
    public async Task ListRows_ShowNotFilledInsteadOfBlank()
    {
        var blankOrg = Fixtures.B with { Org = "", InvoiceTitle = "乙大學" };
        var h = await Harness.CreateAsync(new[] { blankOrg });

        var row = h.Main.Rows.Single();
        Assert.Equal("尚未填寫", row.Organization);
        Assert.Contains("新報名", row.Status);
        Assert.Equal(ChipTone.Info, row.StatusTone);
        Assert.Equal(ChipTone.Warning, row.MembershipTone);
    }
}
