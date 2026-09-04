---
name: jet-readable-docs
description: "Use when writing or rewriting Traditional Chinese text that people will read in JET: current docs under docs/, plan files in docs/specs/, development-status entries, commit messages, or a reply that summarizes finished work. Also use when the Documentation check reports style_warning or style_pattern. Keeps every fact unchanged while replacing agent jargon, symbol-joined phrases, and coined terms with plain Taiwanese Chinese sentences. Not for code, identifiers, JSON, or test names."
---

# JET 面向人的文字

讀者是第一次接觸專案、只會讀一次的維護者。目標是讓他讀完能用自己的話說出「誰做了什麼、結果如何、
下一步是什麼」。事實不能變；變的只有句子。

## 什麼時候用

- 要寫或改 `docs/` 現行文件、`docs/specs/` 計畫、`docs/development-status.md`、commit 訊息，或回覆使用者的
  收尾摘要。
- `tools/verify.ps1 -Command Documentation` 回報 `style_warning` 或 `style_pattern`。
- 使用者說文件難讀、像天書、術語太密、有奇怪的語法。

不用在程式碼、識別字、JSON、測試名稱與固定命令上；那些照程式慣例。

## 步驟

1. **先定讀者與問題。** 這段文字要回答誰的什麼問題？入口文件（`README.md`、`docs/project-context.md`）的
   讀者不懂驗證框架；計畫檔的讀者是下一個 session；commit 訊息的讀者是三個月後看 `git log` 的人。
2. **列出不能動的事實。** 檔名、數字、日期、裁定、條件、例外、不確定性，以及程式與 UI 既有的名稱。
   改寫前先寫下來，改寫後逐項核對。查不到出處的內容標「待確認」，不補猜。
3. **一次改一段，只做最小必要修改。** 舊句子已經不自然時可以整段重寫，但不順手擴充範圍，也不重寫整份
   文件。
4. **逐項對照下面的檢查表。** 命中就改。
5. **跑 Documentation。** 每一條 warning 都要有結論：改掉，或說明為什麼保留。警告只是疑點，判斷仍由人負責。
6. **做轉述測試。** 假裝自己沒看過這輪對話，讀一次改後的段落，用一句話說出誰做了什麼、結果如何。
   說不出來就再改。
7. **留下範例。** 有代表性的改寫寫進 `references/rewrite-examples.md`，記錄原文、改寫、原因與來源。

## 檢查表

| 症狀 | 怎麼改 |
|:---|:---|
| 用 `＋`、`／`、`→`、`＝` 把名詞串成句子 | 用「和」「或」「然後」「共」寫成句子，或改成編號步驟 |
| 兩層以上括號，或一句塞三個以上名詞 | 拆句，一句只講一件事 |
| 自創名詞、縮寫、階段名稱；已知例子列在 `references/rewrite-examples.md` | 用普通中文說做了什麼；程式已有名稱就沿用那個名稱 |
| 驗證框架的工作用語進到面向人的文字，例如 receipt、gate、lane、`紅燈`、`綠燈` | 寫「收據」「檢查」「路線」「通過」「失敗」；`first-red` 是框架固定名稱，可保留，第一次出現時說明是「第一次失敗的證據」 |
| 只寫「已完成」「全部通過」「無問題」 | 寫實際執行的命令、數字與範圍；沒做的事逐項寫「未執行」 |
| 形近錯字，例如把「路徑」寫成「路徽」 | 重讀一次，常用詞逐字看；短句比長句容易看出錯字 |
| 沒有主詞的句子，例如「已修正並重新驗證」 | 補上誰改的、改了什麼、用什麼驗證 |
| 翻譯腔或過度正式，例如「進行執行」「予以確認」 | 直接用動詞：「執行」「確認」 |

## 不要做的事

- 不建立禁用詞清單當品質標準；換一組新詞繞過很容易。清單只當疑點提示。
- 不用句長、「的」字數量或 warning 數量當硬性失敗條件。
- 不一次重寫全部文件。沒有事實核對的大規模改寫會再次產生密集術語，並遺失脈絡。
- 不把「更專業、更正式、更完整」當目標。
- 不讓舊版與新版同時看起來像現行規格；被取代的內容進 `docs/history/`，並標明已被取代。

## 來源與分工

本 skill 整理自使用者 2026-09-02 提供的 Codex 對話「如何避免 agent talk shit」。那份對話彙整了 GOV.UK 的
plain language 原則、Microsoft 繁體中文本地化指南、Diátaxis 的文件四分法、GitLab 文件指南、WordPress 的
agent 文件指引與 Digital.gov 的轉述測試，並建議 JET 在驗證框架階段建立一個小型的 human-readable-docs
skill；本 skill 依專案慣例命名為 `jet-readable-docs`。

文件分工、新名詞准入規則與完成前的檢查以 `docs/README.md`「寫作規範」為準，本 skill 不另立第二套。
