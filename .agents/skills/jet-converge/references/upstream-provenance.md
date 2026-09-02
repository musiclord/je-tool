# 上游查核與 JET 適配

本檔只供維護 `jet-converge` 時使用。一般收斂工作不載入。

查核日期：2026-09-01。所有來源均取自當日的原始儲存庫或 OpenAI 官方文件。

| 來源 | 查核版本 | 本次採用 | 明確不採用 |
|:---|:---|:---|:---|
| [OpenAI Skills](https://learn.chatgpt.com/docs/build-skills) | 官方文件當日版本 | `.agents/skills` repo discovery、漸進載入、`agents/openai.yaml`、explicit invocation | 不包成 plugin；目前只供 JET repo 使用 |
| [OpenAI AGENTS.md](https://learn.chatgpt.com/docs/agent-configuration/agents-md) | 官方文件當日版本 | 根目錄權威與專案內近端指示；保持 `AGENTS.md` 簡短 | 不把完整 skill 複製進 `AGENTS.md` |
| [OpenAI Memories](https://learn.chatgpt.com/docs/customization/memories) | 官方文件當日版本 | 本機記憶是查找線索；必要規則與團隊知識留在受版控文件 | 不把本機 memory 當唯一權威，也不手工維護 Codex 生成記憶 |
| [mattpocock/skills](https://github.com/mattpocock/skills) | `6654f6b60cd9d5be8b54c6fafe44346dabeb3b76` | `grilling` 的決策樹前緣、分輪問題、Agent 自行查證；`domain-modeling` 的名詞衝突與程式交叉檢查 | 不建立 `CONTEXT.md`／ADR；不安裝整套 skills 或 issue 流程 |
| [sandeco/reversa](https://github.com/sandeco/reversa) | `4cc8f7298dd73268d7eddc01e0dbcc59da074696` | Framer → Explorer → Challenger → Arbiter → Pre-Spec；中心假設、便宜驗證、分歧保留 | 不安裝 agent 群、不建立 `.reversa/`、`_reversa_sdd/` 或平行規格系統 |
| [obra/superpowers](https://github.com/obra/superpowers) | `b36e0829c6d0140e93cfef2ca599b1b07d4a7797` | 先查專案脈絡、2–3 個方向、設計自我審查；外部事實需要時先研究 | 不採全面 hard gate、自動 commit、專屬 specs/plans 根或強制子代理流程 |

## 更新原則

- 重新查核時記錄新的 main SHA、變更內容，以及 JET 是否需要調整；不可只寫「已更新」。
- 只吸收會改善 JET 收斂品質的做法，不追隨上游包裝、命令名稱或文件目錄。
- 上游方法和 JET 的機敏資料、單一事實來源、一次只有一份現行大型計畫或 Git 授權衝突時，以 JET 邊界為準。
- 不以 star 數、流行度或 detector 分數作為採用依據；要說明它解決的具體失敗模式。
