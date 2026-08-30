# SQL Server 企業環境延後範圍

更新日期：2026-08-30  
狀態：延後中；這是現行待辦的一部分，不是歷史紀錄

JET 目前以 SQLite／DuckDB 本機流程為優先。SQL Server 的資料存取與控制面已有自動測試，程式碼也保留
完整 provider 相容性，但真正的多人、公司帳號與企業政策只能在公司環境確認。這份文件保存已確認的
安全缺口與驗收範圍，讓企業線重啟時不必重新追查前因後果。

## 何時才需要重啟

同時具備以下條件後，再另立短期驗收計畫：

- 可連線的 SQL Server 2022。
- 至少兩個不同的 Windows／網域帳號。
- DBA 或具備查詢專案登錄、schema 與 audit log 權限的人員。
- 已核定的專案授權、AD 整合與管理員操作規則。

## 已確認押後的四個多人安全缺口

以下四項在 2026-08-01 確認仍未閉環，只記錄、不在本地優先開發輪推測實作。企業線必須先取得真實帳號、
SQL Server 與管理政策再設計及驗收：

1. **真實身分驗證**：目前 `system.whoAmI` 與 registry ACL 使用 host 端 `WindowsIdentity` 形成的
   client 自報 principal；共用 SQL 登入下，資料庫端尚未把連線身分與該 Windows／網域帳號做不可偽造的
   綁定。企業線需選定 AD／整合驗證或等價信任邊界，並以至少兩個真實帳號驗證授權與 audit attribution。
2. **serverOnly 刪除**：`project.delete` 目前先要求本機 `project.json`，只有 registry 條目而尚未物化
   本機快取的案件會回 `project_not_found`。企業線需定義從線上清單直接刪除的授權、鎖、確認與交易
   邊界，不能用前端偷偷先載入來繞過。
3. **noAccess metadata 隱藏**：持有他人案件本機快取時，`project.list` 可標示 `noAccess`，但本機文件的
   案件編號、實體名稱、期間等 metadata 仍可能出現在列上。企業線需裁決未授權使用者可見的最小欄位，
   並驗證清單、錯誤與本機殘留都不洩漏超出政策的資訊。
4. **SQL Server 2022 版本硬閘**：目前會明確拒絕 Express 並顯示產品版本，但尚未在所有 production
   進入點強制 `ProductMajorVersion >= 16`；較舊的非 Express 版本不是完整硬擋。企業線需在連線與建案前
   建立 SQL Server 2022（16.x）一致的 fail-closed gate，並驗證 Developer／Standard／Enterprise 的
   允許矩陣。

## 重啟時要驗收的操作面

**多人共用案件**

- 使用者 A 建立的線上案件，已授權的使用者 B 可以在清單看到。
- 刪除案件後，所有使用者的清單、專案 schema 與登錄資料都一致消失。
- 建立與刪除動作可在 audit log 查到操作者與電腦。

**同一案件的使用鎖**

- 使用者 A 開啟案件後，使用者 B 不能同時修改，且看得到由誰使用。
- A 正常離開後，B 可以立即接手；A 異常關閉後，鎖逾時才允許 B 接手。
- A 停留在案件內或切換步驟時，鎖不會誤釋放。

**後端容量資訊**

- 系統可以顯示 SQL Server 資料庫大小與線上案件數，案件數隨建立或刪除正確增減。
- 容量接近門檻時顯示提醒；正常範圍不誤報。

**audit log 的企業面**

- 本機案件的最小 audit log 已落地（schema v9 起由資料庫層強制 append-only；DuckDB 因引擎沒有
  trigger 維持程式紀律）。查詢介面、匯出與保留政策屬於企業線範圍，本地輪不做。
- SQLite／DuckDB 的刪案留痕已於 2026-08-20 裁決不做：本地案件屬個人工作性質，持久刪案證據是
  中心化管理議題；SQL Server 既有的刪案紀錄維持。

## 不在範圍內

- 尚未核定的專案授權名單。
- Active Directory 的最終整合方式。
- 管理員強制釋放他人鎖的規則。
- 企業 CFA、DLP、備份與憑證政策。

這些項目需要新的公司環境決策，不能依舊設計草稿直接實作或宣稱已驗收。重啟時依當時程式碼另寫短期
驗收計畫，完成後把結果回寫 [`development-status.md`](development-status.md)。live SQL Server 的
相容性驗證入口是 `tools/verify.ps1` 的 `Provider` 命令，條件見 [`harness.md`](harness.md)。
