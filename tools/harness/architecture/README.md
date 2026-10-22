# 架構圖表的封裝與檢查

這裡只放文件工具。閱讀方式見[架構文件](../../../docs/architecture/README.md)，agent 的完整流程見
[分析與繪圖](../../../.agents/harness/architecture-documentation.md)。

## 封裝圖表

`package-redraw.py` 只用 Python 標準函式庫，把依某一份程式快照重繪的圖表封裝成 `docs/architecture/diagrams/`。這是唯一的圖表目錄，
每次重繪都直接覆寫，不另開版本；快照由 `--run` 指定研究目錄 `runs/` 下的快照目錄，重繪日期由 `--redraw-date` 寫進頁面與 `manifest.json`：

~~~powershell
python tools/harness/architecture/package-redraw.py --research-root <研究目錄> --run <快照目錄名稱> --redraw <重繪工作目錄> --output docs/architecture/diagrams --redraw-date <重繪日期>
~~~

快照目錄裡有程式快照 `snapshot/`，以及 `evidence/` 下的來源清單、Roslyn 紀錄、已核對事實、資料表結構、方法簽章與代表呼叫對照。
2026-10-16 的研究快照把 Roslyn 紀錄與代表呼叫對照放在研究目錄最上層的 `evidence/`，腳本找不到快照目錄自己的那一份時才讀那裡。
重繪工作目錄放在 Git 忽略的 `artifacts/` 下，內含各圖的候選 JSON 與 finalize 紀錄、LikeC4 模型與離線輸出，以及
`evidence-spec.json`。這份規格列出每條連線依據的呼叫、實作、AppCompositionRoot 的建立位置或事實，類別圖上每個成員的宣告位置，
以及重繪時另外補充、快照目錄還沒有的事實。腳本核對來源檔雜湊，再產生連線證據、四張類別圖的關係證據、成員宣告位置、來源摘錄、閱讀頁面與檔案清單。

腳本先在重繪工作目錄裡組好整個目錄，全部檢查通過後，才刪除 `docs/architecture/` 下原有的圖表目錄並放入新目錄；前一版留在 Git 歷史。
輸出目錄已存在但不是圖表目錄時會停止，不覆寫。下列情況也會直接停止，輸出目錄維持原樣：找不到某條連線的 Roslyn 證據、
圖上線型和規格的證據種類不符、圖上有線沒有寫進規格、回傳沒有對應方向相反的呼叫、類別圖畫出的成員和規格列出的宣告位置不一致、
最新一次 finalize 沒有通過，或輸出中留有本機絕對路徑。LikeC4 的單檔 HTML 會把本機連結改成包內相對連結，並內嵌網頁圖示；模型與版面不改。

配色也在封裝時處理。Archify 圖使用 editorial 預設風格並通過 finalize 之後，腳本把重繪工作目錄的 `archify-theme.css`
插入每張圖，只換顏色變數、標題字型與裝飾；`manifest.json` 的 `finalizedHtmlSha256` 保留插入前的雜湊。LikeC4 的方框與連線顏色寫在
`likec4/likec4.config.json`，由 LikeC4 建置時套用；畫布底色寫在 `likec4/jet-theme.css`，由腳本插入匯出的 HTML，
因為 LikeC4 1.59.4 的 customCss 設定在本機試用時無法註冊。三個配色檔都會複製進圖表目錄。
不讀取使用者案件，不複製工具安裝、索引資料庫或整份程式快照。

研究快照沒有放進儲存庫，所以重新 clone 後可以閱讀與重繪已收錄的模型，但不能只用這個封裝腳本重跑原研究。
更新到新版程式時，依 agent 流程另建快照目錄與分析結果，不聲稱一鍵重建全部證據；2026-10-22 依提交 `07b97b3` 建的快照目錄附有取快照、
重抽資料表結構與方法簽章、重做代表呼叫對照，以及重新核對事實的腳本，下一次可以照用。

## 正式檢查

執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Documentation`。
`JetDocumentationCheck.ps1` 依政策檔 `architectureManifest` 指定的清單載入 `Check-Architecture.ps1`，並執行 30 個小型合成案例。
一般文件檢查不需要 Python、RepoWise、Roslyn、LikeC4 或 Archify。

檢查內容：

- 封裝檔案與圖表的 SHA-256，防止模型與已交付圖表被不同步改動。
- 來源摘錄所記錄的雜湊與來源清單一致；這不等於重新取得或編譯原始快照。
- 類別關係的端點及種類與保存的 Roslyn 證據一致，依賴線上寫的方法也要出現在 Roslyn 證據裡。
- 每張 Archify 圖與 LikeC4 模型的每條線都有連線證據，證據也都對應到圖上存在的線。
- 循序圖的線型與 LikeC4 的關係種類，要和證據種類一致。直接呼叫只接受具體方法，介面呼叫只接受介面方法；
  畫出實作類別時要同時有實作紀錄與 AppCompositionRoot 的建立位置。
- 回傳對應到同一張圖裡方向相反的呼叫；事實引用存在，事實的來源雜湊相符。
- 類別圖上每個成員都有一筆宣告位置與來源摘錄，不重複，也沒有圖上不存在的成員。
- 固定快照未觀察執行期行為，不能把關係改標為一定執行或已觀察。
- HTML、JSON、Markdown 與模型檔不含本機使用者目錄的絕對路徑。

`architecture-evidence.tests.ps1` 包含正常依賴、實作、繼承與各種連線，以及錯誤種類、錯誤目標、缺少證據、
缺少執行狀態、無證據的執行宣稱、方法不在證據裡、缺少組裝證據與不認得的證據種類，
以及成員少了宣告位置、重複列出、列出圖上沒有的成員與摘錄不存在。資料均為獨立的小型合成符號，不啟動 JET。

圖表自帶的檢查記錄仍有原來的用途。這項檢查不取代 LikeC4 模型檢查、Archify finalize、
實際看圖或業務流程驗證，也不證明業務結果正確。
