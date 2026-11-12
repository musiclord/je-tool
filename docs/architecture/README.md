# JET 架構與流程圖

首次 SAT 的現行 SQLite 架構請看 [Technical Document](<../SAT/Technical Document.docx>) 第 6 節，資料表關聯圖與 UML 類別圖在 [Diagrams](../SAT/Diagrams/) 資料夾。

下列詳細圖表保留原程式快照的範圍，含有已暫時移除的其他資料庫，不代表本次正式版支援範圍。

開啟[圖表入口](diagrams/index.html)，即可在本機查看，不需要安裝繪圖工具或啟動 JET。

| 想了解什麼 | 從哪裡開始 |
|:---|:---|
| JET 做什麼，資料放在哪裡 | 入口的「先認識 JET」 |
| 匯入、篩選與底稿匯出怎麼進行 | 「看操作怎麼進行」與「看資料如何流動」 |
| 程式怎麼處理，資料表如何連接 | 「追查處理順序」、「查程式分工」與「查資料表」 |
| 電腦需要什麼，案件如何移轉 | 「準備部署與維護」 |

每張圖下方的「查閱這張圖的依據」可展開程式位置與摘錄。圖表範圍、線條意義與修改方式見[閱讀說明](diagrams/README.md)。完整文字規格見[系統規格](../jet-guide.md)。

## 維護

新增或重繪圖表前，先依[繪圖前的設計](../../.agents/harness/architecture-documentation.md#繪圖前的設計)決定讀者、問題、圖種與版面。
這份程序已整併 diagram-design 的設計方法，並保留 JET 的配色、證據與離線閱讀方式。

修改圖表來源後，依[分析與繪圖流程](../../.agents/harness/architecture-documentation.md)重新產生、檢視畫面，再執行文件檢查。
Archify 的安裝版本與封裝方式見[封裝與檢查工具](../../tools/harness/architecture/README.md)。圖表固定放在 `diagrams/`，前版保留在 Git 歷史。

目前仍缺完整分層資料流圖、所有 action 的追溯、其他資料庫結構，以及正式部署與維護手冊。DocFX 暫緩。
