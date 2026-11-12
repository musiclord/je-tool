# 安全檢查紀錄

這裡放 `security-audit` agent 的執行紀錄。使用方式見 [skill](../../.agents/skills/security-audit/SKILL.md)，安裝版本見 [來源](../../.agents/skills/security-audit/UPSTREAM.md)。

每次完整稽核新增 `run-1/`、`run-2/` 這類目錄，使用下一個未使用的整數，保留先前紀錄。
開始前確認該次目錄已被 Git 忽略，例如執行 `git check-ignore docs/security/run-1/`。

- 先看該次的 `REPORT.md`，需要細節再看 `FINDINGS-DETAIL.md` 與 `NEEDS-VALIDATION.md`。
- JSON、各 agent 的紀錄與其他輸出沿用 skill 的格式，一起放在該次目錄，不另設模板。
- 說明用繁體中文，簡單寫出檢查範圍、發現、證據、尚未確認的事項與下一步。
- 內容遵守 [專案規則](../../AGENTS.md)，不記錄真實案件或機敏資料。

`run-*` 目錄已設為 Git 忽略；本說明保留在可納入版本控制的範圍。

本次 SAT 做過限定範圍的原始碼檢視與套件漏洞查詢，結果寫在 [Technical Document](<../SAT/Technical Document.docx>) 第 5 節、第 12 節與第 19 節。先前另外整理的安全檢查摘要已在 2026-11-10 移除。這些檢查不是完整稽核，不登記為 run 紀錄。
