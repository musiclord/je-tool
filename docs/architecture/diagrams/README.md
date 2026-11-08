# 圖表閱讀說明

開啟[圖表入口](index.html)，從左側依用途選圖。一般讀者可先看「先認識 JET」、「看操作怎麼進行」與「看資料如何流動」。要核對程式時，再展開圖下方的「查閱這張圖的依據」。

## 圖表範圍

圖表依固定程式快照整理，不包含之後的修改。來源版本、工具版本與檔案清單保留在 [manifest.json](manifest.json)。連線來自程式碼分析，不是實際執行紀錄；現況以 src 與測試為準。部署與維護圖中的建議程序仍待公司確認。

## 線條怎麼讀

| 線條 | 意義 |
|:---|:---|
| 灰色實線 | 直接呼叫類別的方法。 |
| 鋼藍色虛線 | 透過介面呼叫；啟動時決定使用哪個實作類別。 |
| 琥珀色線 | 畫面訊息、預先交出的函式或逐列讀取等間接觸發。兩種工具的線型略有不同。 |

<details>
<summary>類別圖與資料表圖的補充</summary>

類別圖保留程式名稱與方法型別。成員省略參數名稱、CancellationToken 及預設參數；超過四個參數只列前兩個。package 表示 internal，實作類別不重複列出介面已有的成員。完整宣告在[類別與成員](types.html)。

資料表圖的線代表 SQL 使用的關聯欄位。這批 SQLite 定義沒有宣告外鍵，不會由資料庫強制保護這些關聯。PK 是主鍵，UK 是唯一約束或索引。未畫線不代表資料表互不相關；完整欄位在[資料表欄位](schema.html)。

</details>

## 修改圖表

LikeC4 來源是 [jet.c4](likec4/jet.c4)。Archify 來源是各圖資料夾的 candidate.json，例如[篩選情境圖](archify/filter-save/candidate.json)。修改來源後，依 `tools/harness/architecture/README.md` 重新產生與封裝。

第三方授權：[Archify](licenses/archify-LICENSE.txt)、[LikeC4](licenses/likec4-LICENSE.txt)。
