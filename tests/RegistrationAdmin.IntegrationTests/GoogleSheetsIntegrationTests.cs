using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.GoogleSheets.Auth;

namespace RegistrationAdmin.IntegrationTests;

/// <summary>
/// 只對「複製的測試 Spreadsheet」執行。未設定環境變數時自動略過。
///   RA_IT_SPREADSHEET_ID   測試試算表 ID（絕不可用正式回應試算表）
///   RA_IT_CLIENT_SECRET    Desktop OAuth client JSON 路徑
///   RA_IT_SOURCE_SHEET_ID  測試回應工作表的 sheetId
/// 第一次執行會開啟瀏覽器授權。
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RA_IT_SPREADSHEET_ID"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RA_IT_CLIENT_SECRET")))
        {
            Skip = "未設定 RA_IT_SPREADSHEET_ID／RA_IT_CLIENT_SECRET，略過 Google 整合測試。";
        }
    }
}

internal sealed class EnvSettings : IConnectionSettings
{
    public string ClientSecretPath => Environment.GetEnvironmentVariable("RA_IT_CLIENT_SECRET") ?? "";

    public string SpreadsheetId => Environment.GetEnvironmentVariable("RA_IT_SPREADSHEET_ID") ?? "";

    public string TokenDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegistrationAdmin", "it-tokens");
}

public class GoogleSheetsIntegrationTests
{
    [IntegrationFact]
    public async Task Initialize_refresh_twice_is_idempotent_and_source_is_untouched()
    {
        var settings = new EnvSettings();
        using var auth = new GoogleSheetsAuthorizer(settings);
        var schema = new SchemaManager(auth, settings);
        var store = new GoogleSheetsRegistrationStore(auth, settings);
        var ct = CancellationToken.None;

        // 未設定 RA_IT_SOURCE_SHEET_ID 時，沿用試算表 _Config 已記錄的來源工作表（程式已初始化過的測試表）。
        var sourceSheetId = int.TryParse(Environment.GetEnvironmentVariable("RA_IT_SOURCE_SHEET_ID"), out var configuredId)
            ? configuredId
            : (await store.LoadAsync(ct)).Config.SourceSheetId
              ?? throw new InvalidOperationException("請設定 RA_IT_SOURCE_SHEET_ID，或先用程式初始化測試試算表。");
        await schema.InitializeAsync(sourceSheetId, ct);

        var before = await store.LoadAsync(ct);
        var ws = new RegistrationWorkspace(store, new TaipeiClock(), new GuidIdGenerator(), "it");
        await ws.RefreshAsync(ct);
        await ws.RefreshAsync(ct);
        var after = await store.LoadAsync(ct);

        // UAT-03：重新整理不產生重複管理列
        Assert.Equal(after.AdminRows.Count, after.AdminRows.Select(a => a.SourceKey).Distinct().Count());
        Assert.Equal(ws.Registrations.Count(r => r.Source is not null), after.AdminRows.Count(a => ws.Registrations.Any(r => r.RecordId == a.RecordId && r.Source is not null)));

        // UAT-19：原始回應逐欄一致
        Assert.Equal(before.Source.Headers, after.Source.Headers);
        Assert.Equal(before.Source.Rows.Count, after.Source.Rows.Count);
        for (var i = 0; i < before.Source.Rows.Count; i++)
        {
            Assert.Equal(before.Source.Rows[i].Formatted, after.Source.Rows[i].Formatted);
        }

        Assert.All(after.AdminRows, a => Assert.Equal(DefaultProfile.ProfileId, a[AdminColumns.ProfileId]));
    }

    /// <summary>UAT-20：兩個工作階段修改同一筆，第二個因 row_version 不同而被拒，雲端保留第一個的值。</summary>
    [IntegrationFact]
    public async Task Second_writer_gets_conflict_and_cloud_keeps_first_write()
    {
        var settings = new EnvSettings();
        using var auth = new GoogleSheetsAuthorizer(settings);
        var store = new GoogleSheetsRegistrationStore(auth, settings);
        var ct = CancellationToken.None;

        var first = new RegistrationWorkspace(store, new TaipeiClock(), new GuidIdGenerator(), "it");
        var second = new RegistrationWorkspace(store, new TaipeiClock(), new GuidIdGenerator(), "it");
        await first.RefreshAsync(ct);
        await second.RefreshAsync(ct);
        // 需要測試試算表至少有一筆回應（可用 tools/UatHarness seed 建立）。
        var r1 = first.Registrations.First(r => r.Source is not null);
        var r2 = second.Registrations.Single(r => r.RecordId == r1.RecordId);

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var w1 = r1.Admin.Clone();
        w1[AdminColumns.AdminNotes] = "IT 寫入者 1 " + stamp;
        await first.SaveAsync(r1, w1, ct);

        var w2 = r2.Admin.Clone();
        w2[AdminColumns.AdminNotes] = "IT 寫入者 2 " + stamp;
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => second.SaveAsync(r2, w2, ct));

        var cloud = (await store.LoadAsync(ct)).AdminRows.Single(a => a.RecordId == r1.RecordId);
        Assert.Equal("IT 寫入者 1 " + stamp, cloud[AdminColumns.AdminNotes]);
    }
}
