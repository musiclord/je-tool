# 架構圖表的封裝與檢查

這裡只放文件工具。閱讀方式見[架構文件](../../../docs/architecture/README.md)，agent 的完整流程見
[分析與繪圖](../../../.agents/harness/architecture-documentation.md)。

## 封裝圖表

`package-redraw.py` 只用 Python 標準函式庫，把依固定快照重繪的圖表封裝成 `docs/architecture/` 下唯一的圖表目錄。目錄以重繪日期命名，目前是 `docs/architecture/2026-10-21/`：

~~~powershell
python tools/harness/architecture/package-redraw.py --research-root <研究目錄> --redraw <重繪工作目錄> --output docs/architecture/<重繪日期> --redraw-date <重繪日期>
~~~

重繪工作目錄放在 Git 忽略的 `artifacts/` 下，內含各圖的候選 JSON 與 finalize 紀錄、LikeC4 模型與離線輸出，以及
`evidence-spec.json`。這份規格列出新增的已核對事實，以及每條連線依據的呼叫、實作、組裝或事實。
腳本從研究目錄讀取 Roslyn 紀錄、固定快照的來源清單與原有的已核對事實，核對來源檔雜湊，
再產生連線證據、四張類別圖的關係證據、來源摘錄、閱讀頁面與檔案清單。

腳本先在重繪工作目錄裡組好整個目錄，全部檢查通過後，才刪除 `docs/architecture/` 下原有的圖表目錄並放入新目錄；前一版留在 Git 歷史。
目錄改名後，要同步更新政策檔 `documentation-check.json` 與文件裡的連結。輸出目錄已存在但不是圖表目錄時會停止，不覆寫。下列情況也會直接停止，輸出目錄維持原樣：找不到某條連線的 Roslyn 證據、
圖上線型和規格的證據種類不符、圖上有線沒有寫進規格、回傳沒有對應方向相反的呼叫、最新一次 finalize 沒有通過，
或輸出中留有本機絕對路徑。LikeC4 的單檔 HTML 會把本機連結改成包內相對連結，並內嵌網頁圖示；模型與版面不改。
不讀取使用者案件，不複製工具安裝、索引資料庫或整份程式快照。

研究快照沒有放進儲存庫，所以重新 clone 後可以閱讀與重繪已收錄的模型，但不能只用這個封裝腳本重跑原研究。
更新到新版程式時，依 agent 流程另建快照與分析結果，不聲稱一鍵重建全部證據。

## 正式檢查

執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Documentation`。
`JetDocumentationCheck.ps1` 依政策檔 `architectureManifest` 指定的清單載入 `Check-Architecture.ps1`，並執行 25 個小型合成案例。
一般文件檢查不需要 Python、RepoWise、Roslyn、LikeC4 或 Archify。

檢查內容：

- 封裝檔案與圖表的 SHA-256，防止模型與已交付圖表被不同步改動。
- 來源摘錄所記錄的雜湊與來源清單一致；這不等於重新取得或編譯原始快照。
- 類別關係的端點及種類與保存的 Roslyn 證據一致，依賴線上寫的方法也要出現在 Roslyn 證據裡。
- 每張 Archify 圖與 LikeC4 模型的每條線都有連線證據，證據也都對應到圖上存在的線。
- 循序圖的線型與 LikeC4 的關係種類，要和證據種類一致。直接呼叫只接受具體方法，介面呼叫只接受介面方法；
  畫出實作類別時要同時有實作紀錄與 AppCompositionRoot 的建立位置。
- 回傳對應到同一張圖裡方向相反的呼叫；事實引用存在，事實的來源雜湊相符。
- 固定快照未觀察執行期行為，不能把關係改標為一定執行或已觀察。
- HTML、JSON、Markdown 與模型檔不含本機使用者目錄的絕對路徑。

`architecture-evidence.tests.ps1` 包含正常依賴、實作、繼承與各種連線，以及錯誤種類、錯誤目標、缺少證據、
缺少執行狀態、無證據的執行宣稱、方法不在證據裡、缺少組裝證據與不認得的證據種類。資料均為獨立的小型合成符號，不啟動 JET。

圖表自帶的檢查記錄仍有原來的用途。這項檢查不取代 LikeC4 模型檢查、Archify finalize、
實際看圖或業務流程驗證，也不證明業務結果正確。
