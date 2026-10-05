# RegistrationAdmin — 活動報名後台（Windows）

> **聲明**
> - 本軟體**免費使用**，依現況（as-is）提供，不提供任何明示或默示的保證。
> - 作者對於使用本軟體所造成的任何損失、資料錯誤或其他後果，**不負任何責任**。
> - 本專案的程式碼與文件**完全由 AI 開發**（Anthropic Claude，經 Claude Code）。使用前請自行審查與測試。
> - 授權條款：[MIT License](LICENSE)。
>
> *Free to use, provided as-is with no warranty; the author accepts no responsibility or liability. Developed entirely by AI. Licensed under MIT.*

一個以 Google Form 回應試算表為資料來源的活動報名後台 MVP 專案。
讀取 Google Form 連結的回應試算表，讓管理者在 Windows 程式中審核報名、核對會員、控管名額、記錄付款／發票，並匯出名單。

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
  RegistrationAdmin.Tests/          xUnit：規則、同步、匯出與畫面狀態的單元測試
  RegistrationAdmin.IntegrationTests/  只對複製的測試試算表執行（未設定環境變數時自動略過）
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
4. 產出安裝程式：`powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1` → `artifacts\installer\RegistrationAdminApp-win-Setup.exe`（Velopack；免管理員權限安裝、開始功能表與桌面捷徑、可從「應用程式」解除安裝、自動更新）。更新來源設定在 `config\update-feed.local.txt`（不進版控；範本見 `config\update-feed.txt`）。

## 基本使用

1. **Google Cloud（只做一次）**：建立專案並啟用 Google Sheets API；OAuth 同意畫面選「外部」；建立「電腦版應用程式（Desktop app）」OAuth 用戶端並下載 JSON，另存為 `config\google-oauth-client.json`（格式見 `config\google-oauth-client.example.json`，此檔不會提交）。
2. **連線**：開啟程式 →「設定 → 開始使用」，貼上 Google Form 回應試算表的網址，按「登入 Google 並連線」，用對該試算表有編輯權限的帳號登入。
3. **建立管理資料區**：程式會在同一份試算表新增 `_Config`、`_FieldMap`、`_Lookups`、`_Admin`、`_ChangeLog` 分頁；原始回應頁不會被修改。若欄位對不上，請修改 `_FieldMap` 的 `source_header`。
4. **日常**：「待處理」看需要處理的報名、點一筆審核／付款核帳後按「儲存變更」；「匯出」可輸出 Excel／CSV 名單。

## 已知限制

- 單一主要編輯者：Sheets API 沒有原子 compare-and-swap，row_version 只能偵測多數衝突。
- 不做背景輪詢、離線編輯、寄信、簽到、金流或發票開立。
- 未簽章測試包可能觸發 SmartScreen；正式版需組織的程式碼簽章憑證。
- 預設欄位映射取自 2026-10-02 的表單 PDF；若實際回應工作表標題不同，健康檢查會停止同步並列出缺少的欄位，請修改試算表上的 `_FieldMap`。
