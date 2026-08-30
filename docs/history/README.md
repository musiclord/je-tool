# 歷史文件

這個目錄保存 `je-tool` 成立之前的開發脈絡，涵蓋 2026-06-04 到 2026-08-26。內容能解釋設計為什麼長成
現在這樣、哪些選擇被試過又放棄，但它**不描述 `je-tool` 現在的行為或驗證狀態**。

先前這些檔案依來源專案分成兩個目錄。目前已依時間軸重組，因為開發是連續的一條線，來源專案的名稱
對後續開發沒有意義。原本兩個來源在 2026-06-20 交接，交接前後的 Git tree 相同；詳細關係見
[`../repository-lineage.md`](../repository-lineage.md)。

## 目錄

| 位置 | 內容 |
|:---|:---|
| [`development-log.md`](development-log.md) | 合併後的開發紀錄，依日期排列，新的在上。共 81 個條目 |
| [`specs/`](specs/) | 當時的設計書與驗證證據，檔名以日期開頭。7 份設計書（2026-06）、5 份證據（2026-07） |
| [`superseded/`](superseded/) | 已被現行文件取代的舊版本，檔名以世代月份結尾 |

`superseded/` 的每一份都有對應的現行文件，查現況請看現行文件而不是這裡：

| 舊版本 | 現行文件 |
|:---|:---|
| `jet-guide-2026-06.md`、`jet-guide-2026-08.md` | [`../jet-guide.md`](../jet-guide.md) |
| `action-contract-manifest-2026-06.md`、`-2026-08.md` | [`../action-contract-manifest.md`](../action-contract-manifest.md) |
| `jet-frontend-description-2026-06.md`、`-2026-08.md` | [`../jet-frontend-description.md`](../jet-frontend-description.md) |
| `development-status-2026-06.md`、`-2026-08.md` | [`../development-status.md`](../development-status.md) |
| `docs-readme-2026-06.md`、`docs-readme-2026-08.md` | [`../README.md`](../README.md) |
| `windows-handoff-2026-06.md`、`-2026-08.md` | 人工驗收改由現行計畫承接，沒有常設清單 |
| `agent-frameworks-2026-08.md` | [`../agent-compatibility.md`](../agent-compatibility.md) |
| `sqlserver-online-handoff-deferred-2026-08.md` | [`../sqlserver-enterprise-deferred.md`](../sqlserver-enterprise-deferred.md)（內容已回收為現行文件） |

`agent-frameworks-2026-08.md` 記錄了當時對 Claude Code 與 Codex 載入機制的逐項查證，包含各家官方文件的
轉址與版本。那些查證有時效性，引用前要重新確認，不能直接當成目前的平台行為。

## 這些副本被改過什麼

- 工作簿名稱已同步為 `data/` 的現行名稱。
- 已棄用的真實案件 fixture 敘述不再作為現行測試要求。
- 私人使用者路徑與組織信箱已改成中性 placeholder；秘密字面值不保留。
- 2026-08-30 合併開發紀錄時只重新排序並補上分界說明，沒有改動任何條目的文字、數字、條件、例外或
  使用者裁定。同一次把移動後失效的相對連結改成正確深度。

因為內容經過上述調整，本目錄不宣稱與來源逐位相同。未修改的原文、精確舊路徑與舊 commit blame 都在
保留範圍之外；不要為了補回它們把來源 Git history、mirror、bundle 或未分類 snapshot 匯入 `je-tool`，
也不要把來源專案的存在當成日常開發前提。

## 紀錄粒度

`development-log.md` 在 2026-06-29 前後粒度不同：之後是逐輪的完整條目，之前有一段是事後補寫的區間
摘要。兩段在 2026-06-20 到 2026-06-23 重疊但不矛盾，檔案中的分界說明有寫清楚差異。

一般閱讀先回到 [`../README.md`](../README.md)。
