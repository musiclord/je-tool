# 設計：案件名稱資料夾命名、套用測試案件不跳步與授權編製人員預覽

這份是 2026-11-01 壓縮後的摘要。使用者原話、題目與回答、日期、數字與裁定照原文保留；實作經過、驗證收據與重跑過程壓成結果。
壓縮前的全文在提交 `85af454`，可用 `git show 85af454:docs/history/specs/2026-06-22-project-naming-and-ux-fixes-design.md` 取回。

## 狀態

日期：2026-06-22。原文狀態是已實作並通過測試（750 項通過，含 SQL Server 等價測試；最後的複審判定可以合併）。
第 2 項不跳步屬純前端，待使用者 GUI 人工驗收。三項都遵守專案架構規則：契約先行、前端沒有商業邏輯、provider 分支只在 Infrastructure、兩種資料庫等價。

## 使用者提出的三項修改

使用者實機操作後提出：

1. **案件名稱資料夾命名**：`projects/` 底下的專案資料夾目前以雜湊（32-hex GUID）命名，難辨識。建立案件流程要新增「案件名稱」欄位，且專案資料夾以案件名稱命名。
2. **「套用測試案件」不自動跳步**：點擊後只填當前區塊資料，不自動跳到下一個流程（例如在「建立案件」套用後停在建立案件，不跳到「匯入資料」）。
3. **授權編製人員清單預覽**：「匯入資料」區塊除既有的科目配對預覽外，新增授權編製人員清單預覽。

另一條約束是**既有專案零遷移**：`projects/` 內既有雜湊資料夾必須照常開啟（使用者明確要求）。

## 第 1 項：案件名稱作為資料夾名

### 使用者已拍板的三項決策

| 決策 | 選擇 |
|:---|:---|
| 命名來源 | **新增「案件名稱」欄位**（與案件編號、客戶名稱並列） |
| 唯一性策略 | **資料夾完全等於案件名稱**（建立時擋非法字元 + 拒絕重複） |
| 既有專案 | **維持原狀、照常開啟**（只有新案件用可讀名稱） |

### 做法

- 原本的 `projectId`（32-hex GUID）同時是資料庫鍵、資料夾名（`{root}/{projectId}`）與 SQL Server 庫名來源（`JET_{淨化後 projectId}`），並由 `IsValidProjectId`（`^[0-9a-f]{32}$`）兼任路徑穿越守衛。改成讓 **`projectId` 直接等於案件名稱**，維持「資料夾名 == projectId == DB 鍵」。另存名稱與 id 對照的方案被否決，因為會讓 `JetProjectFolder` 從純函式變成需要掃描查找。
- 取捨（使用者已接受）：案件名稱就是 id，所以建立後不可改名（當時沒有改名功能），且必須唯一。
- 案件名稱驗證：去前後空白後 1–100 字元；允許 Unicode 文字（含中日韓）、數字、空白、`_`、`-`、半形 `()` 與全形 `（）`；拒絕 `/ \ : * ? " < > |`、`.` 與控制字元；拒絕前後空白、全空白，以及 Windows 保留名 `CON`、`PRN`、`AUX`、`NUL`、`COM1`–`COM9`、`LPT1`–`LPT9`（不分大小寫）。禁用 `.` 就杜絕了 `..` 路徑穿越，也免去結尾句點的問題。
- 這個字元集涵蓋舊 32-hex GUID，所以舊專案照常列出與開啟，不需遷移。
- 唯一性在 `ProjectCreateHandler` 檢查：建立前 `projectStore.FindAsync(caseName)`，已存在就回錯誤「案件名稱『X』已存在，請換一個。」。
- 契約：`project.create` payload 新增**選填** `caseName`。有值時當作 `projectId` 與資料夾名；沒有值時回退 `Guid.NewGuid().ToString("N")`，約 15 處測試的建立方式不用改。畫面上的建立表單強制必填。回應仍是 `{ projectId, ok }`，`ProjectDocument` 不加欄位。
- SQL Server 庫名沿用 `JET_{只保留英數底線}`，不改，以免舊庫對不上。兩個名稱淨化後可能撞同一庫名（例如「A 公司」與「A公司」），所以新增 `DatabaseExistsAsync`，在唯一性檢查之後再擋一次。
- demo 專案名稱用固定常數（例如 `DemoCaseName = "範例測試案件"`）。已知後果（使用者已被告知）：重複套用測試案件時，第二次會因同名被擋，要先刪除舊 demo 專案；刻意不加唯一後綴，因為那會違反「完全等於名稱」。

## 第 2 項：「套用測試案件」不自動跳步

根因是 `create-step.js` 的 `mockCreateProject` 最後呼叫 `Ui.applyLoadedProject(loaded)`，而它依 `currentStep`（新專案是 1，即匯入）設定步驟。

做法是讓 `applyLoadedProject(data, options)` 接受 `options.stayOnCurrentStep`（預設 false）；為 true 時照常套用全部狀態，但不改目前步驟。
`mockCreateProject` 改帶 `{ stayOnCurrentStep: true }`。正式建立案件後仍前進到匯入，從專案清單開啟舊案仍回到保存的步驟。
其他步驟的 mock loader 逐一確認不會自動前進。這是純前端行為，由人工確認。

## 第 3 項：授權編製人員清單預覽

照科目配對預覽的做法。`query.dataPreview` 的 dataset 新增 `authorizedPreparers`；兩種資料庫查詢 `SELECT name FROM target_authorized_preparer ORDER BY name` 加總數，
欄位 id 為 `preparerName`。前端資料預覽加「授權編製人員清單」，欄名對應「姓名」，空狀態提示「尚未匯入授權編製人員清單…」；
匯入卡在已匯入時加「預覽」按鈕。

## 風險

- 最高風險是改寫 `IsValidProjectId` 這個安全閘，以完整的負向測試覆蓋路徑穿越；字元集是白名單，預設拒絕。
- SQL Server 庫名淨化後碰撞，以建立前檢查目標庫是否存在來緩解。
- 固定 demo 名稱重複套用會被擋，是「完全等於名稱」的預期後果，已告知使用者。
