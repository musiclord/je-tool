# 正規化歷史文件

這個目錄保存從兩個來源 repo 搬入、並按 `je-tool` 現行檔名正規化的歷史文件。它們能解釋設計與驗證
脈絡，但不是來源 HEAD 的逐位 snapshot，也不能代表 `je-tool` 現在的行為或驗證狀態。

| 目錄 | 角色 | 檔案數 |
|:---|:---|---:|
| `je-testing/` | 早期 README、guide、frontend、action contract、status、log、handoff 與設計文件 | 14 |
| `new-je-tool/` | 後續 README、guide、frontend、action contract、status、log、agent framework 與 evidence | 14 |

## 本次正規化

- 工作簿名稱已同步為 `data/` 的現行名稱。
- 已棄用的 repo-local 真案 fixture 敘述不再作為現行測試要求。
- 私人使用者路徑與組織信箱已改成中性 placeholder；秘密字面值不保留在歷史副本。
- 因內容已調整，本目錄不再宣稱搬移前後 SHA-256 相同。
- `new-je-tool/docs/design_handoff_jet_frontend/` 是已棄用的臨時模板，刻意不納入；歷史文件中的連結
  只保留當時脈絡，不構成現行缺檔。

未修改原文、精確舊路徑、raw variants 與舊 commit blame 都在本次保留範圍之外。不要為此把來源
Git history、mirror、bundle 或未分類 snapshot 匯入 `je-tool`，也不要把來源 repo 的存在當成日常前提。

一般閱讀先回到 [`../README.md`](../README.md)；來源關係見
[`../repository-lineage.md`](../repository-lineage.md)。
