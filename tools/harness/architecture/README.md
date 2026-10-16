# 架構圖表的封裝與檢查

這裡只放文件工具。閱讀方式見[架構文件](../../../docs/architecture/README.md)，agent 的完整流程見
[分析與繪圖](../../../.agents/harness/architecture-documentation.md)。

## 收錄既有研究

`package-diagrams.py` 使用 Python 標準函式庫，接受已審閱的研究目錄與新的輸出目錄：

~~~powershell
python tools/harness/architecture/package-diagrams.py --research-root <研究目錄> --output <新的圖表目錄>
~~~

來源是既有研究的固定目錄格式；這不是通用的程式分析器。輸出目錄已存在時會停止，
不覆寫原圖。它複製 Archify HTML、候選 JSON 與 LikeC4 模型。LikeC4 離線 HTML 的本機來源連結會改為相對連結，
再產生閱讀入口、來源摘錄和檔案清單。清單另外記錄 LikeC4 清理前後的雜湊及修改連結數。
不讀取使用者案件，不複製工具安裝、索引資料庫或整份程式快照。

研究快照沒有放進儲存庫，所以重新 clone 後可以閱讀與重繪已收錄的模型，但不能只用這個封裝腳本重跑原研究。
更新到新版程式時，依 agent 流程另建快照與分析結果，不聲稱一鍵重建全部證據。

## 正式檢查

執行 `pwsh -NoProfile -File tools/verify.ps1 -Command Documentation`。
`JetDocumentationCheck.ps1` 依政策載入 `Check-Architecture.ps1`，並執行八個小型合成案例。
一般文件檢查不需要 Python、RepoWise、Roslyn、LikeC4 或 Archify。

檢查範圍：

- 封裝檔案與原圖的 SHA-256，防止模型與已交付圖表被不同步改動。
- 來源摘錄所記錄的雜湊與來源清單一致；這不等於重新取得或編譯原始快照。
- 17 組類別關係的端點及種類與保存的 Roslyn 證據一致。
- 固定快照未觀察執行期行為，不能把關係改標為一定執行或已觀察。

`architecture-evidence.tests.ps1` 包含正常依賴、實作與繼承，以及錯誤種類、錯誤目標、缺少證據、
缺少執行狀態和無證據的執行宣稱。資料均為獨立的小型合成符號，不啟動 JET。

圖表自帶的檢查記錄仍有原來的用途。這項檢查不取代 LikeC4 模型檢查、Archify finalize、
實際看圖或業務流程驗證，也沒有將十八張圖的每條線全部納入。
