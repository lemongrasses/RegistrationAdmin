# RegistrationAdmin — 2026 機器狗工作坊報名後台（Windows）

> **聲明**
> - 本軟體**免費使用**，依現況（as-is）提供，不提供任何明示或默示的保證。
> - 作者對於使用本軟體所造成的任何損失、資料錯誤或其他後果，**不負任何責任**。
> - 本專案的程式碼與文件**完全由 AI 開發**（Anthropic Claude，經 Claude Code）。使用前請自行審查與測試。
> - 授權條款：[MIT License](LICENSE)。
>
> *Free to use, provided as-is with no warranty; the author accepts no responsibility or liability. Developed entirely by AI. Licensed under MIT.*

依《Windows 活動報名後台系統開發規劃－2026 機器狗工作坊》v2.0 實作的 MVP 專案。
讀取 Google Form 連結的回應試算表，讓管理者在 Windows 程式中審核報名、核對會員、控管 20 人名額、記錄付款／發票，並匯出名單。

- 原始回應工作表**只讀不寫**；後台狀態與修正值只寫入同一份試算表的 `_Admin`、`_ChangeLog`。
- 完整名單只存在記憶體與 Google Sheets，本機不建資料庫。
- 沒有應用程式帳號、角色或權限頁；資料存取邊界由 Google 帳號與試算表共用設定決定。

## 方案結構

```
RegistrationAdmin.slnx
src/
  RegistrationAdmin.Core/           模型、狀態規則、驗證、同步合併、篩選、使用案例（無外部套件）
  RegistrationAdmin.GoogleSheets/   OAuth（DPAPI 保存 token）、管理分頁、唯讀來源、批次讀寫、重試
  RegistrationAdmin.Export/         XLSX（ClosedXML）／CSV（CsvHelper）、8 種預設範本、前導零保護
  RegistrationAdmin.App/            WPF + MVVM（CommunityToolkit.Mvvm）、DI、Serilog 遮蔽日誌
tests/
  RegistrationAdmin.Tests/          xUnit：規劃 13 章固定測試資料 A–F、P01–P21 與 UAT 對應測試
  RegistrationAdmin.IntegrationTests/  只對複製的測試試算表執行（未設定環境變數時自動略過）
docs/                               操作說明、開發者說明、Phase 0 決策紀錄、UAT 對照
scripts/                            build.ps1、publish.ps1、integration-test.ps1
```

## 快速開始（開發者）

1. 安裝 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）與 Visual Studio 2022 17.14 以上或 VS 2026（可開啟 `.slnx`）。
2. 在專案根目錄執行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
   ```

   會還原套件、建置並執行單元測試。第一次 restore 會產生各專案的 `packages.lock.json`，請一併提交以鎖定版本。
3. 執行程式：`dotnet run --project src/RegistrationAdmin.App`，或在 Visual Studio 將 `RegistrationAdmin.App` 設為啟動專案。
4. 產出未簽章本機測試安裝程式：`powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -NoUpload` → `artifacts\installer\RegistrationAdminApp-win-Setup.exe`（Velopack；免管理員權限安裝、開始功能表與桌面捷徑、可從「應用程式」解除安裝、自動更新）。正式發佈須選擇簽章供應者，設定與驗證流程見 [docs/release-signing.md](docs/release-signing.md)。更新來源設定在 `config\update-feed.local.txt`（不進版控；範本見 `config\update-feed.txt`）。

每次打包也會產生 `artifacts\交付安裝包\<版本>\`，內含安裝程式、使用手冊 PDF、安裝說明與 SHA256 校驗碼。將整個資料夾複製給新使用者即可；GitHub 繼續發佈 Release，供已安裝的程式線上更新。資料夾交付不會改變安裝檔的簽章或免除 Windows 安全提示。整理已有的安裝檔可執行 `powershell -ExecutionPolicy Bypass -File .\scripts\prepare-handoff.ps1 -Version 0.2.3`，不會重新打包或上傳。

`config` 資料夾只放範本：請自行建立 Google Cloud 的 Desktop app OAuth 用戶端，下載 JSON 另存為 `config\google-oauth-client.json`（格式見 `config\google-oauth-client.example.json`；此檔已列入 `.gitignore`，不會提交）。

第一次使用的 Google Cloud 設定與操作流程見 [docs/操作說明.md](docs/操作說明.md)；維護人員的部署教學見 [docs/manual/部署手冊.md](docs/manual/部署手冊.md)，行政人員的逐畫面使用手冊（含截圖）見 [docs/manual/使用手冊.md](docs/manual/使用手冊.md)，PDF 版在同一資料夾；安裝程式只附使用手冊。

## 實作對照（規劃章節）

| 規劃 | 實作位置 |
|---|---|
| 7.2–7.6 管理分頁 | `Core/Domain/*`、`GoogleSheets/SchemaManager.cs` |
| 8.1 source_key／fingerprint | `Core/Identity/SourceIdentity.cs`、`Core/Sync/SourceParser.cs` |
| 8.2 重新整理流程 | `Core/UseCases/RegistrationWorkspace.RefreshAsync`、`Core/Sync/SyncMerger.cs` |
| 8.3 儲存與 row_version | `RegistrationWorkspace.SaveAsync`、`GoogleSheetsRegistrationStore.UpdateAdminRowsAsync` |
| 9 狀態與業務規則 | `Core/Rules/*` |
| 9.4 重複提示 | `Core/Issues/IssueDetector.cs` |
| 10 Windows 畫面 | `App/Views/*`、`App/ViewModels/*` |
| 11 匯出 | `Export/*` |
| 12 日誌遮蔽 | `Core/Diagnostics/Redactor.cs`、`App/Infrastructure/AppInfrastructure.cs` |

## 已知限制

- 單一主要編輯者：Sheets API 沒有原子 compare-and-swap，row_version 只能偵測多數衝突。
- 不做背景輪詢、離線編輯、寄信、簽到、金流或發票開立。
- 未簽章測試包可能觸發 SmartScreen；正式版需組織的程式碼簽章憑證。
- 預設欄位映射取自 2026-10-02 的表單 PDF；若實際回應工作表標題不同，健康檢查會停止同步並列出缺少的欄位，請修改試算表上的 `_FieldMap`。
