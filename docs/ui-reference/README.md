# RegistrationAdmin UI 參考素材

這份資料夾只提供介面改版參考，不修改既有資料流程、Google Sheets 結構或業務規則。

## 建議採用的方向

整體建議是「Microsoft Lists 的清楚名單 + Airtable Record Review 的左右分欄 + Windows Fluent 的原生操作感」。

- 一般使用者只看到：待處理、全部名單、匯出。
- 只有在「設定 → 進階」才看到 Google OAuth、Spreadsheet、來源工作表、欄位對應與診斷。
- 一般畫面不用 `_Admin`、`_FieldMap`、`source_key`、`fingerprint`、`row_version` 等技術詞。
- 每個畫面只保留一個最明顯的主要動作。

## 外部產品畫面參考

### 1. Microsoft Lists：名單主畫面

適合參考：

- 搜尋、篩選、排序都在名單上方。
- 表格只顯示判斷工作真正需要的欄位。
- 狀態使用「文字 + 色彩」標籤，不能只靠顏色。
- 詳細資訊在選取一筆後再展開，不把所有欄位塞進主表格。

官方資料：

- [Microsoft Lists 介紹](https://support.microsoft.com/en-us/sharepoint/lists/data-and-lists/what-is-a-list-in-microsoft-365)
- [List、Gallery 與篩選方式](https://support.microsoft.com/en-us/teams/platform/view-your-team-s-lists)

畫面示例：

![Microsoft Lists Issue Tracker](https://intranet.ai/media/6294/microsoft-lists-issue-tracker.jpg)

不要照搬：工具列塞滿分享、自動化、Power Apps 等本系統不需要的功能。

### 2. Airtable Interface Designer：一般介面與資料底層分開

這是本案最重要的概念參考。Airtable 明確把複雜資料底層與一般使用者介面分開；一般使用者只看到完成工作所需的資料和動作。

適合參考：

- 左側是待處理名單，右側是選取項目的詳細資料。
- 可先用篩選顯示「需要我處理」的紀錄。
- 詳細頁只開放少數後台欄位編輯；原始答案保持唯讀。
- 工程設定不混入日常操作畫面。

官方資料：

- [Airtable Interface Designer 入門](https://support.airtable.com/articles/8078126534-Getting-started-with-Airtable-Interface-Designer)
- [Record Review 介面案例](https://www.airtable.com/guides/collaborate/interface-designer-record-review)

畫面示例：

![Airtable Record Review](https://embed-ssl.wistia.com/deliveries/dc5192f9086bb2e77d0be31f3bad36b79fded60b.jpg?image_crop_resized=640x480)

不要照搬：不要加入可自由拖拉的介面設計器；本案只需要固定且一致的畫面。

### 3. Eventbrite Manage Orders：活動報名工作流

適合參考：

- 先以姓名、Email 或編號搜尋。
- 以狀態和日期快速篩選。
- 單筆資料集中顯示動作，不在每個儲存格都放按鈕。
- 匯出是清楚的次要動作。

官方資料：

- [Eventbrite：管理單一活動訂單](https://www.eventbrite.com/help/en-us/articles/855993/how-to-customize-and-export-an-orders-report/)

畫面示例：

![Eventbrite Orders Dashboard](https://rebeltoolkit.extinctionrebellion.uk/uploads/images/gallery/2023-10/scaled-1680-/eventbrite-13.png)

不要照搬：本系統沒有票券、退款、入場簽到，不要把 Eventbrite 的完整活動功能帶進來。

### 4. Windows Fluent／WinUI：Windows 應用程式骨架

目前專案是 WPF，不必為了外觀改寫成 WinUI；只要在 WPF 重現 Fluent 的層次、間距、導航與狀態元件即可。

適合參考：

- 穩定且淺層的左側導航。
- List/Details（名單／詳細資料）模式。
- 不把所有命令重複放在多個地方。
- 窄視窗隱藏次要資料，而不是把所有元素一起縮小。

官方資料：

- [Windows NavigationView](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview)
- [Windows 導航基礎與 List/Details 模式](https://learn.microsoft.com/en-us/windows/apps/design/basics/navigation-basics)
- [商務型 Windows 應用程式設計](https://learn.microsoft.com/en-us/windows/apps/get-started/line-of-business/design-for-lob)

### 5. GOV.UK：防呆、錯誤與文案

這不是視覺風格範本，而是「讓不熟電腦的人也不容易出錯」的行為範本。

適合參考：

- 一次只要求使用者完成一個明確決定。
- 接受電話、統編等合理格式差異，再由程式標準化。
- 錯誤訊息直接說明「現在要怎麼修正」。
- 儲存失敗時保留使用者已輸入的內容。
- 幫助文字保持短，進一步說明放在可展開區塊。

官方資料：

- [Designing good questions](https://www.gov.uk/service-manual/design/designing-good-questions)
- [Recover from validation errors](https://design-system.service.gov.uk/patterns/validation/)

## 本資料夾的三張草圖

- `01-general-mode.svg`：一般使用者的主名單與待辦。
- `02-record-detail.svg`：單筆報名的審核／付款處理畫面。
- `03-advanced-settings.svg`：工程設定與診斷集中區。

草圖是資訊架構參考，不要求逐像素照抄。實作時優先沿用 Windows 系統字型、鍵盤操作、焦點框、對比模式與現有 WPF 技術。

## 最重要的產品決策

1. 預設開啟「一般模式」，不是儀表板堆滿技術資訊。
2. 一般模式左側最多 4 個入口：`待處理`、`全部名單`、`匯出`、`設定`。
3. `待處理`是預設首頁，直接列出會員未核對、資格未判定、付款待處理、錯誤與疑似重複。
4. 點選一筆後開啟詳細頁；原始表單答案使用唯讀樣式，後台欄位使用可編輯樣式。
5. `設定`預設只顯示連線狀態與重新連線；技術欄位放進「進階（管理員）」並在進入時提示。
6. 危險或不可逆操作必須二次確認；一般篩選、檢視與返回不需要確認。
7. 不用圖示代替重要文字；圖示只能輔助。
8. 色彩不是唯一訊號，狀態標籤必須帶文字。

