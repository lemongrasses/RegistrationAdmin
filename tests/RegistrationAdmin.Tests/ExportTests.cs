using System.Text;
using ClosedXML.Excel;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.Search;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Export;
using RegistrationAdmin.Tests.TestData;

namespace RegistrationAdmin.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ra-export-" + Guid.NewGuid().ToString("N"));

    public ExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<RegistrationWorkspace> LoadAsync()
    {
        var ws = new RegistrationWorkspace(new FakeStore(Fixtures.Table(Fixtures.AtoE)), new FixedClock(), new SequentialIds(), "test");
        await ws.RefreshAsync(CancellationToken.None);
        return ws;
    }

    private static ExportRequest Request(RegistrationWorkspace ws, IReadOnlyList<Registration> rows, ExportTemplate template, bool sensitive = true, bool? notes = null) =>
        new()
        {
            Template = template,
            Registrations = rows,
            Lookups = ws.Lookups,
            IncludeSensitive = sensitive,
            IncludeApplicantNotes = notes,
            FilterSummary = "測試",
            DataVersion = "v-test",
            GeneratedAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.FromHours(8)),
        };

    [Fact]
    public async Task UAT18_xlsx_matches_filter_and_keeps_leading_zeros()
    {
        var ws = await LoadAsync();
        var filtered = RegistrationFilter.Apply(ws.Registrations, new RegistrationQuery { Keyword = "甲公司" });
        var path = Path.Combine(_dir, "a.xlsx");

        var count = ExportService.Export(Request(ws, filtered, ExportTemplates.AllRegistrations), ExportFormat.Xlsx, path);

        Assert.Equal(2, count);
        using var wb = new XLWorkbook(path);
        var sheet = wb.Worksheet("名單");
        var headers = sheet.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        var nameCol = headers.IndexOf("姓名") + 1;
        var phoneCol = headers.IndexOf("聯絡電話") + 1;
        var taxCol = headers.IndexOf("統一編號") + 1;

        Assert.Equal("測試甲", sheet.Cell(2, nameCol).GetString());
        Assert.Equal("測試丁", sheet.Cell(3, nameCol).GetString());
        Assert.Equal("0911000001", sheet.Cell(2, phoneCol).GetString());
        Assert.Equal(XLDataType.Text, sheet.Cell(2, taxCol).DataType);
        Assert.Equal("01234567", sheet.Cell(2, taxCol).GetString());
        Assert.True(sheet.Cell(4, nameCol).IsEmpty());
        Assert.NotNull(wb.Worksheet("匯出資訊"));
    }

    [Fact]
    public async Task UAT18_csv_matches_filter_and_protects_leading_zeros()
    {
        var ws = await LoadAsync();
        var filtered = RegistrationFilter.Apply(ws.Registrations, new RegistrationQuery { Keyword = "甲公司" });
        var path = Path.Combine(_dir, "a.csv");

        ExportService.Export(Request(ws, filtered, ExportTemplates.AllRegistrations), ExportFormat.Csv, path);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "CSV 應有 UTF-8 BOM");
        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("\"=\"\"01234567\"\"\"", text);
        Assert.Contains("\"=\"\"0911000001\"\"\"", text);
        Assert.Contains("測試甲", text);
        Assert.Contains("測試丁", text);
        Assert.DoesNotContain("測試乙", text);
        Assert.StartsWith("# 產生時間", text.TrimStart('﻿'));
    }

    [Fact]
    public async Task UAT14_notes_and_channels_optional_for_review_list()
    {
        var ws = await LoadAsync();
        var full = ExportTableBuilder.Build(Request(ws, ws.Registrations, ExportTemplates.AllRegistrations));
        Assert.Contains(full.Columns, c => c.Key == "applicant_notes");
        Assert.Contains(full.Columns, c => c.Key == "acquisition_channels");
        var dRow = full.Rows.Single(r => r.Contains("測試丁"));
        Assert.Contains("不吃牛肉，其餘皆可", dRow);
        Assert.Contains("同事/朋友推薦、其他", dRow);

        var review = ExportTableBuilder.Build(Request(ws, ws.Registrations, ExportTemplates.PendingReview, notes: false));
        Assert.DoesNotContain(review.Columns, c => c.IsApplicantNotes);
        Assert.Equal(5, review.Rows.Count);
    }

    [Fact]
    public async Task UAT20_sensitive_columns_can_be_excluded()
    {
        var ws = await LoadAsync();
        var table = ExportTableBuilder.Build(Request(ws, ws.Registrations, ExportTemplates.AllRegistrations, sensitive: false));
        Assert.DoesNotContain(table.Columns, c => c.IsSensitive);
        Assert.DoesNotContain(table.Rows.SelectMany(r => r), v => v.Contains("@example.test"));
        Assert.DoesNotContain(table.Rows.SelectMany(r => r), v => v == "0911000001");
    }

    [Fact]
    public async Task Raw_mode_ignores_overrides()
    {
        var ws = await LoadAsync();
        var a = ws.Registrations.First();
        var working = a.Admin.Clone();
        working[AdminColumns.OverrideName] = "修正後姓名";
        await ws.SaveAsync(a, working, CancellationToken.None);

        var effective = ExportTableBuilder.Build(Request(ws, new[] { a }, ExportTemplates.AllRegistrations));
        var raw = ExportTableBuilder.Build(new ExportRequest
        {
            Template = ExportTemplates.AllRegistrations,
            Registrations = new[] { a },
            Lookups = ws.Lookups,
            Mode = ExportValueMode.Raw,
        });
        Assert.Contains("修正後姓名", effective.Rows[0]);
        Assert.Contains("測試甲", raw.Rows[0]);
    }

    [Fact]
    public async Task Meal_template_has_summary()
    {
        var ws = await LoadAsync();
        var table = ExportTableBuilder.Build(Request(ws, ws.Registrations, ExportTemplates.Meal));
        Assert.Contains(table.Summary, kv => kv.Key == "葷食" && kv.Value == "2");
        Assert.Contains(table.Summary, kv => kv.Key == "合計" && kv.Value == "5");
    }
}
