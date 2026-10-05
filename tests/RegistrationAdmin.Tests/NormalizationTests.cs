using RegistrationAdmin.Core.Diagnostics;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Identity;
using RegistrationAdmin.Core.Normalization;
using RegistrationAdmin.Core.Sync;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.Tests;

public class NormalizationTests
{
    [Theory]
    [InlineData("  Alpha@Example.TEST ", "alpha@example.test")]
    [InlineData("ａｌｐｈａ@example.test", "alpha@example.test")]
    [InlineData("", "")]
    public void Email_is_trimmed_and_lowercased(string input, string expected) =>
        Assert.Equal(expected, Normalizers.Email(input));

    [Theory]
    [InlineData("0911-000-001", "0911000001")]
    [InlineData("(06) 275-7575", "062757575")]
    [InlineData("+886 911 000 001", "+886911000001")]
    [InlineData("０９１１０００００１", "0911000001")]
    public void Phone_removes_only_known_separators(string input, string expected) =>
        Assert.Equal(expected, Normalizers.Phone(input));

    [Theory]
    [InlineData("葷食", MealCode.Meat, "")]
    [InlineData("素食", MealCode.Vegetarian, "")]
    [InlineData("不需午餐", MealCode.None, "")]
    [InlineData("其他：不吃牛", MealCode.Other, "不吃牛")]
    [InlineData("不吃牛", MealCode.Other, "不吃牛")]
    [InlineData("", "", "")]
    public void Meal_keeps_free_text_separate(string input, string code, string other)
    {
        var (c, o) = Normalizers.Meal(input);
        Assert.Equal(code, c);
        Assert.Equal(other, o);
    }

    [Theory]
    [InlineData("01234567", true)]
    [InlineData("個人", false)]
    [InlineData("1234567", false)]
    [InlineData("０１２３４５６７", true)]
    public void Tax_id_eight_digit_check(string input, bool expected) =>
        Assert.Equal(expected, Normalizers.IsTaxId8Digits(input));

    [Fact]
    public void Option_selected_matches_partial_option_text()
    {
        Assert.True(Normalizers.OptionSelected(Fixtures.ConsentBoth, "三日全勤"));
        Assert.True(Normalizers.OptionSelected(Fixtures.ConsentBoth, "同意主辦單位"));
        Assert.False(Normalizers.OptionSelected(Fixtures.ConsentData, "三日全勤"));
        Assert.False(Normalizers.OptionSelected("", "三日全勤"));
    }

    [Fact]
    public void Multi_choice_splits_google_forms_separator()
    {
        var channels = Normalizers.MultiChoice("同事/朋友推薦, 其他");
        Assert.Equal(new[] { "同事/朋友推薦", "其他" }, channels);
    }

    [Fact]
    public void Source_key_is_stable_and_case_insensitive_for_email()
    {
        var token = SourceIdentity.TimestampToken(46296.375, "");
        var k1 = SourceIdentity.ComputeSourceKey("DEMO-2026", token, Normalizers.Email("Alpha@Example.test"), Normalizers.Phone("0911-000-001"));
        var k2 = SourceIdentity.ComputeSourceKey("DEMO-2026", token, Normalizers.Email("alpha@example.test"), Normalizers.Phone("0911000001"));
        var k3 = SourceIdentity.ComputeSourceKey("DEMO-2026", SourceIdentity.TimestampToken(46296.376, ""), "alpha@example.test", "0911000001");
        Assert.Equal(k1, k2);
        Assert.NotEqual(k1, k3);
        Assert.Equal(64, k1.Length);
    }

    [Fact]
    public void Fingerprint_changes_when_any_answer_changes()
    {
        var a = new Dictionary<string, string> { ["full_name"] = "測試甲", ["job_title"] = "工程師" };
        var b = new Dictionary<string, string> { ["job_title"] = "工程師", ["full_name"] = "測試甲" };
        var c = new Dictionary<string, string> { ["full_name"] = "測試甲", ["job_title"] = "經理" };
        Assert.Equal(SourceIdentity.ComputeFingerprint("1", a), SourceIdentity.ComputeFingerprint("1", b));
        Assert.NotEqual(SourceIdentity.ComputeFingerprint("1", a), SourceIdentity.ComputeFingerprint("1", c));
    }

    [Fact]
    public void Default_headers_pass_health_check()
    {
        var health = SchemaHealthCheck.Check(Fixtures.Headers, DefaultProfile.FieldMappings);
        Assert.True(health.IsHealthy, health.Describe());
        Assert.Empty(health.UnmappedHeaders);
    }

    [Fact]
    public void Full_width_and_spacing_variants_of_header_still_match()
    {
        var headers = Fixtures.Headers.ToArray();
        headers[3] = "服務/所屬單位  （如：某大學某學系）";
        var health = SchemaHealthCheck.Check(headers, DefaultProfile.FieldMappings);
        Assert.True(health.IsHealthy, health.Describe());
    }

    [Fact]
    public void UAT06_renamed_email_header_fails_health_check()
    {
        var headers = Fixtures.Headers.ToArray();
        headers[5] = "電子信箱（新）";
        var health = SchemaHealthCheck.Check(headers, DefaultProfile.FieldMappings);
        Assert.False(health.IsHealthy);
        Assert.Contains(LogicalFields.Email, health.MissingRequiredFields);
        Assert.Contains("電子信箱（新）", health.UnmappedHeaders);
    }

    [Fact]
    public void Redactor_masks_personal_data_and_tokens()
    {
        var text = "user alpha@example.test phone 0911-000-001 tax 01234567 id A123456789 token ya29.abc-DEF_123 "
                   + "{\"refresh_token\": \"1//0gabcdefghijk\"} date 2026-10-02";
        var redacted = Redactor.Redact(text);
        Assert.DoesNotContain("alpha@example.test", redacted);
        Assert.DoesNotContain("0911-000-001", redacted);
        Assert.DoesNotContain("01234567", redacted);
        Assert.DoesNotContain("A123456789", redacted);
        Assert.DoesNotContain("ya29.", redacted);
        Assert.DoesNotContain("1//0g", redacted);
        Assert.Contains("2026-10-02", redacted);
    }

    [Theory]
    [InlineData("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789/edit#gid=0", "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("not an id", null)]
    public void Spreadsheet_id_parser(string input, string? expected) =>
        Assert.Equal(expected, SpreadsheetIdParser.Parse(input));
}
