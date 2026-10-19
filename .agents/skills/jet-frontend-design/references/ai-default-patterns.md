# 容易偏離 JET 的預設樣式

這份清單供設計疑慮或獨立檢查時查閱。它把既有研究中的風險轉成 JET 的具體選擇，不要求每輪新增規則。
來源與原查核日期保留在 [sources.md](sources.md)；已接受的結果以 [前端說明](../../../../docs/jet-frontend-description.md) 為準。

| 容易套用的樣式 | JET 的選擇 | 原研究來源 |
|:---|:---|:---|
| 奶油底、襯線大標、斜體字與陶土色的組合 | 暖紙是使用者指定；搭無襯線字和石板藍，不加陶土或琥珀裝飾 | Anthropic 提示與 frontend-design、impeccable、taste-skill |
| 近黑底、螢光色與發光框 | 延續淺色工作面與內容優先 | frontend-design、impeccable |
| 報紙風、處處細線、等寬小標 | 用間距、表面與角色圓角分層，細線只在需要的分界 | frontend-design、gstack |
| 全頁同款圓角卡片、框中框 | 托盤、白紙、文件卡依用途分工，同一區塊不重複包框 | frontend-design、impeccable、interface-design |
| 全大寫小標、假順序編號、文字後裝飾箭頭 | 使用直接的中文標題；只有真正的步驟才保留序號 | Anthropic、frontend-design、PR #1806 |
| 膠囊按鈕、單側粗彩邊 | 按鈕沿用控制項圓角；膠囊只用狀態，導覽短藍條保留 | Anthropic、impeccable、gstack |
| 漸層文字、彩色毛玻璃卡、裝飾光暈 | Liquid Glass 只用於浮動功能層，保留細邊光與不透明退路 | Apple、使用者裁定；Anthropic、taste-skill 提供反例 |
| 每張卡片滑入、移上去就浮起、數字跳動 | 只回應操作；可取用的 KCT 卡片保留既有輕微抬升暗示 | Apple、interface-design、impeccable |
| 大數字卡片當主角、假統計或假頭像填空 | 真實摘要沿用相連統計列，資料仍是畫面主體 | frontend-design、gstack |
| 金融就套海軍藍加金、整套 Tailwind slate | 暖紙與石板藍是已選方向，不重新按產品類型套色 | impeccable、taste-skill |
| 改禁用清單後又換成冷灰藍、鈷藍與琥珀的組合 | 先看使用者已接受的實際畫面，避免只換一組常見配色 | PR #1806 樣本觀察，未經統計 |
| 行銷文案、成功訊息加驚嘆號 | 只寫操作、狀態與必要下一步 | frontend-design、impeccable、taste-skill |
| 中文字距、斜體、小於 12px 的字 | 沿用 Noto Sans TC、正常字距與既有字級 | W3C、Windows、impeccable |

暖紙、狀態膠囊、6px 控制項與 Liquid Glass 都有使用者裁定，不能因出現在通用風格清單就禁止。
同樣地，輕微玻璃彈性不等於誇張彈跳，平滑的操作回應也不等於裝飾動畫。

需要改變既定方向時，說明審計員遇到的問題與具體替代樣式，依本輪授權處理。不要拿這張清單當成所有新設計都須再請示的理由。
