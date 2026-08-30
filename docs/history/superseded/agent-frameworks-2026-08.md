# JET Agent Frameworks

本檔是 JET 專案的 agent framework 現況規格。它說明 Claude Code 與 Codex 如何共用同一份 skill、哪些上游框架只作參考、哪些內容已被刻意移除，以及下次更新時怎麼避免再度混入另一套文件、issue tracker 或版本控制流程。

## 現行結構

`AGENTS.md` 是跨 host 的入口；`.agents/harness/agent-workflow.md` 只保存 JET 特有的協作與驗收限制。`.agents/skills/<name>/SKILL.md` 是每個 skill 的唯一內容權威。

Codex 直接發現 `.agents/skills/`（掃描順序：目前工作目錄與各層 parent 直到 repository root、`$HOME/.agents/skills`、`/etc/codex/skills`、內建 skill），並以各目錄的 `agents/openai.yaml` 的 `policy.allow_implicit_invocation: false` 關閉隱式呼叫——該欄位預設為 `true`，漏寫等同開放隱式呼叫。Claude Code **只**發現 `.claude/skills/`，不掃 `.agents/skills/`，因此薄入口是必要的橋接而不是冗餘；每個入口只保留必要的原生 frontmatter 與回讀 canonical skill 的指令，不複製正文，其中 `disable-model-invocation: true` 負責關閉隱式呼叫。

兩側的對稱由 `SkillExplicitInvocationPolicyTests` 機器把關：canonical 與 Claude 入口必須一一對應、Codex 的 `allow_implicit_invocation` 必須恰有一次且精確為未加引號的 `false`、Claude 入口必須有精確為 `true` 的 `disable-model-invocation` 並指回自己的 canonical 路徑且不複製正文，`.claude/settings.json` 也必須維持 Superpowers plugin 為停用。少一邊、寫錯值或把正文貼進 wrapper 都會紅燈。

Repository 不保存模型別名、reasoning effort、通用角色 prompt 或自訂 subagent adapter。這些能力由目前 host 原生提供；專案只約束範圍、架構、測試互斥、驗收與版本控制。

## Host 載入機制與預算

下表是 2026-08-17 重新由兩家官方文件查證的載入行為，用量同日實測。修改 `AGENTS.md` 或 `CLAUDE.md` 前先確認不會超出預算——超出時內容會被**靜默截斷或降低遵循度**，不會有錯誤訊息。

| Host | 載入方式 | 預算 | 目前用量 |
|:---|:---|:---|:---|
| Codex | 先 `~/.codex/AGENTS.override.md`、`~/.codex/AGENTS.md`，再由 git root 往下逐層合併各層 `AGENTS.override.md`／`AGENTS.md`，越近的檔案排在越後面而覆寫較早的 | 合併總量預設 `project_doc_max_bytes` = 32 KiB，達到上限即停止加入後續檔案 | 2026-08-17 實測 `AGENTS.md` 22,735 bytes ≒ 22 KB，佔 32 KiB 的 69%（中文 UTF-8 每字 3 bytes，行數少不代表 bytes 少） |
| Claude Code | 讀 `CLAUDE.md`，不讀 `AGENTS.md`；以 `@AGENTS.md` import 讓兩者共用同一份內容（import 最多四層，且**不會**降低 context 用量，被 import 的檔案一樣在啟動時整份載入） | 官方建議單檔 200 行以內，越長遵循度越低 | 2026-08-17 實測 199 行（`CLAUDE.md` 19 ＋ `AGENTS.md` 180，本日減行後回到上限內）——**無餘裕，新增任何規則前必須先刪掉至少等量的行** |

兩家的官方建議一致：規則要具體到可驗證，並移除可由 codebase 推得的內容（目錄樹、依賴清單、架構概觀）。JET 依此把逐檔清單移出 `AGENTS.md` 與 `jet-guide.md` §14，改由架構測試把關。

規則若成長到超出預算，兩家都有原生的路徑作用域機制：Codex 用子目錄的 `AGENTS.md`／`AGENTS.override.md`，Claude Code 用 `.claude/rules/` 的 `paths:` frontmatter。**目前刻意都不使用**——兩者機制不同，一旦採用就會產生兩份需要同步的規則，違反單一事實來源。要採用必須先確認該規則確實只在特定路徑成立。

## 上游基準

| 上游 | 本次查證基準 | 採用方式 |
|:---|:---|:---|
| [obra/superpowers](https://github.com/obra/superpowers) | release `v6.3.0`（2026-08-12 發布），2026-08-16 查證 | reference-only；不安裝、不鏡像 skill |
| [mattpocock/skills](https://github.com/mattpocock/skills) | main commit `068b6e0c62393147daf03530149cdce209c93da8`，2026-08-15 提交，2026-08-16 查證 | project-adapted subset；來源與選入清單鎖在 `skills-lock.json` |
| [sandeco/reversa](https://github.com/sandeco/reversa) | main branch，2026-08-17 查證 | reference-only；ideation 理念內化為 project-owned `jet-converge`，不安裝 |

Superpowers 6.1 改以 host 原生 skill discovery 為主，不再替 Codex 注入 SessionStart bootstrap；6.1.1 在 Codex manifest 明確加入空 `hooks`，避免誤載 Claude hook。JET 採納這個「原生能力優先」方向，但不直接啟用 plugin。

2026-08-16 逐項比對 6.2.0 與 6.3.0 的新方法論後，**沒有需要移植的缺口**，因此只更新基準版本、不改 JET 流程：

- 6.3.0 依 spike／bounded／architectural 分級縮放流程儀式，小任務跳過雙文件。JET 的對應規則已在 `fresh-session-staged-development.md` 的「何時使用」（小型修補與單一 session 可完成的工作不建 master spec，且不得為套框架把工作切碎）。
- 6.3.0 讓 controller 遇到計畫衝突時記錄裁定而非停擺。JET 的對應規則已在同檔「部分進度優於全面停止」。
- 6.3.0 把同型小任務批次成一次派工。JET 的對應機制是階段內的 track（同前置、範圍不重疊、驗收合併），且刻意保留「階段一律線性」。
- 6.2.0 改用 per-plan workspace 以免 ledger 互相污染。JET 已規定每個主題只有一份 active master spec，逐階段 manifest 也各自落在被忽略的 evidence 目錄。

mattpocock/skills 在 2026-08-15 修掉「skill 呼叫其他 user-invoked skill」的問題（upstream #453）。JET 的適配版本本來就沒有這個缺陷：當時的 `grilling` 與 `to-tickets` 只寫「後續由使用者明確呼叫」，不自行轉呼叫（這兩個 skill 已於 2026-08-17 移除，見下方 Skill 清單）。

sandeco/reversa 是把 legacy 系統逆向工程成 agent 可執行規格的完整框架（上百個 agent）。2026-08-17 評估結論為不安裝：安裝器會把整批 agent 寫進 `.agents/skills/`（撞 canonical skill root 與對稱守衛）、建立 `.reversa/` 與 `_reversa_*` 平行文件根（含自己的缺陷追蹤），而其 discovery 管線要生成的規格正是 `jet-guide.md` 與 `action-contract-manifest.md` 已人工驗證擁有的內容；讓未經審計的第三方 agent 群掃描含機敏案件資料的 repository 也超出可接受邊界。其 ideation 管線（framer／explorer／challenger／arbiter／clarify）的方法論已內化為 `jet-converge`，逐條紀錄見 `skills-lock.json`。

Superpowers 的完整工作流會自動路由 skill，並預設使用自己的 specs／plans、worktree、commit 與 branch finishing 慣例。這些行為和 JET 的 explicit-only、`docs/specs/`、單線性工作樹以及「使用者驗證後才明確下令 commit／push」規則衝突。因此 `.claude/settings.json` 必須維持 `superpowers@claude-plugins-official: false`。若未來要啟用，必須先由使用者明確改變上述 repository 規則，不能只升版 plugin。

## Skill 清單

三個 project-owned skill 由 `AGENTS.md` 按任務要求閱讀，也可由使用者明確呼叫：

- `minimalist-ui`：JET 前端視覺與互動邊界。
- `jet-testing`：JET 測試層級、oracle、provider 與共享資源規則。
- `jet-dev-loop`：contract-first、build、test、runtime debug 與 GUI handoff。

第四個 project-owned skill 沒有任務路由，只由使用者明確呼叫：

- `jet-converge`：把發散、模糊或互相衝突的想法收斂成可裁決的計畫輸入（框定→發散→挑戰→收斂）。2026-08-17 建立，內化 mattpocock `grilling` 的訪談紀律與 sandeco `reversa` ideation 管線的理念；兩個上游的出處與不安裝原因記在 `skills-lock.json`。

保留的 mattpocock-derived workflow skill 全部 explicit-only：

- `diagnosing-bugs`：用可區分假設的證據定位根因。
- `code-review`：依 repository 標準與 spec 兩軸審查變更（並覆蓋內建 `/code-review`，見下節）。

其餘六個（`grilling`、`to-spec`、`to-tickets`、`wayfinder`、`prototype`、`implement`）於 2026-08-17 依使用者裁決移除：自 2026-07-17 引入以來零使用紀錄，規劃工作流實際由 `.agents/harness/fresh-session-staged-development.md` 覆蓋。逐條原因記在 `skills-lock.json` 的 `removedSkills`；未來要用可從上游重新適配。

## Claude Code plugin 與內建 skill 的交互作用

`.claude/settings.json` 目前啟用三個 official plugin：`frontend-design`、`csharp-lsp`、`microsoft-docs`。它們是 Claude Code 專屬，Codex 完全看不到，因此**不得**成為任何規則、驗收條件或架構判斷的依據；兩家的結論必須在不啟用 plugin 的前提下也成立。

plugin skill 以 `plugin-name:skill-name` namespace 載入，不會和 `.claude/skills/` 撞名，但**不受 repository 的 explicit-only 政策約束**——`frontend-design` 沒有 `disable-model-invocation`，Claude 可以自行判斷載入。這是 explicit-only 的唯一既有例外，刻意保留，因為它只提供通用設計語彙、不改變 JET 的行為權威。衝突時的優先順序固定為：`docs/jet-frontend-description.md`（行為）→ `docs/action-contract-manifest.md`（契約）→ `minimalist-ui`（視覺）→ plugin。`minimalist-ui` 自身開頭已寫明它優先於通用 frontend-design plugin；`AGENTS.md` 的任務路由也要求前端視覺工作直接讀 `minimalist-ui`，不依賴 plugin 是否剛好載入。

另有一個容易誤判的疊加：Claude Code 的專案層 skill 會**覆蓋同名內建 skill**。JET 的 `.claude/skills/code-review/` 因此接管了 `/code-review`，內建版本只剩別名 `/review` 可達。這是刻意的——JET 的審查要走 repository 標準與指定 spec 兩軸——但要知道在本 repository 打 `/code-review` 不會得到內建的雲端多代理審查。

`csharp-lsp` 與 `microsoft-docs` 只提供語言服務與文件查詢，不帶入工作流或版本控制慣例，因此沒有和 repository 規則衝突的面。

## Claude Code permissions 硬擋層

2026-08-17 起，`.claude/settings.json` 的 `permissions.deny` 把三組本來只寫在 `AGENTS.md` 的禁令升級為執行前攔截：直接 `dotnet test`（測試必須走 `tools/verify.ps1` 的互斥鎖與證據）、`git add -A`／`git add --all`／`git add .`／`git write-tree`／`git stash --all`（防機敏資料被整批寫進 object store，該類錯誤不可逆）、對兩個機敏資料目錄的寫入（樣本只讀；讀取照常開放）。官方文件明示 CLAUDE.md 只是 context、不是強制層，permission 規則才由 Claude Code 客戶端強制執行：deny 永遠壓過 allow、複合命令逐段比對（`cd x && dotnet test` 一樣被擋）、`timeout` 等 wrapper 先剝除再比對。deny 清單不得被靜默移除，由 `SkillExplicitInvocationPolicyTests` 機器把關。

限制與跨 host 邊界：deny 只擋 Claude Code 自身發出的命令與檔案工具，不擋子程序自行開檔——那一層仍由架構測試負責。Codex 沒有 repo 可攜的逐命令 deny：`approval_policy`／`sandbox_mode` 等鍵放在專案層 `.codex/config.toml` 會被忽略，屬使用者層級設定。因此跨 host 共同底線維持「`AGENTS.md` 規則＋架構測試」，permissions 是 Claude 側的加層，不是替代。

## Output style 歸位

Output style 是 Claude Code 全域個人資產：CLI、桌面版與 VS Code 擴充共讀同一套 `~/.claude/`，個人 style 放在 `~/.claude/output-styles/` 即對所有專案生效，正本一律在該處。本 repository 只鏡像 `.claude/settings.json` `outputStyle` 啟用的那一份（`natural-technical-conversation.md`，供 fresh clone 直接生效），其他個人 style 不進 repository——2026-08-17 依使用者裁決移除已漂移的 `readable-technical.md` repo 副本（正本為使用者 2026-08-15 改寫的本機版）。另注意：custom style 未設 `keep-coding-instructions: true` 時會移除 Claude Code 內建的軟體工程指示；本專案啟用的 style 已正確設定。

## 已移除的混雜內容

下列上游 skill 不再保留：

- `setup-matt-pocock-skills`：會建立 issue-tracker 與外部文件根，和 JET 文件體系重疊。
- `grill-with-docs`：現行上游內容為 `grilling`＋`domain-modeling` 的組合殼（2026-08-17 查證）；兩個依賴一個已移除、一個原本就拒絕，訪談與收斂需求由 `jet-converge` 覆蓋。
- `domain-modeling`、`codebase-design`、`improve-codebase-architecture`：會引入另一套 ADR／架構文件與通用設計流程，和 `jet-guide.md`、`docs/specs/` 及現有架構守衛重疊。
- `tdd`：測試節奏、資料庫真實引擎與 GUI 邊界已由 `jet-testing` 唯一管理。
- `grilling`、`to-spec`、`to-tickets`、`wayfinder`、`prototype`、`implement`：2026-08-17 因引入一個月零使用移除；規劃工作流由 `.agents/harness/` 的 fresh-session 框架覆蓋。

同時移除 `.claude/backups/`、`docs/superpowers/`、host-specific agent role、model-dispatch、delegation template、judgment rubric 與 session diagnosis。這些內容不是現況規格；它們會讓搜尋結果混入舊程式碼、已完成 plan 或過時工具假設。歷史追溯由 Git 與 `docs/development-log.md` 負責。

## 更新程序

1. 從官方 repository／官方 host 文件查證最新 release、commit、skill discovery、metadata schema 與載入預算。不要只看 blog、舊快照或本機已安裝版本。取不到來源時明確記為「未能查證」，不得以推測填補。
   - 2026-08-03 查證：Codex 的 `AGENTS.md` 文件已從 `developers.openai.com` 永久轉址到 `learn.chatgpt.com/docs/agent-configuration/agents-md`；Claude Code 的 memory 文件已從 `docs.anthropic.com` 轉址到 `code.claude.com/docs/en/memory`。兩者皆查證成功。
   - 2026-08-03 未能查證：`openai.com/index/harness-engineering/` 回 HTTP 403，內容未取得，因此**沒有**依該文調整本 repository 的任何規則。
   - 2026-08-16 查證：Codex 的 skill 文件已從 `developers.openai.com/codex/skills` 永久轉址到 `learn.chatgpt.com/docs/build-skills`，`.agents/skills/` 的四層掃描順序與 `policy.allow_implicit_invocation` 預設 `true` 由該文確認；Claude Code 的 `code.claude.com/docs/en/skills` 確認專案 skill 只從 `.claude/skills/` 探索、專案層覆蓋內建同名 skill、`disable-model-invocation` 預設 `false`。兩家的 explicit-only 接線與本 repository 現況一致。
   - 2026-08-17 查證：Claude Code `code.claude.com/docs/en/permissions`／`hooks`／`memory` 確認 permission deny 為客戶端強制層（官方明示 CLAUDE.md 非強制層）、複合命令逐段比對、`.claude/rules/` 含 `paths:` frontmatter 已是正式功能（本 repository 維持不採用）；`/output-style` 指令 v2.1.73 棄用、v2.1.91 移除，功能由 `/config` 與 `outputStyle` 設定承接。Codex changelog（`learn.chatgpt.com/docs/changelog`）v0.146／v0.147 新增 Agent Plugins、executor skills 與 turn-scoped permission profiles，移除 `codex exec --full-auto`（本 repository 未使用）；AGENTS.md 與 skills 發現機制無變更。另確認 Codex 的 approval／sandbox 鍵在專案層 `.codex/config.toml` 被忽略，逐命令 deny 無 Codex 等價物。
   - 2026-08-17 查證（skill 評估）：mattpocock `grill-with-docs` 現行內容為 `grilling`＋`domain-modeling` 的組合殼；sandeco/reversa 全樹與其 `framer`／`explorer`／`challenger`／`arbiter`／`clarify`／`brainstorm` agent 內容由 GitHub 直接取得。兩者評估結論皆為不安裝、理念內化為 project-owned `jet-converge`，原因記於 `skills-lock.json`。
2. 先比較上游變更是否解決 JET 的真問題。不要因上游新增 skill 就全量同步。
3. 上游 skill 只能更新 canonical `.agents/skills/`。移除 issue tracker、外部文件樹、commit／push、固定 Agent 語法與和 JET 規則重複的內容；Claude wrapper 不複製正文。
4. 更新 `.agents/skills/skills-lock.json` 的 commit、日期、選入與移除原因。Superpowers 若仍是 reference-only，也要記錄最新查證版本。
5. 驗證所有 skill 的 frontmatter、Codex `allow_implicit_invocation: false`、Claude `disable-model-invocation: true`、相對路徑與殘留引用；重新量測「Host 載入機制與預算」一節的用量；最後執行 `git diff --check`。

不要為 framework 更新建立 repository 內備份。需要臨時比較時使用系統暫存目錄；需要追溯時使用 Git diff。不要把上游 release notes、完整 skill tree 或安裝產物複製進 repository。
