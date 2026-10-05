// 端對端 UAT 驗證：只可對「複製的測試試算表」執行。
// 用法：dotnet run --project tools/UatHarness -- <seed|run> <spreadsheetId> <clientSecretPath>
// seed：在原始回應頁附加明顯虛構的測試回應（規劃 13.1 的 A–G 與 P01–P05，已存在則略過）。
// run ：以真實 Google Sheets store 驅動 RegistrationWorkspace，逐項輸出 PASS／FAIL。
using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using RegistrationAdmin.Core.Abstractions;
using RegistrationAdmin.Core.Domain;
using RegistrationAdmin.Core.UseCases;
using RegistrationAdmin.Export;
using RegistrationAdmin.GoogleSheets;
using RegistrationAdmin.GoogleSheets.Auth;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 3)
{
    Console.WriteLine("用法：<seed|run> <spreadsheetId> <clientSecretPath>");
    return 2;
}

var settings = new HarnessSettings(args[1], args[2]);
using var auth = new GoogleSheetsAuthorizer(settings);
var schema = new SchemaManager(auth, settings);
var store = new GoogleSheetsRegistrationStore(auth, settings);
var ct = CancellationToken.None;

return args[0] switch
{
    "seed" => await SeedAsync(),
    "run" => await RunAsync(),
    "headers" => await HeadersAsync(),
    "more" => await new MoreUat(auth, store, args[1], args.Length > 3 ? args[3] : null).RunAsync(ct),
    "diag" => await new MoreUat(auth, store, args[1]).DiagnoseAsync(ct),
    "restore" => await new MoreUat(auth, store, args[1]).RestoreAsync(ct),
    _ => 2,
};

async Task<int> HeadersAsync()
{
    var snapshot = await store.LoadAsync(ct);
    var i = 0;
    foreach (var h in snapshot.Source.Headers)
    {
        Console.WriteLine($"{ColumnName(++i)}: {h}");
    }

    return 0;
}

async Task<int> SeedAsync()
{
    var service = await auth.GetAsync(ct);
    var snapshot = await store.LoadAsync(ct);
    var sourceId = snapshot.Config.SourceSheetId ?? throw new InvalidOperationException("_Config 沒有 source_sheet_id，請先用程式初始化。");
    var info = await schema.GetSpreadsheetAsync(ct);
    var title = info.Sheets.Single(s => s.SheetId == sourceId).Title;
    var headers = snapshot.Source.Headers;
    Console.WriteLine($"原始回應頁「{title}」，欄位 {headers.Count} 個，現有回應 {snapshot.Source.Rows.Count} 列。");

    var existingNames = snapshot.Source.Rows.Select(r => r.Formatted.Count > 2 ? r.Formatted[2] : "").ToList();
    var all = Seed.All().ToList();
    // 之前由本工具寫入的列（第 2 列起、姓名順序相同）直接覆寫，其餘情況只附加尚未存在的資料。
    var rewrite = existingNames.Count >= all.Count && existingNames.Take(all.Count).SequenceEqual(all.Select(a => a.Name));
    var answers = rewrite ? all : all.Where(a => !existingNames.Contains(a.Name)).ToList();
    if (answers.Count == 0)
    {
        Console.WriteLine("測試資料已存在，略過。");
        return 0;
    }

    var missingHeaders = Seed.Headers.Where(h => !headers.Select(x => x.Trim()).Contains(h)).ToList();
    if (missingHeaders.Count > 0)
    {
        Console.WriteLine("警告：回應頁缺少下列預期標題，對應欄位將留白：" + string.Join("｜", missingHeaders));
    }

    var firstRow = rewrite ? 2 : snapshot.Source.Rows.Count + 2;
    var lastColumn = ColumnName(headers.Count);
    // 時間戳記以 USER_ENTERED 寫入讓試算表解析為日期；其餘欄以 RAW 寫入（保留統編、電話的前導零）。
    var stamps = answers.Select(a => (IList<object>)new List<object> { a.Timestamp }).ToList();
    var values = answers.Select(a => (IList<object>)headers.Skip(1).Select(h => (object)a.ValueFor(h)).ToList()).ToList();
    var lastRow = firstRow + answers.Count - 1;

    var stampRequest = service.Spreadsheets.Values.Update(new ValueRange { Values = stamps }, args[1], $"'{title}'!A{firstRow}:A{lastRow}");
    stampRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
    await stampRequest.ExecuteAsync(ct);
    var valueRequest = service.Spreadsheets.Values.Update(new ValueRange { Values = values }, args[1], $"'{title}'!B{firstRow}:{lastColumn}{lastRow}");
    valueRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
    await valueRequest.ExecuteAsync(ct);
    Console.WriteLine($"已寫入 {answers.Count} 筆虛構回應（第 {firstRow}–{lastRow} 列）。");
    return 0;
}

async Task<int> RunAsync()
{
    var results = new List<(string Id, bool Pass, string Detail)>();
    void Check(string id, bool pass, string detail)
    {
        results.Add((id, pass, detail));
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {id}：{detail}");
    }

    var before = await store.LoadAsync(ct);

    // UAT-02／03：第一次同步與重複同步
    var ws = new RegistrationWorkspace(store, new TaipeiClock(), new GuidIdGenerator(), "uat");
    var first = await ws.RefreshAsync(ct);
    var second = await ws.RefreshAsync(ct);
    var afterSync = await store.LoadAsync(ct);
    var linked = ws.Registrations.Count(r => r.Source is not null);
    var distinctKeys = afterSync.AdminRows.Select(a => a[AdminColumns.SourceKey]).Distinct().Count();
    Check("UAT-02", linked == before.Source.Rows.Count && ws.Health?.IsHealthy != false,
        $"來源 {before.Source.Rows.Count} 列，連結 {linked} 筆，健康檢查 {(ws.Health?.IsHealthy == false ? "未通過：" + ws.Health.Describe() : "通過")}");
    Check("UAT-03", afterSync.AdminRows.Count == distinctKeys && afterSync.AdminRows.Count == linked,
        $"_Admin {afterSync.AdminRows.Count} 列、source_key 不重複 {distinctKeys} 個；第二次同步 {second}");

    // UAT-07／12／13：重複與資料問題
    var issues = ws.AllIssues.Where(i => !i.Accepted).ToList();
    var byCode = issues.GroupBy(i => i.Code).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}");
    Console.WriteLine("問題清單：" + string.Join("、", byCode));
    var a = ws.Registrations.SingleOrDefault(r => r.Raw(LogicalFields.FullName) == "測試甲" && r.Raw(LogicalFields.OrganizationName) == "甲公司");
    var e = ws.Registrations.SingleOrDefault(r => r.Raw(LogicalFields.FullName) == "測試戊");
    var d = ws.Registrations.SingleOrDefault(r => r.Raw(LogicalFields.FullName) == "測試丁");
    var g = ws.Registrations.SingleOrDefault(r => r.Raw(LogicalFields.FullName) == "測試庚");
    if (a is null || e is null || d is null || g is null)
    {
        Check("SEED", false, "找不到預期的測試資料（測試甲／丁／戊／庚），請先執行 seed。");
        return Summary();
    }

    Check("UAT-07", a.Issues.Any(i => i.Code == IssueCodes.DuplicateEmail) && e.Issues.Any(i => i.Code == IssueCodes.DuplicateEmail),
        "測試甲與測試戊 Email 相同 → " + string.Join(",", a.Issues.Select(i => i.Code)));
    Check("UAT-12", g.Issues.Any(i => i.Code == IssueCodes.TaxIdFormat) && g.Issues.Any(i => i.Code.StartsWith(IssueCodes.MissingPrefix, StringComparison.Ordinal)),
        "測試庚（統編 1234、缺電話）→ " + string.Join(",", g.Issues.Select(i => i.Code)));
    Check("UAT-13", d.EffectiveMealCode() != "" && d.EffectiveMealOtherText().Contains("不吃牛", StringComparison.Ordinal),
        $"測試丁午餐「其他」→ 代碼 {d.EffectiveMealCode()}，說明「{d.EffectiveMealOtherText()}」，統編「個人」→ {string.Join(",", d.Issues.Select(i => i.Code))}");

    // UAT-08：會員未確認不可轉通過待付款
    var b = ws.Registrations.Single(r => r.Raw(LogicalFields.FullName) == "測試乙");
    await ToUnderReviewAsync(b, MembershipStatus.Unchecked, EligibilityStatus.NeedsInformation);
    var blocked = ws.ValidateTransition(b, b.Admin.Clone(), RegistrationStatus.ApprovedPendingPayment);
    Check("UAT-08", blocked.Any(m => m.Field == AdminColumns.MembershipStatus),
        "測試乙會員未確認 → " + string.Join("；", blocked.Select(m => m.Message)));

    // UAT-11：審核中 → 通過待付款 → 付款核帳 → 已確認，重新讀取後仍保留
    if (a.Admin.RegistrationStatus != RegistrationStatus.Confirmed)
    {
        await ToUnderReviewAsync(a, MembershipStatus.Verified, EligibilityStatus.Eligible);
        var approve = a.Admin.Clone();
        var approveMessages = ws.TryApplyTransition(a, approve, RegistrationStatus.ApprovedPendingPayment);
        if (approveMessages.Count == 0)
        {
            await ws.SaveAsync(a, approve, ct);
        }

        var paid = a.Admin.Clone();
        paid[AdminColumns.PaymentStatus] = PaymentStatus.Verified;
        paid[AdminColumns.PaidAt] = "2026-10-05";
        paid[AdminColumns.VerifiedAt] = "2026-10-06";
        var confirmMessages = ws.TryApplyTransition(a, paid, RegistrationStatus.Confirmed);
        if (confirmMessages.Count == 0)
        {
            await ws.SaveAsync(a, paid, ct);
        }

        Console.WriteLine("轉換訊息：" + string.Join("；", approveMessages.Concat(confirmMessages).Select(m => m.Message)));
    }

    var reread = new RegistrationWorkspace(store, new TaipeiClock(), new GuidIdGenerator(), "uat");
    await reread.RefreshAsync(ct);
    var aAgain = reread.Registrations.Single(r => r.RecordId == a.RecordId);
    Check("UAT-11", aAgain.Admin.RegistrationStatus == RegistrationStatus.Confirmed && aAgain.Admin.PaymentStatus == PaymentStatus.Verified,
        $"重新讀取：報名 {aAgain.Admin.RegistrationStatus}、付款 {aAgain.Admin.PaymentStatus}、版本 {aAgain.Admin.RowVersion}");

    // UAT-18：XLSX／CSV 匯出保留前導零
    var outDir = Path.Combine(Path.GetTempPath(), "RegistrationAdmin-UAT");
    Directory.CreateDirectory(outDir);
    var request = new ExportRequest
    {
        Template = ExportTemplates.AllRegistrations,
        Registrations = reread.Registrations.ToList(),
        Lookups = reread.Lookups,
        GeneratedAt = DateTimeOffset.Now,
        FilterSummary = "UAT",
    };
    var xlsx = Path.Combine(outDir, "uat.xlsx");
    var csv = Path.Combine(outDir, "uat.csv");
    var xlsxCount = ExportService.Export(request, ExportFormat.Xlsx, xlsx);
    ExportService.Export(request, ExportFormat.Csv, csv);
    using (var book = new XLWorkbook(xlsx))
    {
        var texts = book.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetFormattedString()).ToList();
        var csvText = File.ReadAllText(csv);
        Check("UAT-18", texts.Contains("01234567") && texts.Contains("0911000001") && csvText.Contains("01234567", StringComparison.Ordinal),
            $"匯出 {xlsxCount} 筆；XLSX 統編 01234567 {(texts.Contains("01234567") ? "保留" : "遺失")}、電話 0911000001 {(texts.Contains("0911000001") ? "保留" : "遺失")}；CSV {(csvText.Contains("01234567", StringComparison.Ordinal) ? "保留" : "遺失")}（{outDir}）");
    }

    // UAT-19：原始回應頁逐欄不變
    var after = await store.LoadAsync(ct);
    var sameHeaders = before.Source.Headers.SequenceEqual(after.Source.Headers);
    var sameRows = before.Source.Rows.Count == after.Source.Rows.Count &&
                   before.Source.Rows.Zip(after.Source.Rows).All(p => p.First.Formatted.SequenceEqual(p.Second.Formatted));
    Check("UAT-19", sameHeaders && sameRows, $"標題{(sameHeaders ? "一致" : "不同")}、{before.Source.Rows.Count} 列逐欄{(sameRows ? "一致" : "不同")}");

    var dash = reread.Dashboard();
    Console.WriteLine($"儀表板：總計 {dash.Total}、名額占用 {dash.Capacity.Occupied}/{dash.Capacity.Capacity}、會員待確認 {dash.MembershipUnchecked}、待付款 {dash.PaymentPending}、錯誤 {dash.RecordsWithErrors} 筆、警告 {dash.RecordsWithWarnings} 筆、重複候選 {dash.DuplicateCandidates}");
    Console.WriteLine("狀態：" + string.Join("、", dash.ByStatus.Select(kv => $"{kv.Key} {kv.Value}")));
    Console.WriteLine("午餐：" + string.Join("、", dash.MealCounts.Select(kv => $"{kv.Key} {kv.Value}")));
    return Summary();

    // 新報名 → 審核中，並設定會員／資格後儲存（已在審核中則只更新會員／資格）。
    async Task ToUnderReviewAsync(Registration r, string membership, string eligibility)
    {
        var working = r.Admin.Clone();
        if (working.RegistrationStatus == RegistrationStatus.Submitted)
        {
            var messages = ws.TryApplyTransition(r, working, RegistrationStatus.UnderReview);
            if (messages.Count > 0)
            {
                Console.WriteLine("無法轉審核中：" + string.Join("；", messages.Select(m => m.Message)));
            }
        }

        working[AdminColumns.MembershipStatus] = membership;
        working[AdminColumns.EligibilityStatus] = eligibility;
        await ws.SaveAsync(r, working, ct);
    }

    int Summary()
    {
        Console.WriteLine($"結果：{results.Count(r => r.Pass)} PASS／{results.Count(r => !r.Pass)} FAIL");
        return results.All(r => r.Pass) ? 0 : 1;
    }
}

static string ColumnName(int index)
{
    var name = "";
    while (index > 0)
    {
        var m = (index - 1) % 26;
        name = (char)('A' + m) + name;
        index = (index - m - 1) / 26;
    }

    return name;
}

internal sealed class HarnessSettings(string spreadsheetId, string clientSecretPath) : IConnectionSettings
{
    public string ClientSecretPath => clientSecretPath;

    public string SpreadsheetId => spreadsheetId;

    // 與整合測試共用授權（已在瀏覽器同意過）。
    public string TokenDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RegistrationAdmin", "it-tokens");
}

/// <summary>規劃 13.1 的虛構測試資料（Email 皆為保留網域 example.test）。</summary>
internal sealed record Seed(string Name, string Org, string Email, string Phone, string TaxId, string Meal, int Minute,
    string Notes = "", string Channels = "")
{
    private const string Consents = "我已閱讀活動資訊，並同意主辦單位於本活動報名、聯繫、簽到、及活動行政作業範圍內使用本人填寫之資料。, 我已充分了解本工作坊為三天連貫課程，並確認我能夠三日全勤出席。";
    private const string Fee = "我已了解示範活動工作坊為酌收報名費之活動，後續將依主辦單位通知完成繳費或相關確認程序。";
    private const string Accuracy = "我確認以上資料填寫正確，並了解主辦單位將依名額狀況與資料完整性進行報名確認。";

    public static readonly string[] Headers =
    {
        "時間戳記", "請詳閱活動說明後再行勾選", "姓名", "服務/所屬單位 (如：某大學某學系)", "職稱",
        "Email (用於接收報名確認及活動資訊)", "聯絡電話(僅供活動聯繫使用)", "報名費說明確認", "發票抬頭", "統一編號",
        "午餐需求", "備註 (如：特殊飲食需求等)", "您是如何得知本次活動？", "送出前確認",
    };

    public string Timestamp => new DateTime(2026, 10, 1, 9, 0, 0).AddMinutes(Minute).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string ValueFor(string header) => Array.IndexOf(Headers, header.Trim()) switch
    {
        1 => Consents,
        2 => Name,
        3 => Org,
        4 => "工程師",
        5 => Email,
        6 => Phone,
        7 => Fee,
        8 => Org,
        9 => TaxId,
        10 => Meal,
        11 => Notes,
        12 => Channels,
        13 => Accuracy,
        _ => "",
    };

    public static IEnumerable<Seed> All()
    {
        yield return new("測試甲", "甲公司", "alpha@example.test", "0911000001", "01234567", "葷食", 0, Channels: "電子郵件/EDM");
        yield return new("測試乙", "乙大學", "beta@example.test", "0911000002", "11111111", "素食", 1, Channels: "單位內部公告");
        yield return new("測試甲", "丙機構", "gamma@example.test", "0911000003", "22222222", "不需午餐", 2, Channels: "社群媒體 (FB/IG/Line等)");
        yield return new("測試丁", "甲公司", "delta@example.test", "0911000004", "個人", "其他：不吃牛", 3, "不吃牛肉，其餘皆可", "同事/朋友推薦, 其他");
        yield return new("測試戊", "丁公司", "alpha@example.test", "0911000005", "33333333", "葷食", 4);
        yield return new("測試己", "戊研究所", "foxtrot@example.test", "0911000006", "44444444", "素食", 5);
        yield return new("測試庚", "己協會", "golf@example.test", "", "1234", "葷食", 6);
        foreach (var p in Pool(1, 5))
        {
            yield return p;
        }
    }

    /// <summary>名額測試用 P{from}–P{to}，Email 與電話皆唯一。</summary>
    public static IEnumerable<Seed> Pool(int from, int to) =>
        Enumerable.Range(from, to - from + 1)
            .Select(i => new Seed($"名額{i:D2}", $"名額單位{i:D2}", $"p{i:D2}@example.test", $"09220000{i:D2}", $"5555{i:D4}", "葷食", 100 + i));
}
