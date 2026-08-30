# 設計:案件名稱資料夾命名 + 套用測試案件不跳步 + 授權編製人員預覽

> 狀態:已實作並通過測試（750 綠,含 SQL Server parity;final Opus 複審 READY-TO-MERGE）。#2 不跳步屬純前端,待使用者 GUI 人工驗收。
> 日期:2026-06-22
> 範圍:三項收尾修改,皆遵守 JET 架構鐵律(契約先行、前端零商業邏輯、business logic 不外移、provider 分支僅在 Infrastructure、雙 provider 等價)。

## 背景與目標

使用者實機操作後提出三項收尾修改:

1. **案件名稱資料夾命名**:`projects/` 底下的專案資料夾目前以雜湊(32-hex GUID)命名,難辨識。建立案件流程要新增「案件名稱」欄位,且專案資料夾以案件名稱命名。
2. **「套用測試案件」不自動跳步**:點擊後只填當前區塊資料,不自動跳到下一個流程(例如在「建立案件」套用後停在建立案件,不跳到「匯入資料」)。
3. **授權編製人員清單預覽**:「匯入資料」區塊除既有的科目配對預覽外,新增授權編製人員清單預覽。

## 全域約束(每個 task 隱含適用)

- **契約先行**:凡動 action wire shape,先改 `docs/action-contract-manifest.md` 再實作;`ActionContractTests` 鎖回應形狀。
- **前端零商業邏輯**:HTML/JS 不組 SQL、不做驗證/計算;`postMessage` 僅在 `jet-api.js`。
- **provider 分支僅在 Infrastructure**:Application/Domain 不得出現 sqlite/sqlServer 條件;新查詢一律 Sqlite + SqlServer + ProviderRouting 三實作,配 `[SqlServerFact]` parity 測試。
- **TDD**:規則/驗證類先寫紅燈測試再實作。
- **雙 provider 等價**:任何新增查詢在兩引擎結果一致。
- **既有專案零遷移**:`projects/` 內既有雜湊資料夾必須照常開啟(使用者明確要求)。

---

## Finding 1:案件名稱作為資料夾名

### 使用者已拍板的三項決策

| 決策 | 選擇 |
|:---|:---|
| 命名來源 | **新增「案件名稱」欄位**(與案件編號、客戶名稱並列) |
| 唯一性策略 | **資料夾完全等於案件名稱**(建立時擋非法字元 + 拒絕重複) |
| 既有專案 | **維持原狀、照常開啟**(只有新案件用可讀名稱) |

### 核心模型:`projectId` 即案件名稱

目前 `projectId`(32-hex GUID)三位一體:**DB 鍵**、**資料夾名**(`{root}/{projectId}`)、**SQL Server 庫名來源**(`JET_{淨化後 projectId}`),並由 `IsValidProjectId`(`^[0-9a-f]{32}$`)兼任 **path-traversal 守衛**。

決策「資料夾完全等於名稱」下,最小且最不易出錯的做法是讓 **`projectId` 直接等於案件名稱**,維持既有不變式「資料夾名 == projectId == DB 鍵」。

> 否決的替代方案:保留 GUID 當 `projectId`、資料夾另用案件名稱、存 name↔id 對照。這會把 `JetProjectFolder` 從純函式(`GetProjectDirectory(id) = root/id`)改成需掃描查找,牽動 `GetProjectDirectory`/`GetDatabasePath`/`GetProjectJsonPath`/`EnumerateProjectIds` 全部,複雜度與出錯面遠高於本方案。

**取捨(使用者已接受)**:案件名稱即 id ⇒ 建立後不可改名(目前無改名功能)、且須唯一。這正是「完全等於名稱 + 拒絕重複」的直接結果。

### 案件名稱驗證規則(建立時 + id 守衛共用)

定義單一驗證器(取代 `JetProjectFolder.IsValidProjectId` 的 32-hex 規則):

- **長度**:1–100 字元(去前後空白後)。
- **允許字元**:Unicode 文字(`\p{L}`,含中日韓)、數字(`\p{N}`)、空白、`_`、`-`、半形 `()`、全形 `（）`。
- **拒絕字元**:`/ \ : * ? " < > |` 與 `.`(句點)及控制字元。**禁用 `.` 即天然杜絕 `..` 路徑穿越**,亦免去「結尾句點」的 Windows 陷阱。
- **拒絕**:前導或結尾空白;Windows 保留名(不分大小寫):`CON`、`PRN`、`AUX`、`NUL`、`COM1`–`COM9`、`LPT1`–`LPT9`。
- **空白**:`null`/全空白 → 無效。

**遷移免費的關鍵**:上述字元集是舊 32-hex GUID 的**超集**(hex 字元都是英數、長度 32 ≤ 100、不撞保留名),故既有專案的 `projectId` 仍然有效、`EnumerateProjectIds` 仍會列出、`GetProjectDirectory` 仍放行 → 舊雜湊專案照常開啟,零遷移。

**安全性**:`GetProjectDirectory` 仍先驗證再 `Path.Combine(RootPath, projectId)`;拒絕 `. / \ :` 使其無法形成相對上溯或絕對路徑。這是本項最敏感處,配完整正/負向單元測試。

### 唯一性把關落點

`JsonFileProjectStore.CreateAsync` 以 `Directory.CreateDirectory`(同名靜默覆寫)+ `WriteAsync`,本身不擋重複。故唯一性檢查放在 **`ProjectCreateHandler`(Application 層,provider 無關)**:建立前以 `projectStore.FindAsync(caseName)` 查詢,命中即丟 `JetActionException`(訊息:「案件名稱『X』已存在,請換一個。」)。

### 契約變更(契約先行)

`project.create` payload 新增**選填** `caseName`(string)。**提供時**:作為 `projectId`/資料夾名(經驗證 + 唯一性檢查);**未提供時**:回退 `Guid.NewGuid().ToString("N")`,維持既有程式化/測試建立行為(既有測試免改)。**UI 建立表單強制必填**(前端 `required`),故使用者路徑一律有可讀名稱。回應仍為 `{ projectId, ok }`。先改 `docs/action-contract-manifest.md`,`ActionContractTests` 同步。

> 設計取捨:選填 + GUID 回退是刻意決定。專案的 `project.create` payload 在約 15 處測試各自內嵌組裝(無中央 helper);強制必填會churn 全部且無使用者效益(測試專案用 GUID 資料夾無妨)。選填把「必填」落在真正重要的 UI 層,契約向後相容。

`ProjectCreateHandler.HandleAsync`:
- 讀**選填** `caseName`;有值則 `Trim`、跑驗證器(失敗丟 `InvalidPayload`)、唯一性檢查(見上),以其作為 `ProjectId`;無值則 `ProjectId = Guid.NewGuid().ToString("N")`。其餘欄位不變。

`ProjectDocument` 結構不變(不新增欄位):`ProjectId` 承載案件名稱。UI 將 `projectId` 以「案件名稱」標籤呈現;`projectCode`(案件編號)維持獨立欄位。

### SQL Server 庫名

`SqlServerProjectDatabase.DatabaseName` 沿用現有 `JET_{只保留英數底線}` derivation(**不改**,以免既有 SQL Server 專案的 `JET_{guid}` 庫對不上)。CJK 字元 `char.IsLetterOrDigit` 為真會保留,庫名以 `[...]` 內嵌已安全。

**極端情境防護(具體機制)**:兩個不同案件名稱淨化後可能撞同一庫名(如「A 公司」與「A公司」皆 → `JET_A公司`,因淨化會去掉空白),會讓第二案沉默共用第一案的 SQL Server 庫(資料汙染)。防護:`IProjectDatabaseInitializer` 新增 `Task<bool> DatabaseExistsAsync(string projectId, CancellationToken)`——SQL Server 查 `DB_ID(N'JET_{...}') IS NOT NULL`,SQLite 查 `File.Exists(jet.db)`(routing 委派)。`ProjectCreateHandler` 在 `FindAsync` 唯一性檢查**之後**再 gate:`DatabaseExistsAsync` 為真則擋下並提示換名。對 SQLite 此時 DB 檔在尚不存在的資料夾內 → 恆 false(無害的防禦縱深);對 SQL Server 則攔下淨化碰撞。

### Demo 專案命名

`DemoHandlers` 的 `project.loadDemo` 回傳 metadata 新增 `caseName`,來源 `DemoDataFactory` 新增常數(例如 `DemoCaseName = "範例測試案件"`)。`DemoProjectData` 增 `CaseName` 欄位。

**已知後果(使用者已被告知)**:demo 案件名稱固定 ⇒「套用測試案件(建立)」重複點擊,第二次會因同名被「已存在」擋下(與一般使用者建立同名的行為一致)。要重跑須先刪除舊 demo 專案。本設計刻意不對 demo 加唯一後綴(那會違反使用者選的「完全等於名稱」)。

### Blast radius(實作時逐檔對齊,非湊綠)

- `JetProjectFolder`:`IsValidProjectId` 規則改寫;相關單元測試(接受合法案件名稱 + 舊 32-hex;拒絕 `..`/路徑分隔/保留名/空白/超長/前後空白)。
- `ProjectCreateHandler` 及其測試:payload 改 `caseName`、唯一性/驗證錯誤路徑。
- 既有測試呼叫 `project.create` **不提供 `caseName`** → 回退 GUID → 行為與今相同,**免改**(約 15 處內嵌 payload 不動)。`DemoProjectPipeline` 等 helper 同理(維持每測試隔離,避免共用 instance 上同名 SQL Server 庫碰撞)。僅**新增** caseName 行為測試。
- `DemoDataFactory`/`DemoHandlers`/`DemoProjectPipeline`/前端 mock:demo 專案補 `caseName`。

### Finding 1 測試矩陣

- 驗證器:合法(中文名、含空白/`-`/`_`/括號、舊 32-hex);非法(`含/斜線`、`a..b`、`C:x`、`空字串`、`  前後空白 `、`CON`、101 字元、含 `*?"<>|`)。
- `project.create`:正常建立(projectId==caseName、資料夾名==caseName);缺 `caseName` → InvalidPayload;非法 caseName → InvalidPayload;同名重複 → 明確錯誤;sqlServer 同庫名衝突 → 擋下。
- 既有專案相容:以 32-hex id 建立的專案 `project.load`/`list`/`delete` 仍正常。
- 路徑安全:`GetProjectDirectory` 對含 `..`/分隔字元的 id 丟例外。

---

## Finding 2:「套用測試案件」不自動跳步

### 根因

`create-step.js` 的 `mockCreateProject` 最後呼叫 `Ui.applyLoadedProject(loaded)`,而 `applyLoadedProject`(`ui-core.js`)依 `data.project.currentStep`(新建專案 = 1 = 匯入)設定步驟索引,故停在建立案件時點「套用測試案件」會被帶到「匯入資料」。

### 設計

- `applyLoadedProject(data, options)` 新增可選 `options.stayOnCurrentStep`(預設 false → 維持現行依 currentStep 導覽的行為)。為 true 時:照常套用所有狀態(專案、匯入態、配對、規則結果、情境),但**不改變目前步驟索引**。
- `create-step.js` 的 `mockCreateProject` 呼叫 `applyLoadedProject(loaded, { stayOnCurrentStep: true })`,套用後停在「建立案件」(顯示已建立摘要)。
- **不動**:正式「建立案件」表單送出(line 116–123,仍導覽到匯入,屬正常建立後前進);從專案清單開啟舊案的 resume(line 310,仍 resume 到保存步驟)。
- **逐一檢視其餘 demo loader**:`import`/`mapping`/`validate`/`filter` 的 mock loader 不得自動前進。現況看來它們只 `setState`+`addMessage` 不導覽(`mockImportData` 不呼叫 `gotoStep`/`applyLoadedProject`),實作時逐一確認;若有導覽則一併移除。

### 驗證

純前端 GUI 行為,依 jet-testing 硬邊界不做 WinForms/WebView2 E2E 自動化。以 `/verify` 或使用者手動確認:點各步「套用測試案件」後停在當前步、資料正確填入、不前進。

---

## Finding 3:授權編製人員清單預覽

鏡射既有科目配對預覽(`accountMappings`)。授權名單存於 `target_authorized_preparer`(單欄 `name`)。

### 後端(契約先行 + 雙 provider)

- **契約**:`docs/action-contract-manifest.md` 的 `query.dataPreview` 允許 dataset 新增 `authorizedPreparers`。
- **Domain**:`DataPreviewDataset` enum 加 `AuthorizedPreparers`;`DataPreviewDatasetNames.TryParse` 加 `"authorizedPreparers"` 分支;`QueryDataPreviewHandler` 錯誤訊息允許值列表補上。
- **Infrastructure**:`SqliteDataPreviewRepository` 與 `SqlServerDataPreviewRepository` 的 `dataset switch` 各加 `AuthorizedPreparers => await AuthorizedPreparersPreviewAsync(connection, limit, ct)`;查詢 `SELECT name FROM target_authorized_preparer ORDER BY name`(SQLite `LIMIT @limit` / SQL Server `OFFSET 0 ROWS FETCH NEXT @limit`)+ `COUNT` 總數;回 `DataPreviewResult`(columns=`["preparerName"]`,rows=names,無 stats),鏡射 `AccountMappingsPreviewAsync`。`ProviderRoutingDataPreviewRepository` 無須改(已委派)。
- **column id**:回 `preparerName`,前端標籤對應「姓名」。

### 前端(零邏輯)

- `data-preview.js`:`DATASETS` 加 `{ value: 'authorizedPreparers', label: '授權編製人員清單' }`;`COLUMN_LABELS` 加 `preparerName: '姓名'`;`emptyHint` 加 `authorizedPreparers` 文案(「尚未匯入授權編製人員清單…」)。
- `import-step.js`:授權編製人員卡片(已匯入狀態為門檻)加「預覽」鈕 `data-action="preview-authorized-preparer"`,綁定 `Ui.openDataPreview('authorizedPreparers')`,鏡射既有 `preview-account-mapping`。

### Finding 3 測試

- `QueryDataPreviewHandler`/Domain:`TryParse("authorizedPreparers")` 成功;handler 接受該 dataset。
- Infrastructure:匯入授權名單後 `GetPreviewAsync(AuthorizedPreparers)` 回正確列數與總數、排序;空表回 totalCount 0。SQLite + SQL Server `[SqlServerFact]` parity。
- `ActionContractTests`:`query.dataPreview` 允許 dataset 含 authorizedPreparers。

---

## 風險與待辦

- **最高風險:`IsValidProjectId` 改寫**(安全閘)。以完整負向測試覆蓋路徑穿越;字元集為白名單(預設拒絕),`.`/分隔字元/`:` 一律擋。
- **SQL Server 庫名淨化碰撞**:以建立前目標庫存在性檢查緩解(見 F1)。
- **demo 重複建立**:固定 demo 名稱重複套用會被擋,屬「完全等於名稱」的預期後果,已告知使用者。
- 三項彼此獨立,可分 task 平行設計,但同一輪實作、同一輪測試、同一次提交。
