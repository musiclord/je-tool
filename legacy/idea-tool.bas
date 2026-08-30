Dim IndustryArray() AS string
Dim GLFieldArray_mix() AS string
Dim GLFieldArray_num() AS string
Dim GLFieldArray_date() AS string
Dim GLFieldArray_Num() AS string
Dim GLFieldArray_Date() AS string
Dim FieldArray_date() AS string
Dim FieldArray_num() AS string
Dim FieldArray_Mix() AS string
Dim FieldArray_AddAccPairing() AS string
Dim list1$() AS string
Dim TestArray() AS string
Dim TBFieldArray_num() AS string
Dim GLFieldArray_Char() AS string
Dim TBFieldArray_mix() AS string
Dim TBFieldArray_Char() AS string
Dim tempListbox1() AS string
Dim tempListbox2() AS string

Begin Dialog Introduction 0,14,283,293,"分錄測試自動化工具 (Ver.202607) ;  KPMG Taiwan ITA 開發", .Intro_Dlg
  Text 37,98,40,9, "步驟 一", .Text2
  PushButton 95,91,110,25, "進行資料欄位的配對", .BtnDataMap
  PushButton 95,122,110,25, "上傳特定科目配對的檔案", .BtnLoadfile
  PushButton 95,153,110,25, "預先篩選程序", .BtnRoutine
  PushButton 95,183,110,25, "設定進階篩選條件", .BtnCriteria
  PushButton 95,213,110,25, "產生工作底稿", .BtnExport
  Text 37,129,40,9, "步驟 二", .Text3
  Text 37,160,40,9, "步驟 三", .Text4
  Text 37,191,40,9, "步驟 四", .Text5
  Text 37,222,40,9, "步驟 五", .Text6
  PushButton 95,244,40,20, "重新執行", .BtnReRun
  PushButton 164,244,40,20, "離開", .BtnCancel
  Text 11,5,249,76, "Text", .Text1
  CheckBox 95,266,106,14, "Send Mail", .CheckBox1
  PushButton 10,86,10,10, "i", .BtnIntroductionlHelp
End Dialog

Begin Dialog ReRunDialog 0,25,167,196,"重新執行設定", .ReRun_Dlg
  PushButton 19,20,120,25, "重新進行資料欄位配對作業", .BtnDataMap
  PushButton 19,50,120,25, "重新上傳特定科目配對的檔案", .BtnLoadfile
  PushButton 19,80,120,25, "重新執行預先篩選程序", .BtnRoutine
  PushButton 19,110,120,25, "清空已保存之進階篩選結果", .BtnCriteria
  Text 19,5,120,12, "您想要由那個步驟重新作業 ?", .Text1
  PushButton 89,143,50,20, "離開", .BtnCancel
End Dialog

Begin Dialog Information 0,17,312,351,"步驟一  案件基本資訊 及測試資料欄位配對 設定", .Info_Dlg
  Text 25,34,105,10, "財務報導期間 - 開始日", .TextStartDate
  TextBox 155,34,55,10, .TextBoxStartDate
  Text 25,48,105,10, "財務報導期間 - 結束日", .TextEndDate
  TextBox 155,48,55,10, .TextBoxEndDate
  GroupBox 10,7,281,93, "案件基本資訊", .GroupBox1
  Text 25,20,55,10, "案件客戶名稱", .TextEntityName
  TextBox 85,20,201,10, .TextBoxEntityName
  Text 25,62,125,10, "期末財務報表準備期間-開始日", .TextLastDate
  TextBox 155,62,55,10, .TextBoxLastDate
  PushButton 220,61,18,12, "...", .BtnLastDate
  PushButton 220,47,18,12, "...", .BtnEndDate
  PushButton 220,33,18,12, "...", .BtnStartDate
  GroupBox 20,115,113,44, "試算表(Trial Balance)", .GroupBox2
  PushButton 246,297,40,20, "下一步", .BtnNext
  PushButton 17,296,40,20, "取消", .BtnCancel
  PushButton 12,58,10,10, "i", .TBDetails_Help1
  Text 25,78,40,10, "產業別", .Text5
  DropListBox 74,78,136,13, IndustryArray(), .Industry_DropListBox
  GroupBox 10,237,281,53, "步驟一　作業進度訊息", .GroupBox6
  GroupBox 143,115,128,45, "傳票分錄檔(Journal Entry )", .GroupBox8
  GroupBox 10,102,281,131, "請點選下方按鈕進行試算表與總帳設定", .GroupBox7
  PushButton 27,135,85,16, "試算表：待處理", .TB_Dealing
  PushButton 150,135,85,16, "傳票：待處理", .GL_Dealing
  Text 23,256,260,24, .Text1
  GroupBox 22,174,257,56, "前期設定配對檔(WP)", .GroupBox3
  PushButton 251,199,20,14, " ... ", .BtnWP
  Text 31,187,210,37, "WorkingPaperPath(施工中)                                       不要亂點 (╬☉д⊙) 點一次會寄一封信給Dorian         說要自願提供一個笑話。", .Txt_WP
End Dialog

Begin Dialog GLDetails 0,10,585,346,"步驟一  傳票分錄檔欄位配對設定", .GLDetail_Dlg
  Text 30,50,50,12, "傳票號碼", .TextDocNum
  Text 230,50,55,12, "會計科目編號", .TextAccNum
  Text 230,85,55,12, "傳票摘要", .TextDes
  Text 230,68,55,12, "會計科目名稱", .TextAccName
  Text 30,68,55,12, "傳票文件項次", .TextLineID
  Text 230,155,60,12, "分錄來源模組", .TextJESource
  Text 30,85,50,12, "總帳日期", .TextPostDate
  Text 30,198,56,12, "傳票建立人員", .TextCreateBy
  DropListBox 90,50,130,14, GLFieldArray_mix(), .DropListDocNum
  DropListBox 295,50,130,14, GLFieldArray_mix(), .DropListAccNumGL
  DropListBox 295,85,130,14, GLFieldArray_mix(), .DropListDes
  DropListBox 295,68,130,14, GLFieldArray_mix(), .DropListAccName
  DropListBox 90,67,130,14, GLFieldArray_num(), .DropListLineID
  DropListBox 295,155,130,14, GLFieldArray_mix(), .DropListJESource
  DropListBox 90,85,130,14, GLFieldArray_date(), .DropListPosDate
  DropListBox 90,198,130,14, GLFieldArray_mix(), .DropListCreateBy
  Text 230,137,60,12, "人工/自動分錄", .TextManual
  DropListBox 295,137,130,15, GLFieldArray_Num(), .DropListManual
  Text 30,137,50,12, "傳票核准日", .TextDocDate
  DropListBox 90,137,130,14, GLFieldArray_date(), .DropListDocDate
  Text 230,198,55,12, "傳票核准人員", .TextApprovBy
  DropListBox 295,198,130,14, GLFieldArray_mix(), .DropListApprovBy
  GroupBox 9,36,555,65, "必要欄位", .GroupBox1
  GroupBox 15,120,545,60, "其他資料欄位資訊", .GroupBox2
  GroupBox 13,183,545,37, "建立/核准人員資訊", .GroupBox4
  GroupBox 9,105,555,180, "非必要欄位(但若之後篩選條件會考量，則須做配對)", .GroupBox5
  Text 34,6,303,10, "請點選左方按鈕選擇總帳傳票分錄檔(Journal Entry)", .TextGLFile
  PushButton 9,5,18,12, "...", .BtnGLFile
  PushButton 435,290,60,20, "確定", .BtnRun
  PushButton 43,289,60,20, "取消", .BtnCancel
  TextBox 20,260,130,14, .UseDefind1
  GroupBox 15,224,545,58, "自行定義欄位", .GroupBox3
  TextBox 155,260,130,14, .UseDefind2
  TextBox 290,260,130,14, .UseDefind3
  DropListBox 20,241,130,14, GLFieldArray_mix(), .DropListUserDefind1
  DropListBox 155,241,130,14, GLFieldArray_mix(), .DropListUserDefind2
  DropListBox 290,241,130,14, GLFieldArray_mix(), .DropListUserDefind3
  DropListBox 425,241,130,14, GLFieldArray_Date(), .DropListUserDefind4
  Text 36,22,300,10, "請針對所選擇之傳票分錄檔(Journal Entry)進行欄位配對", .Text2
  PushButton 6,220,10,10, "i", .GLDetail_Help1
  TextBox 425,260,130,14, .UseDefind4
  PushButton 450,65,80,20, "金額欄位：待處理", .GL_Amount
  PushButton 450,143,80,21, "無需考量過帳狀態", .BtnVaild
  CheckBox 30,155,100,15, "傳票核准日同總帳日", .CK_Approval_Date
End Dialog

Begin Dialog Routines 0,0,429,251,"步驟三  預先篩選程序", .Routine_Dlg
  Text 30,68,240,11, "#1. 於期末財務報表準備期間核准之分錄", .TextRout1
  Text 30,83,280,10, "#2. 分錄摘要出現特定描述(預設的特定文字請參考 iButton 說明)", .TextRout2
  Text 30,97,240,11, "#3. 未預期出現之特定借貸組合", .TextRout3
  Text 30,113,240,11, "#4. 分錄金額中有連續0的尾數(0的位數請參考 i-button 說明)", .TextRout4
  Text 30,127,240,12, "#5. 依分錄編製者彙總分錄", .TextRout5
  Text 30,141,230,11, "#6. 較少使用之科目", .TextRout6
  GroupBox 9,53,400,104, "標準化之預先篩選程序", .GroupBox1
  Text 17,179,385,20, "註：確認後，按下【確定執行】將產生Pre-screeningReport，除會帶入上述預先篩選條件之結果外，亦將放入前面ValidationReport已產生之空白摘要結果。", .RoutineMsgText
  Text 9,20,400,26, "執行預先篩選程序的目的，係協助查核團隊進一步定義出特定的高風險條件，以辨認不尋常的分錄進行測試。這些程序個別的用途僅作為初步判斷使用，其本身將不會是實際設定之高風險範圍條件。", .TextPR
  Text 9,7,400,11, "在預先篩選程序 這個步驟中，將會先執行一般常見的初步篩選。", .Routines_Text1
  PushButton 3,48,10,10, "i", .Routines_Help1
  GroupBox 9,162,400,47, "步驟三  作業進度訊息", .GroupBox3
End Dialog

Begin Dialog Criteria 27,1,563,463,"步驟四  進階篩選作業", .Criteria_Dlg
  CheckBox 254,258,85,10, "額外的特定尾數", .ChkBoxModfRout4
  CheckBox 25,61,300,10, "於期末財務報表準備期間核准之分錄", .ChkBoxRout1
  CheckBox 24,163,200,10, "分錄摘要出現特定描述", .ChkBoxRout2
  CheckBox 25,325,190,10, "未預期出現之特定借貸組合", .ChkBoxRout3
  CheckBox 24,258,200,10, "分錄金額中有連續0的尾數", .ChkBoxRout4
  CheckBox 25,108,12,10, .ChkBoxSelDate1
  CheckBox 25,125,12,10, .ChkBoxSelDate2
  TextBox 199,110,50,10, .TextBoxFromDate1
  DropListBox 38,125,100,10, FieldArray_date(), .DropListSelDate2
  DropListBox 38,108,100,10, FieldArray_date(), .DropListSelDate1
  PushButton 120,395,70,30, "匯出彙總報告", .BtnExpSumReport
  TextBox 199,128,50,10, .TextBoxFromDate2
  TextBox 289,110,50,10, .TextBoxToDate1
  TextBox 289,128,50,10, .TextBoxToDate2
  Text 274,110,10,9, "到", .Text1
  Text 274,128,10,10, "到", .Text2
  Text 144,110,55,10, "依日期區間 : 從", .TextSelDate1
  PushButton 369,110,79,12, "特定日期篩選", .BtnSelSpeDate1
  Text 144,128,55,10, "依日期區間 : 從", .TextSelDate2
  PushButton 369,128,79,12, "特定日期篩選", .BtnSelSpeDate2
  GroupBox 13,45,515,98, "搜尋特定日期範圍", .GroupBox2
  CheckBox 24,278,12,10, .ChkBoxSelNum1
  TextBox 206,278,60,10, .TextBoxFromNum1, ES_NUMBER
  DropListBox 36,275,100,14, FieldArray_num(), .DropListSelNum1
  TextBox 310,278,60,10, .TextBoxToNum1, ES_NUMBER
  Text 281,278,12,10, "到", .Text3
  Text 147,278,52,10, "依數字區間 : 從", .TextSelNum1
  GroupBox 13,242,515,63, "考量數字欄位篩選條件", .GroupBox3
  CheckBox 24,180,12,10, .ChkBoxSelChar1
  CheckBox 285,180,12,10, .ChkBoxSelChar2
  DropListBox 305,178,100,14, FieldArray_Mix(), .DropListSelChar2
  DropListBox 40,178,100,14, FieldArray_Mix(), .DropListSelChar1
  PushButton 150,180,80,12, "特定文字篩選", .BtnSelSpeChar1
  PushButton 415,180,80,12, "特定文字篩選", .BtnSelSpeChar2
  GroupBox 13,146,515,94, "篩選其他文字欄位，如特定摘要或特定人員(例如：關鍵人員或主管名字)", .GroupBox4
  PushButton 254,110,15,10, "...", .BtnFromDate1
  PushButton 344,110,15,10, "...", .BtnToDate1
  PushButton 254,128,15,10, "...", .BtnFromDate2
  PushButton 344,128,15,10, "...", .BtnToDate2
  CheckBox 25,92,125,10, "核准日期在非工作日之週末", .ChkBoxWeeekend_DocDate
  CheckBox 175,92,100,10, "核准日期在國定假日", .ChkBoxHoliday_DocDate
  PushButton 462,395,70,30, "執行篩選", .BtnRun
  PushButton 19,395,70,30, "離開", .BtnExit
  CheckBox 25,27,100,10, "篩選人工編制傳票", .ChkBoxIsManual
  Text 265,342,25,10, "借方:", .Text8
  Text 265,362,25,10, "貸方:", .Text9
  CheckBox 265,325,70,13, "科目類別配對", .ChkBoxModfRoutA3
  GroupBox 13,305,515,80, "考量特殊科目類別配對條件", .GroupBox5
  CheckBox 25,76,125,10, "總帳日期在非工作日之週末", .ChkBoxWeeekend_PostDate
  CheckBox 175,76,100,10, "總帳日期在國定假日", .ChkBoxHoliday_PostDate
  PushButton 227,395,70,30, "查看篩選結果", .QuickSummary
  Text 206,293,190,10, "↑若僅填入單邊欄位，左邊為<=，右邊為>=。", .Text11
  CheckBox 285,163,199,10, "分錄無摘要描述(即空白摘要)", .ChkBoxIsDescNull
  CheckBox 25,345,85,10, "考量特定科目類別", .ChkBoxNumAcc
  DropListBox 115,344,100,14, FieldArray_AddAccPairing(), .DropListNumAcc
  OptionGroup .AccountparingGroup
  OptionButton 425,325,80,10, "Dr A , Cr B", .0
  OptionButton 425,345,80,10, "Dr A, Cr not B", .1
  OptionButton 425,365,80,10, "Dr not A, Cr B", .2
  CheckBox 320,27,100,10, "僅考量貸方傳票", .ChkBoxCredit
  CheckBox 175,27,100,10, "僅考量借方傳票", .ChkBoxDebit
  CheckBox 320,76,150,10, "需排除總帳日期在補班日/加班日", .ChkBoxMakeUpDay
  CheckBox 320,92,150,10, "需排除核准日期在補班日/加班日", .ChkBoxMakeUpDay1
  GroupBox 13,11,515,33, "使用預先設定之參數條件", .GroupBox1
  TextBox 340,258,100,12, .TextBoxRout4
  CheckBox 24,201,12,10, .ChkBoxSelChar3
  CheckBox 285,201,12,10, .ChkBoxSelChar4
  DropListBox 305,199,100,14, FieldArray_Mix(), .DropListSelChar4
  DropListBox 40,199,100,14, FieldArray_Mix(), .DropListSelChar3
  PushButton 150,201,80,12, "特定文字篩選", .BtnSelSpeChar3
  PushButton 415,201,80,12, "特定文字篩選", .BtnSelSpeChar4
  CheckBox 24,223,12,10, .ChkBoxSelChar5
  CheckBox 285,223,12,10, .ChkBoxSelChar6
  DropListBox 305,221,100,14, FieldArray_num(), .DropListSelChar6
  DropListBox 40,221,100,14, FieldArray_Mix(), .DropListSelChar5
  PushButton 150,223,80,12, "特定文字篩選", .BtnSelSpeChar5
  PushButton 415,223,80,12, "特定數字篩選", .BtnSelSpeChar6
  GroupBox 256,315,265,66, .GroupBox6
  PushButton 305,342,80,14, "特定科目類別選擇", .BtnAccPairingDr
  PushButton 305,362,80,14, "特定科目類別選擇", .BtnAccPairingCr
End Dialog

Begin Dialog ExpWorkPaper 0,5,338,364,"步驟五  產生工作底稿", .ExpWorkPaper_Dlg
  CheckBox 47,58,255,15, "篩選條件 一", .ChkBoxCriteria1
  CheckBox 47,115,255,13, "篩選條件 四", .ChkBoxCriteria4
  CheckBox 47,97,255,12, "篩選條件 三", .ChkBoxCriteria3
  CheckBox 47,133,255,14, "篩選條件 五", .ChkBoxCriteria5
  CheckBox 46,151,255,15, "篩選條件 六", .ChkBoxCriteria6
  CheckBox 46,171,255,14, "篩選條件 七", .ChkBoxCriteria7
  CheckBox 46,190,255,14, "篩選條件 八", .ChkBoxCriteria8
  CheckBox 46,208,255,14, "篩選條件 九", .ChkBoxCriteria9
  CheckBox 46,226,255,13, "篩選條件 十", .ChkBoxCriteria10
  CheckBox 47,78,255,14, "篩選條件 二", .ChkBoxCriteria2
  GroupBox 9,0,306,245, "請選擇要將步驟四測試結果匯出到最終工作底稿之項目", .GroupBox1
  Text 22,29,280,25, "* 請按下每個篩選條件左邊的按鈕，並說明選擇該篩選條件的理由。(若有篩選條件未說明理由，將無法產生篩選結果到工作底稿)", .Text
  PushButton 99,301,110,30, "匯出到工作底稿", .BtnExpWorkPaper
  CancelButton 9,302,40,30, "離開", .CancelButton
  Text 24,262,275,28, .WPMsgText
  Text 22,17,280,12, "* 符合條件的完整分錄都將會自動彙總到JE工作底稿", .Text1
  PushButton 25,61,14,10, "...", .PushButton1
  PushButton 25,98,14,10, "...", .PushButton3
  PushButton 25,134,14,10, "...", .PushButton5
  PushButton 25,172,14,10, "...", .PushButton7
  PushButton 25,208,14,10, "...", .PushButton9
  PushButton 25,80,14,10, "...", .PushButton2
  PushButton 25,116,14,10, "...", .PushButton4
  PushButton 25,153,14,10, "...", .PushButton6
  PushButton 25,191,14,10, "...", .PushButton8
  PushButton 25,226,14,10, "...", .PushButton10
  PushButton 245,301,70,30, "查看篩選結果", .BtnStep4Result
  GroupBox 9,250,306,47, "步驟五　工作底稿產出處理進度", .GroupBox2
End Dialog

Begin Dialog WeekendSelection 0,30,455,105,"步驟二 非工作日之週末設定作業", .initialWeekend_Dlg
  GroupBox 12,12,421,38, "請勾選非工作日之週末", .GroupBox1
  CheckBox 23,27,40,14, "星期一", .ChkBoxMon
  CheckBox 83,27,40,14, "星期二", .ChkBoxTue
  CheckBox 145,27,45,14, "星期三", .ChkBoxWed
  CheckBox 270,27,40,14, "星期五", .ChkBoxFri
  CheckBox 327,27,40,14, "星期六", .ChkBoxSat
  CheckBox 384,28,40,13, "星期日", .ChkBoxSun
  CheckBox 207,27,40,14, "星期四", .ChkBoxThur
  PushButton 390,55,40,20, "選擇", .BtnSelect
  PushButton 17,55,40,20, "取消設定", .BtnCancel
End Dialog

Begin Dialog SpeCharSelect 0,22,284,214,"步驟四 輸入特定篩選文字", .SpeCharSelect_Dlg
  TextBox 15,72,241,32, .TextBoxChar
  Text 15,5,240,10, "額外新增的不同特定文字與日期，請用英文逗號(,)分隔", .TextDes
  PushButton 188,157,40,20, "確認", .BtnSave
  PushButton 36,158,40,20, "取消", .BtnCancel
  Text 15,18,240,22, "例: 如針對挑的文字欄位要篩選是否有符合 金城武 或 林志玲 ，則於下方空格中輸入   金城武,林志玲", .TextDes
  Text 15,43,240,11, "若需要篩選日期，輸入格式為yyyymmdd。", .TextDes
  Text 15,57,240,11, "若需要篩選空值，請輸入JETEAM。", .TextDes
  OptionGroup .OptButtonGroupTxt1
  OptionButton 22,111,100,14, " 包含", .OptButtonTxt1
  OptionButton 137,111,100,15, " 完全符合", .OptButtonTxt2
  OptionButton 22,132,100,15, " 包含就排除", .OptButtonTxt3
  OptionButton 137,132,100,15, " 完全符合才排除", .OptButtonTxt4
End Dialog

Begin Dialog DatePicker 0,31,155,177,"Date Picker", .funCalendar
  PushButton 22,47,15,14, "31", .PB1
  PushButton 36,47,15,14, "31", .PB2
  PushButton 50,47,15,14, "31", .PB3
  PushButton 64,47,15,14, "31", .PB4
  PushButton 78,47,15,14, "31", .PB5
  PushButton 92,47,15,14, "31", .PB6
  PushButton 106,47,15,14, "31", .PB7
  PushButton 22,60,15,14, "31", .PB8
  PushButton 36,60,15,14, "31", .PB9
  PushButton 50,60,15,14, "31", .PB10
  PushButton 64,60,15,14, "31", .PB11
  PushButton 78,60,15,14, "31", .PB12
  PushButton 92,60,15,14, "31", .PB13
  PushButton 106,60,15,14, "31", .PB14
  PushButton 22,73,15,14, "31", .PB15
  PushButton 36,73,15,14, "31", .PB16
  PushButton 50,73,15,14, "31", .PB17
  PushButton 64,73,15,14, "31", .PB18
  PushButton 78,73,15,14, "31", .PB19
  PushButton 92,73,15,14, "31", .PB20
  PushButton 106,73,15,14, "31", .PB21
  PushButton 22,86,15,14, "31", .PB22
  PushButton 36,86,15,14, "31", .PB23
  PushButton 50,86,15,14, "31", .PB24
  PushButton 64,86,15,14, "31", .PB25
  PushButton 78,86,15,14, "31", .PB26
  PushButton 92,86,15,14, "31", .PB27
  PushButton 106,86,15,14, "31", .PB28
  PushButton 22,98,15,14, "31", .PB29
  PushButton 36,98,15,14, "31", .PB30
  PushButton 50,98,15,14, "31", .PB31
  PushButton 64,98,15,14, "31", .PB32
  PushButton 78,98,15,14, "31", .PB33
  PushButton 92,98,15,14, "31", .PB34
  PushButton 106,98,15,14, "31", .PB35
  PushButton 22,111,15,14, "31", .PB36
  PushButton 36,111,15,14, "31", .PB37
  PushButton 50,111,15,14, "31", .PB38
  PushButton 64,111,15,14, "31", .PB39
  PushButton 78,111,15,14, "31", .PB40
  PushButton 92,111,15,14, "31", .PB41
  PushButton 106,111,15,14, "31", .PB42
  PushButton 24,17,10,14, "<", .PBPrevious
  PushButton 111,17,10,14, ">", .PBNext
  Text 24,36,10,10, "Su", .Text1
  Text 39,36,10,10, "Mo", .Text1
  Text 52,36,10,10, "Tu", .Text1
  Text 66,36,10,10, "We", .Text1
  Text 80,36,10,10, "Th", .Text1
  Text 95,36,10,10, "Fr", .Text1
  Text 108,36,10,10, "Sa", .Text1
  Text 39,19,67,10, "Text", .txtYearMonth
  DropListBox 66,2,40,12, list1$(), .lstYear
  Text 33,4,28,8, "Select Year", .Text3
  PushButton 86,130,40,15, "Cancel", .BtnCancel
  PushButton 18,130,40,15, "Clear", .BtnClear
End Dialog

Begin Dialog SpeDateSelect 0,46,171,171,"挑選特定日期", .SpeDateSelect_Dlg
  PushButton 92,10,50,20, "新增日期", .BtnAdd
  PushButton 127,120,26,15, "移除", .BtnRemove
  PushButton 91,38,50,20, "全部移除", .BtnRemoveAll
  PushButton 90,66,50,20, "選擇完畢", .BtnSave
  ListBox 11,5,66,103, TestArray(), .ListBox_DateSel
  PushButton 97,94,40,15, "取消", .BtnExit
  Text 7,114,114,21, "註 : 如僅須取消任一日期，只需在上表點選該日期即可", .Text1
End Dialog

Begin Dialog CriteriaLogSum 0,0,610,329,"步驟四 已保存之篩選條件與測試結果", .CriteriaLogSum_Dlg
  GroupBox 5,3,270,60, "Criteria 1", .GroupBox1
  Text 13,12,255,10, .CD1
  Text 13,23,255,38, .W1_Log
  GroupBox 5,62,270,60, "Criteria 3", .GroupBox3
  Text 13,70,255,10, .CD3
  Text 13,81,255,38, .W3_Log
  GroupBox 5,122,270,60, "Criteria 5", .GroupBox5
  Text 13,130,255,10, .CD5
  Text 13,141,255,38, .W5_Log
  GroupBox 5,181,270,60, "Criteria 7", .GroupBox7
  Text 13,190,255,10, .CD7
  Text 12,200,255,38, .W7_Log
  GroupBox 5,240,270,60, "Criteria 9", .GroupBox9
  Text 13,249,255,10, .CD9
  Text 13,259,255,38, .W9_Log
  GroupBox 278,3,270,60, "Criteria 2", .GroupBox2
  Text 283,12,255,10, .CD2
  Text 283,23,260,38, .W2_Log
  GroupBox 278,62,270,60, "Criteria 4", .GroupBox4
  Text 283,70,255,10, .CD4
  Text 283,81,260,38, .W4_Log
  GroupBox 278,122,270,60, "Criteria 6", .GroupBox6
  Text 283,130,255,10, .CD6
  Text 283,141,260,38, .W6_Log
  GroupBox 278,181,270,60, "Criteria 8", .GroupBox8
  Text 283,190,255,10, .CD8
  Text 283,200,260,38, .W8_Log
  GroupBox 278,239,270,60, "Criteria 10", .GroupBox10
  Text 283,249,255,10, .CD10
  Text 283,259,260,38, .W10_Log
  CancelButton 553,276,40,20, "離開", .CancelButton1
End Dialog

Begin Dialog RationaleForWP 0,44,233,112,"步驟五  高風險條件考量說明", .RationaleForWP_DisplayIT
  TextBox 15,16,196,38, .RationaleText
  PushButton 168,59,40,20, "確認輸入", .BtnOK
  PushButton 15,59,40,21, "取消輸入", .BtnCancel
  Text 17,5,200,11, "請輸入此篩選條件為高風險之理由", .Text1
End Dialog

Begin Dialog TBDetailsHelp1TW 0,35,402,246,"步驟一  期末財務報表準備期間-開始日", .TBDetailsHelp1TW_DisplayIT
  Text 25,21,348,20, "“期末財務報表準備期間-開始日”，表示受查者設定從該日開始進行財務報表的關帳過程，並且期末要入的分錄也是在這時候開始過帳。", .Text1
  Text 24,64,348,11, "案件團隊需要詢問受查者何時開始年底的關帳過程。", .Text2
  Text 24,78,348,20, "在記錄【期末財務報表準備期間-開始日】時，查核團隊可能會遇到以下三個情況。以12月做為年度結束為例：", .Text3
  Text 24,101,348,20, "情況1：假如關帳程序開始在年度結束之前，則[期末財務報表準備期間-開始日期]可能在12月。", .Text4
  Text 24,125,348,25, "情況2：假如關帳程序是在年度結束之後才開始，則[期末財務報表準備期間-開始日期]可能在1月或2月或之後的月份。", .Text5
  Text 24,150,348,35, "情況3：假如關帳程序雖然是在年度結束之後才開始，但受查者調整系統過帳日期(例如設為12/31)，使得系統過帳日早於實際過帳日，因此[期末財務報表準備期間-開始日期]就是受查者調整後之系統過帳日期(在這個例子則為12/31)。", .Text6
  OKButton 330,190,40,20, "離開", .OKButton1
  Text 17,8,356,8, "— 什麼是 “期末財務報表準備期間-開始日”?", .Text7
  Text 15,48,356,9, "— 如何確定 “期末財務報表準備期間-開始日”?", .Text7
End Dialog

Begin Dialog TBDetailsHelp2TW 0,16,407,282,"步驟四  總帳測試母體注意事項!!", .TBDetailsHelp2TW_DisplayIT
  Text 26,20,350,20, "依據KAEG [ISA | 812.1300]說明，JE的攸關母體是指在所辨認的期間可能包含高風險分錄與其他調整之分錄母體與其他調整。", .Text1
  Text 26,161,358,34, "(3). 期末關帳後之財務報導編製流程期間(Post-closing adjustments made during the financial statement closing process)：係指從財務報導編製流程開始日(即結完帳產生財務報表初稿)至出具正式財務報表這段期間。", .Text3
  OKButton 335,229,40,20, "離開", .OKButton1
  Text 18,8,360,11, "— 什麼是 “攸關母體”?", .Text4
  Text 26,41,350,21, "雖然攸關母體可限縮在可能包含高風險分錄與其他調整的母體，但實務上，將攸關母體定義為包含所有分錄與其他調整，可能會較有效率也較具效果。", .Text5
  Text 18,66,358,10, "— JE測試可依發生期間區分不同母體(KAEG [ISA | 809.1700])：", .Text6
  Text 25,80,358,11, "會計分錄和其他調整的整個母體可以區分為三個不同期間的個別分層母體(Sub-population)：", .Text6
  Text 26,95,358,35, "(1). 整個財務報導期間編製的分錄(Journal entries made throughout the period)：係指從財務報導期間開始日至期末結帳開始日，期末結帳開始日依各案件情況可能有所不同，可能是財務報導期間結束日之前、當天或之後。", .Text6
  Text 26,133,358,25, "(2). 期末財務報表期間編製的分錄(Journal entries made at the end of the reporting period)：係指從期末結帳開始日至結完帳後之財務報導編製流程開始日。", .Text4
  Text 26,200,360,24, "上述每個分層母體都可能具有不同的風險特性，而會影響會計分錄及其他調整之測試的性質、時間及範圍。", .Text3
End Dialog

Begin Dialog GLDetailHelp1TW 0,41,213,133,"步驟一  自行定義欄位說明", .GLDetailHelp1TW_Displayit
  Text 10,7,173,30, "針對與您案件總帳分錄檔之高風險分錄篩選有關之其他特定資料欄位(亦為攸關資料元素)，另外自行定義該欄位後，進行資料欄位的配對。", .Text1
  OKButton 144,84,40,20, "OK", .OKButton1
  Text 9,40,173,12, "自行定義之欄位將可於步驟四進階篩選作業中使用。", .Text1
  Text 10,59,173,20, "註 : 前三個自訂欄位可挑選文字或數字型態，第四個自訂欄位可挑選日期型態", .Text1
End Dialog

Begin Dialog RoutinesHelp1TW 0,22,370,249,"步驟三  預篩選查核程序說明", .RoutinesHelp1TW_DisplayIT
  GroupBox 16,112,330,36, "#4.分錄金額中有連續0的尾數(0的位數依分錄金額平均數決定)", .GroupBox2
  Text 29,160,310,11, "- 依分錄編製者列示其分錄過帳筆數與金額的清單", .Text1
  GroupBox 16,149,330,24, "#5.依分錄編製者彙總分錄", .GroupBox1
  Text 29,185,310,11, "- 提供在財務報導期間過帳的科目數量清單", .Text2
  GroupBox 18,38,330,35, "#2. 分錄摘要為特定描述", .GroupBox2
  Text 30,88,310,20, "- 預設將列出分錄貸方為收入，但借方不是應收帳款、現金(含銀行存款)或預收款之傳票。", .Text2
  GroupBox 17,75,330,34, "#3.未預期出現之特定借貸組合", .GroupBox2
  Text 29,123,310,23, "- 將依JE母體中分錄全部借方金額除以資料總筆數來決定0的尾數位數。例如：假如借方平均金額為2,391,281，則設定篩選分錄有000,000的連續尾數者", .Text2
  Text 30,16,310,20, "- 將列出[在期末財務報表準備期間-開始日](即期末關帳開始日)及之後核准的分錄", .Text1
  GroupBox 18,5,330,33, "#1. 於期末財務報表準備期間核准之分錄", .GroupBox1
  Text 31,50,310,22, "- 預設會搜尋包括特定描述文字包含 Adj,Rev,Reclass,Suspense,Error,Wrong,調整,迴轉,沖銷,重分類,避險,重編,錯誤,計畫外,預算外 等文字之分錄 ", .Text2
  GroupBox 16,175,330,24, "#6. 較少使用之科目", .GroupBox2
  OKButton 304,195,40,20, "離開", .OKButton1
End Dialog

Begin Dialog CriterialSelectionHelp1TW 0,19,485,276,"步驟四  作業邏輯與按鍵說明", .CriterialSelectionHelp1TW_DisplayIT
  Text 29,173,345,11, "# 查看篩選結果：將列出您所保留的篩選條件。(預設最多保留10種篩選條件)", .Text1
  Text 29,148,426,20, "# 執行篩選：將顯示每個合併考量條件性質所篩選的傳票數量與明細筆數。如果想要保留該篩選條件，以執行進一步考量，則按下""執行篩選""後，請於跳出的對話方塊中按下""Yes""。", .Text2
  Text 28,190,347,11, "# 匯出彙總報告：匯出已保留所篩選測試結果的分錄明細", .Text2
  Text 29,207,269,11, "# 離開：如已有保留任一篩選結果，則可至步驟五產出工作底稿", .Text1
  OKButton 417,225,40,20, "離開", .OKButton1
  Text 29,20,428,21, "#「使用預先篩選結果」與「使用預先設定之參數條件」勾選項目反灰者表示該項目於對應的關鍵欄位或參數設定未於步驟一至三中進行設定，如需使用該條件當成篩選條件請考量此使用重新執行功能回到對應作業步驟中重新進行作業", .Text2
  Text 29,46,428,11, "# 日期/數字欄位進階篩選邏輯 (依日期/數字區間：A 到 B) ", .Text2
  Text 42,77,417,11, "2. 如未輸入 A 僅輸入 B，則僅篩選大於等於(>=) B 的資料", .Text2
  Text 42,61,415,12, "1. 如僅輸入 A 未輸入 B，則僅篩選小於等於(<=) A 的資料", .Text2
  Text 42,93,414,11, "3. 如 A、B 皆輸入，則篩選介於 A 與 B 間的資料 (含 A 與 B)", .Text2
  Text 36,111,420,11, "註 : 日期進階篩選部分，上述功能與特定日期篩選設定僅可擇一執行", .Text2
  GroupBox 14,8,449,116, "作業邏輯說明", .GroupBox1
  GroupBox 13,132,449,88, "作業按鍵功能說明", .GroupBox1
End Dialog

Begin Dialog UploadExcelFile 0,20,178,225,"步驟二  上傳特定科目配對檔", .UploadExcelFile_Dlg
  PushButton 29,16,115,25, "特定科目配對尚未上傳", .BtnAccountMapping
  PushButton 29,130,115,25, "非工作日之週末尚未設定", .BtnWeekend
  PushButton 29,67,115,25, "國定假日檔案尚未上傳", .BtnHoliday
  PushButton 29,99,115,25, "補班日/結帳日檔案尚未上傳", .BtnMakeUpDay
  PushButton 107,168,40,20, "確認", .BtnOK
  GroupBox 15,5,140,41, "必要設定作業", .GroupBox1
  GroupBox 15,53,140,111, "非必要設定作業", .GroupBox2
End Dialog

Begin Dialog SheetAmountDialog 0,10,280,291,"步驟一  試算表金額邏輯處理", .DealSheetAmount_Dlg
  Text 18,158,70,14, "期末金額：", .t3
  Text 18,110,70,15, "年度變動金額：", .t1
  Text 18,133,70,14, "期初金額：", .t2
  DropListBox 94,110,150,12, TBFieldArray_num(), .DropListBox_SA1
  DropListBox 94,133,150,15, TBFieldArray_num(), .DropListBox_SA2
  DropListBox 94,158,150,15, TBFieldArray_num(), .DropListBox_SA3
  DropListBox 94,185,150,11, TBFieldArray_num(), .DropListBox_SA4
  DropListBox 94,207,150,11, TBFieldArray_num(), .DropListBox_SA5
  GroupBox 6,96,251,130, "試算表金額設定依據：", .GroupBox1
  PushButton 11,30,119,25, "設定年度變動金額", .PushButton_SA1
  PushButton 136,30,119,25, "設定期初與期末金額", .PushButton_SA2
  PushButton 11,64,119,25, "設定借方與貸方金額", .PushButton_SA3
  Text 9,11,247,12, "請於下方按鈕中，選擇處理試算表金額的方法：", .Text1
  PushButton 136,64,119,25, "設定借方貸方之期初期末金額", .PushButton_SA4
  Text 18,184,70,14, "借方金額：", .t4
  Text 18,206,70,14, "貸方金額：", .t5
  PushButton 11,234,40,20, "離開", .BtnCancel
  PushButton 215,234,40,20, "確定", .OKButton_SA1
End Dialog

Begin Dialog AmountDialog 0,5,370,260,"步驟一  總帳傳票金額判斷邏輯設定", .DealAmount_Dlg
  Text 133,181,50,10, "借方標識：", .Text4
  TextBox 200,180,130,14, .TextBox_A1
  Text 133,39,50,10, "傳票金額：", .t1
  Text 133,163,50,10, "借貸別：", .t2
  DropListBox 200,38,130,11, GLFieldArray_num(), .DropListBox_A1
  DropListBox 200,162,130,11, GLFieldArray_mix(), .DropListBox_A5
  GroupBox 107,132,238,69, "【3. 依借貸別判斷】", .GroupBox1
  PushButton 8,138,90,58, "3. 須考慮借貸別", .PushButton_A3
  PushButton 8,24,90,32, "1. 設定傳票金額", .PushButton_A1
  Text 6,5,224,11, "請於下方按鍵中，擇一選擇傳票金額處理方式：", .Text1
  PushButton 8,72,90,51, "2. 分別設定借貸金額", .PushButton_A2
  DropListBox 200,107,130,11, GLFieldArray_num(), .DropListBox_A3
  Text 133,108,50,10, "貸方金額：", .t1
  GroupBox 107,26,238,32, "【1. 設定傳票金額】", .GroupBox1
  DropListBox 200,85,130,11, GLFieldArray_num(), .DropListBox_A2
  Text 133,86,50,10, "借方金額：", .t1
  GroupBox 108,69,238,57, "【2. 借方金額－貸方金額】", .GroupBox1
  Text 133,145,50,10, "傳票金額：", .t1
  DropListBox 200,144,130,11, GLFieldArray_num(), .DropListBox_A4
  PushButton 108,205,40,20, "離開", .BtnCancel
  PushButton 304,205,40,20, "確定", .OKButton_A1
End Dialog

Begin Dialog VaildDialog 0,5,292,210,"步驟一  總帳傳票資料過帳否參數設定", .VaildDialog_Dlg
  Text 22,50,234,42, "例如在【Posted Type】欄位中「Y」代表「傳票已過帳」，若欲篩選出僅「已過帳」之傳票，則於下方下拉式選單挑選【Posted Type】，並於下方過帳代碼欄位輸入關鍵字  「Y」。註 : 如空白欄位值表有效傳票則請輸入「XXXX」。", .t2
  PushButton 169,10,40,20, "需要", .PushButton_V1
  Text 16,14,143,12, "是否需要設定僅篩選已過帳之資料?", .Text1
  PushButton 221,10,40,20, "不需要", .PushButton_V2
  Text 25,126,65,14, "已過帳代碼值：", .Text5
  DropListBox 95,101,150,15, GLFieldArray_Char(), .DropListBox_V1
  Text 24,101,65,12, "過帳狀態欄位：", .Text4
  GroupBox 12,34,254,113, "請設定須包含之欄位與欄位內容關鍵字：", .GroupBox1
  PushButton 225,150,40,20, "確定", .OKButton_V1
  TextBox 95,126,150,15, .TextBox_V1
End Dialog

Begin Dialog IntroductionlHelpDialog 0,25,470,229,"總帳明細帳自動化測試工具注意事項說明", .BtnIntroductionlHelp_Dlg
  Text 21,86,414,21, "# 請先檢視試算表之欄位，因工具關係試算表部分欄位為必要欄位，需先確認必要欄位皆有才能使用Tool。 (必要欄位：會計科目編號、會計科目名稱、金額)", .Text2
  Text 21,110,414,21, "# 請先檢視傳票明細之欄位，因工具關係傳票明細部分欄位為必要欄位，需先確認必要欄位皆有才能使用Tool。 (必要欄位：傳票號碼、會計科目編號、會計科目名稱、總帳日期、傳票摘要、金額及傳票文件項次)", .Text2
  Text 21,135,414,11, "# 請先確認篩選條件是否需額外判斷，如人工傳票或是特殊借貸組合等資訊，則須於使用Tool前以判斷式新增欄位進行判斷", .Text2
  Text 21,152,414,23, "# 因JE Tool將協助比對匯入資料之完整性分析，如資料完整性分析未通過，仍可繼續後續之篩選作業，但須於底稿中紀錄完整性分析差異原因", .Text2
  Text 23,20,206,12, "# 單式傳票", .Text2
  OKButton 405,180,40,14, "OK", .OKButton1
  Text 21,62,414,21, "# 請至IDEA>File>Option檢查”Exclude statistics for Character fields”與”Do not allow the deletion of non-virtual fields”是否已取消勾選", .Text2
  GroupBox 10,5,438,30, "### 請注意，如您的總帳測試資料有下列狀況，則無法採取此自動化工具進行作業 ###", .GroupBox1
  GroupBox 9,45,440,131, "### 以下步驟，請確定皆已執行完畢後，再開始使用JE Tool ", .GroupBox2
End Dialog

Begin Dialog TBDetails 0,37,281,194,"試算表欄位配對", .TBDetail_Dlg
  Text 20,77,55,10, "會計科目編號", .TextAccNum
  Text 20,102,55,10, "會計科目名稱", .TextAccDes
  DropListBox 77,78,80,10, TBFieldArray_mix(), .DropListAccNumTB
  DropListBox 77,101,80,11, TBFieldArray_Char(), .DropListAccDes
  GroupBox 11,10,248,116, "試算表(Trial Balance)", .GroupBox2
  Text 50,34,236,10, "請點選左方按鈕選擇試算表(Trial Balance)檔案", .TextTBFile
  PushButton 17,32,18,12, "...", .BtnTBFile
  Text 19,57,200,10, "請針對所選擇之試算表(Trial Balance)檔案進行欄位配對", .Text3
  PushButton 175,86,80,18, "金額欄位：待處理", .TB_Amount
  PushButton 218,135,40,20, "確定", .BtnNext
  PushButton 14,135,40,20, "取消", .BtnCancel
End Dialog

Begin Dialog AccPairing 0,20,412,239,"選擇特定科目配對", .AccPairing_Dlg
  ListBox 30,33,150,146, tempListbox1(), .ListboxLeft
  ListBox 227,34,150,146, tempListbox2(), .ListBoxRight
  PushButton 191,52,20,14, ">", .PushButton1
  PushButton 191,77,20,14, "<", .PushButton2
  PushButton 191,112,20,14, ">>", .PushButton3
  PushButton 191,137,20,14, "<<", .PushButton4
  OKButton 286,187,40,14, "確認", .OKButton1
  CancelButton 82,185,40,14, "取消", .CancelButton1
  Text 31,9,345,14, "將欲選擇之科目配對，選至右邊的方框中。", .Text1
End Dialog


























































































































































































































































































































































































'Option Explicit

Public Const LOCALE_SSHORTDATE = &H1F       '  short date format string
Public Const LOCALE_SDATE = &H1D            '  date separator
Public Const LOCALE_SYSTEM_DEFAULT& = &H800
Public Const LOCALE_USER_DEFAULT& = &H400


Dim sDayArray(41) As String 'string array to hold days of month
Dim sMonthArray(12) As String 'string array to hold months
Dim sYearArray(11) As String 'string ar
Dim iYear As Integer 'to track the year per the date picker
Dim iMonth As Integer  'to track the month per the date picker
Dim sDate As String 'to hold the date selected from teh date picker
Dim sDefaultDateFormat As String 'to hold the regional date format
Dim sDefaultDateSeperator 'to hold the date separator
Dim sDateDefault As String 'populated in getWeekday function, returns the date in the default format

Dim sFilename As String
Dim sTBFilename As String '選擇的TB檔案名稱
Dim sGLFilename As String '選擇的GL檔案名稱
Dim sTempExcelSource As String '存放Excel Template Files路徑
Dim sFilename1 As String '存放Holiday的檔案狀態
Dim Filename As String

'使用者基本資訊取得
Dim WSHnet As Object 
Dim UserName  As String '記錄使用者IID
Dim UserDomain As String  '記錄使用者網域名稱
Dim UserFullName As String '記錄使用者全部網域名

'讀取資料庫用變數
Dim sProjectFolder  As String
Dim SQLconnStr As String
Dim SQLeqn As String
Dim SQLobjConn, SQLsfT As Object
Dim SQLrs As Object

'讀取後端資料庫存放資訊
Dim sEngagement_Info As String
Dim sPeriod_Start_Date As String
Dim sPeriod_End_Date As String
Dim sLast_Accounting_Period_Date As String
Dim sSTEP_1 As String
Dim sSTEP_2 As String
Dim sSTEP_3 As String
Dim sSTEP_4 As String
Dim sSTEP_5 As String
Dim sSTEP_6 As String
Dim sGL_Finsh As Integer
Dim sTB_Finsh As Integer
Dim sGL_FIle_Name As String
Dim sTB_File_Name As String
Dim sPopulation  As Integer
Dim sIndustry As String
Dim sR1 As String
Dim sR2 As String
Dim sR3 As String
Dim sR4 As String
Dim sR7 As String
Dim sR8 As String
Dim sA2 As String
Dim sA3 As String
Dim sA4 As String
Dim sSEQ_Num As Integer
Dim sW1 As String
Dim sW2 As String
Dim sW3 As String
Dim sW4 As String
Dim sW5 As String
Dim sW6 As String
Dim sW7 As String
Dim sW8 As String
Dim sW9 As String
Dim sW10 As String

Dim sType As String
Dim sDecimals As String
Dim sLen As String
Dim sMsg As String
Dim sTemp As String
Dim amountTotal As Double
Dim amountTotal1 As Double
Dim amountTotal2 As Double
Dim S1Check As Integer
Dim sDateSelect As String
Dim sSelDate1 As String
Dim sSelDate2 As String
Dim sSelChar1 As String
Dim sSelChar2 As String
Dim sSelChar3 As String
Dim sSelChar4 As String
Dim sSelChar5 As String
Dim sSelChar6 As String
Dim sSelType1 As String
Dim sSelType2 As String
Dim sSelType3 As String
Dim sSelType4 As String
Dim sSelType5 As String
Dim sSelType6 As String
Dim sSelAcc1 As String
Dim sSelAcc2 As String
Dim SelDate_check As Integer 
Dim sListArry1(10) As String
Dim sListArry2(10) As String
Dim sLog As String
Dim sLogMemo As String
Dim sTBLogMemo As String
Dim sGLLogMemo As String
Dim TBLogMemo As String
Dim GLLogMemo As String
Dim sTemp1 As String
Dim sTemp2 As String
Dim NewArray() As String
Dim OrangeArray() As String
Dim WPselect(10) As Integer
Dim sA2_Memo As String
Dim sA3_Memo As String
Dim sA4_Memo As String
Dim sTagCount As Integer
Dim C1 As Integer
Dim C2 As Integer
Dim sStep4_Rec_info  As String 
Dim sStep5_Rec_info  As String 


Dim iSelType As Integer
Dim MatchArray() As Variant
Dim EqualArray() As  Variant

'Dim result As Boolean

Dim sL As String '畫面語言控制
Dim sVer As String
Dim sProgVer As String 
Dim sVerControl As String

Dim sRationale1 As String 
Dim sRationale2 As String 
Dim sRationale3 As String 
Dim sRationale4 As String 
Dim sRationale5 As String 
Dim sRationale6 As String 
Dim sRationale7 As String 
Dim sRationale8 As String 
Dim sRationale9 As String 
Dim sRationale10 As String 

Dim Run_S_Time As String
Dim Run_E_Time As String


'================用於試算表的VAR===================
Dim TB_Account_name As String '會計科目欄位
Dim TB_DifAmount_name As String '期間變動金額欄位
Dim TB_First_Amount_Field As String '期初變動金額
Dim TB_Last_Amount_Field  As String '期末變動金額
Dim TB_D_amount As String '借方金額
Dim TB_C_Amount As String '貸方金額
Dim  TB_First_D_Amount  As String '試算表期初借方金額
Dim  TB_Last_D_Amount  As String '試算表期末借方金額
Dim  TB_First_C_Amount  As String '試算表期初貸方金額
Dim  TB_Last_C_Amount As String  '試算表期末貸方金額
Dim status_SA As Integer '用於判斷金額處理方法          0表示未設定  1表示設定變動金額 2表示設定期初與期末金額 3表示設定借方金額與貸方金額 4表示設定期初期末與借方貸方金額

'=============用於傳票的VAR===================
Dim JE_Amount_name As String '金額欄位
Dim JE_DC_name As String '借貸欄位
Dim JE_D_Amount_Field As String '借方金額關鍵字
Dim  JE_D_Amount  As String '借方金額
Dim  JE_C_Amount As String  '貸方金額
Dim status_Amount  As Integer '用於判斷金額處理方法	0表尚未設定；1表不須分辨借貸；2表需要分辨借方金額與貸方金額；3表示依據借貸別區分

Dim JE_Vaild_Field As String
Dim JE_Vaild As String

Dim dlgA As AmountDialog
Dim dlgB As VaildDialog
Dim Dlgw As WeekendSelection

'=============用科目配對多選===================
Dim tempListSelect1 As Integer ' var to hold the last SuppValue before hitting the button - need as a global so it is remembered
Dim tempListSelect2 As Integer ' var to hold the last SuppValue before hitting the button - need as a global so it is remembered
Dim DebitAccounts() As String
Dim CreditAccounts() As String
Dim AccPairingMode As Integer
Dim DebitTemp As String
Dim CreditTemp As String
Dim DebitTemp_Match As String
Dim CreditTemp_Match As String

Dim sExcel As Object
Dim oBook As Object
Dim oBook2 As Object
Dim oSheet As Object
Dim oSheet2 As Object
Dim oRange As Object

Declare Function GetLocaleInfo Lib "kernel32" Alias "GetLocaleInfoA"  (ByVal Locale As Long, ByVal LCType As Long, ByVal lpLCData As String, ByVal cchData As Long) As Long


Sub Main
	Dim dlg1 As Introduction
	Dim dlg2 As Information
	Dim dlg3 As TBDetails
	Dim dlg4 As GLDetails
	Dim dlg5 As Routines
	Dim dlg6 As Criteria
	Dim dlg7 As ExpWorkPaper
	Dim dlg8 As SpeDateSelection
	Dim dlg9 As SpeCharSelect
	Dim dlg10 As SpeDateSelect
	Dim dlg11 As RationaleForWP
	Dim dlg12 As UploadExcelFile
	
	'Suppresses the Overwrite File Warning dialog box from being displayed when set to True and automatically overwrites the file.
	IgnoreWarning(True)
		
	sVer = "TW"  
	sL = "CHT" 
	sProgVer = "V.2019"
	sVerControl = 1  ' 0 為測試版、1為正式版
	
	sStep4_Rec_info  = ""
	sStep5_Rec_info  = ""
	Run_S_Time = ""
	
	Dim objUser As String 
	'取得登入網域的個人帳號資料
	Set WSHnet = CreateObject("WScript.Network")
	Let UserName = WSHnet.UserName
	Let UserDomain = WSHnet.UserDomain
	On Error Resume Next	
	Set objUser = GetObject("WinNT://" & UserDomain & "/" & UserName & ",user")
	Let UserFullName = objUser.FullName
	
	If UserFullName = "" Then UserFullName = UserName 
	
	' Define where the template file put
	sProjectFolder = client.WorkingDirectory
	If sVerControl = 1 Then 
		'sTempExcelSource = "C:\temp"
		sTempExcelSource = "C:\Audit_Tool\JE"
	Else
		sTempExcelSource = "C:\temp\beta"
	End If
	
	If UserName = "derekchan" Then sTempExcelSource = "<user-profile>\Documents\My IDEA Documents\IDEA Projects\HK_JE_TOOL\Macros.ILB"
	
	Dim dstr As String
	dstr = client.WorkingDirectory  & "ProjectOverview.db"
	Set fs = CreateObject("Scripting.FileSystemObject")
	If Not fs.FileExists(dstr)  Then
		sMsg = "請優先使用標準功能建立專案文件，並確保已將總帳與試算表匯入IDEA"
		Result = MsgBox(sMsg ,MB_OK Or MB_ICONEXCLAMATION , "專案料庫建立錯誤訊息!!!")
		Exit Sub
	End If

	Call X_Create_Project_Info_Table  
	
	'TBDetails.OptionButtonGroup1= 1

	Dim Button As Integer
 	Button = Dialog (Introduction)
	
End Sub



Function Intro_Dlg(ControlID$, Action%, SuppValue%)
	Dim Button As Integer
	Dim bExitFunction As Boolean

	
	Select Case Action%
		Case 1   'Default: Action = 1
			DialogTopMost(Flase)
			DlgVisible "CheckBox1", 0
			If UserName = "derekchan" Then DlgVisible "CheckBox1", 1
			
			Call X_Get_Project_Info
											
			If sEngagement_Info = "No Defined"  Then
				dlgText "Text1", UserFullName & ", 歡迎使用 JE Testing Tool. " & Chr(13) & Chr(13) & "此軟體將協助查核團隊從總帳會計分錄與試算表產生IDEA專案文件與JE測試之相關底稿。在開始執行前，請先將會計分錄檔與試算表檔的資料匯入到IDEA。"
			Else
				If Z_File_Exist("#List_of_accounts_with_variance.IDM") = True Then sStep4_Rec_info = GetTotal("#List_of_accounts_with_variance.IDM" ,"" ,"DBCount" )
				
				If sStep4_Rec_info <> 0 Then 
					sTemp = "*** 請注意，完整性測試發現有部分科目出現差異 ***"
				ElseIf sGL_Finsh = 0 Then
					sTemp = " *** 完整性測試作業尚未執行 ***"
				Else
					sTemp = " *** 恭喜您，完整性測試作業無誤 ***"
				End If
	
	       			dlgText "Text1", UserFullName & ", 歡迎使用JE自動化測試工具，下面是案件訊息：" & Chr(13) & Chr(13)  & _ 
	       					" 案件名稱 : " & sEngagement_Info  & Chr(13) & _
						" 財務報導期間 : " & sPeriod_Start_Date & " 到 " & sPeriod_End_Date & Chr(13) & _
						" 期末財務表準備期間的開始日 : " & sLast_Accounting_Period_Date & Chr(13) & sTemp
											
			End If 
			Call Introduction_Button_Control
						
		Case 2   'Click: Action = 2
			
			Select Case ControlID$
										
				Case "BtnDataMap"
					Call Introduction_Button_DisableALL
					Call StepN_Excel_File_Check("STEP1") '檢查使用的Excel來源檔案是否存在 & 是否有被開啟 
					If S1CHECK <> "1" Then 
						ReDim FieldArray_mix(0)
			      			ReDim FieldArray_num(0)
	      					ReDim FieldArray_date(0)
	      					Dim FieldArray_Char(0)
						Button = Dialog (Information)
		   		   		Call X_Get_Project_Info 
						If Z_File_Exist("#List_of_accounts_with_variance.IDM") = True Then sStep4_Rec_info = GetTotal("#List_of_accounts_with_variance.IDM" ,"" ,"DBCount" )
						If sStep4_Rec_info <> 0 Then 
							sTemp = "*** 請注意，完整性測試發現有部分科目出現差異 ***"
						ElseIf sGL_Finsh = 0 Then
							sTemp = " *** 完整性測試作業尚未執行 ***"
						Else
							sTemp = " *** 恭喜您，完整性測試作業無誤 ***"
						End If
			       			dlgText "Text1", UserFullName & ", 歡迎使用JE自動化測試工具，下面是案件訊息：" & Chr(13) & Chr(13)  & _ 
								" 案件名稱 : " & sEngagement_Info & Chr(13) & _
								" 財務報導期間 : " & sPeriod_Start_Date & " 到 " & sPeriod_End_Date & Chr(13) & _
								" 期末財務表準備期間的開始日 : " & sLast_Accounting_Period_Date & Chr(13) & sTemp
      		      				Call Introduction_Button_Control
      		      			End If
						
       				Case "BtnLoadfile"
       				
					Call Introduction_Button_DisableALL

					Button = Dialog (UploadExcelFile)
      						
      					Call X_Get_Project_Info
      					Call Introduction_Button_Control
      						      						
       				Case "BtnRoutine"
					'Call StepN_Excel_File_Check("STEP3") '檢查使用的Excel來源檔案是否存在 & 是否有被開啟 
					'Call AddAccPairing_DC
					
					'If S1CHECK <> "1" Then
						'Call Introduction_Button_DisableALL
						'S3Run :
						'Button = Dialog (Routines)
						'If s1Check = 1 Then GoTo S3Run
						'Call X_Get_Project_Info
						'Call Introduction_Button_Control
					'End If
			      		
			      		
				Case "BtnCriteria"
					Call Introduction_Button_DisableALL
					Call GetTagFieldName
					Call AddAccPairing_DC
					
					S1Check = "0"
					ReCriteria :
					If S1Check = "0" Then Button = Dialog (Criteria) 
					If S1Check = "1" Then 
						S1Check = "0"
						GoTo ReCriteria
					End If
					
					Call X_Get_Project_Info
			            		Call Introduction_Button_Control
			            	
			            	Case "BtnIntroductionlHelp" 
		            			Button = Dialog (IntroductionlHelpDialog)
		            			
      				Case "BtnExport"
      					Call Introduction_Button_DisableALL
					Button = Dialog (ExpWorkPaper)
					Call X_Get_Project_Info
					Call Introduction_Button_Control
					Shell "cmd.exe /c rd /s /q %systemdrive%\$Recycle.bin" 
										
              			Case "BtnReRun"
              				Call Introduction_Button_DisableALL
		        		Button = Dialog (ReRunDialog)
		        		Call X_Get_Project_Info
			       		Call Introduction_Button_Control
			       		Shell "cmd.exe /c rd /s /q %systemdrive%\$Recycle.bin" 

					If sEngagement_Info = "No Defined"  Then 
						dlgText "Text1", UserFullName & ", 歡迎使用 JE Testing Tool. " & Chr(13) & Chr(13) & "此軟體將協助查核團隊從總帳會計分錄與試算表產生IDEA專案文件與JE測試之相關底稿。在開始執行前，請先將會計分錄檔與試算表檔的資料匯入到IDEA。"
					Else
						If Z_File_Exist("#List_of_accounts_with_variance.IDM") = True Then sStep4_Rec_info = GetTotal("#List_of_accounts_with_variance.IDM" ,"" ,"DBCount" )
						If sStep4_Rec_info <> 0 Then 
							sTemp = "*** 請注意，完整性測試發現有部分科目出現差異 ***"
						ElseIf sGL_Finsh = 0 Then
							sTemp = " *** 完整性測試作業尚未執行 ***"
						Else
							sTemp = " *** 恭喜您，完整性測試作業無誤 ***"
						End If
			       			dlgText "Text1", UserFullName & ", 歡迎使用JE自動化測試工具，下面是案件訊息：" & Chr(13) & Chr(13)  & _ 
			       						" 案件名稱 : " & sEngagement_Info & Chr(13) & _
									" 財務報導期間 : " & sPeriod_Start_Date & " 到 " & sPeriod_End_Date & Chr(13) & _
									" 期末財務表準備期間的開始日 : " & sLast_Accounting_Period_Date & Chr(13) & sTemp
					End If 

              		Case "BtnCancel"
			
				bExitFunction = True
              	

			End Select                                          

	End Select
         
	If  bExitFunction Then
		Intro_Dlg = 0
	Else 
		Intro_Dlg = 1
	End If
        
End Function

Function Info_Dlg(ControlID$,Action%,SuppValue%)

	Dim Button As Integer
	Dim dlg As dlgDatePicker
	Dim result As Boolean
	Dim sCheck As Integer
	Dim sTxT As String
	Dim bExitFunction As Boolean

	
	Select Case Action%
		Case 1
			If TBLogMemo = "OK" Then 
				Dlgtext "TB_Dealing", "試算表：已處理"
			Else
				Dlgtext "TB_Dealing", "試算表：待處理"
			End If
			If GLLogMemo = "OK" Then 
				Dlgtext "GL_Dealing", "傳票：已處理"
			Else
				Dlgtext "GL_Dealing", "傳票：待處理"
			End If
			If sEngagement_Info = "No Defined"  Then

				DlgEnable "TextBoxStartDate", 1
				DlgEnable "TextBoxLastDate", 1
				DlgEnable "TextBoxEndDate", 1
				dlgText "TextBoxEntityName", "" 
				dlgText "TextBoxStartDate", Format(DateValue(Now),"YYYY")-1 & "0101"
				dlgText "TextBoxLastDate", Format(DateValue(Now),"YYYY")-1 & "1231"
				dlgText "TextBoxEndDate", Format(DateValue(Now),"YYYY")-1 & "1231"
		
				DlgEnable "TB_Amount",  0
										
				ReDim IndustryArray(46)
						
				IndustryArray(0) = "No Select"
				IndustryArray(1) = "General Manufacturing"
				IndustryArray(2) = "Advertising"
				IndustryArray(3) = "Automotive"
				IndustryArray(4) = "Benefit Plans"
				IndustryArray(5) = "Broadcast"
				IndustryArray(6) = "Business Services"
				IndustryArray(7) = "Captive Insurance"
				IndustryArray(8) = "Chemicals"
				IndustryArray(9) = "Cloud"
				IndustryArray(10) = "Communication"
				IndustryArray(11) = "Constuction"
				IndustryArray(12) = "Consumer Products"
				IndustryArray(13) = "Durable Consumer Goods"
				IndustryArray(14) = "Education"
				IndustryArray(15) = "Electronics"
				IndustryArray(16) = "General Banking"
				IndustryArray(17) = "Government Central"
				IndustryArray(18) = "Government Federal"
				IndustryArray(19) = "Government Local"
				IndustryArray(20) = "Healthcare Payors"
				IndustryArray(21) = "Healthcare Providers"
				IndustryArray(22) = "Hotels"
				IndustryArray(23) = "Industrial Products"
				IndustryArray(24) = "Investment Banking"
				IndustryArray(25) = "Investment Management"
				IndustryArray(26) = "Investments Hedge Funds"
				IndustryArray(27) = "Investments Mutual Funds"
				IndustryArray(28) = "Leasing"
				IndustryArray(29) = "Life Insurance"
				IndustryArray(30) = "Mining"
				IndustryArray(31) = "Non Life Insurance"
				IndustryArray(32) = "Not for Profit"
				IndustryArray(33) = "Oil and Gas"
				IndustryArray(34) = "Pharmaceuticals"
				IndustryArray(35) = "Power and Utilities"
				IndustryArray(36) = "Professional Services"
				IndustryArray(37) = "Publishing"
				IndustryArray(38) = "Real Estate Development"
				IndustryArray(39) = "Real Estate Investment"
				IndustryArray(40) = "Reinsurance"
				IndustryArray(41) = "Retail"
				IndustryArray(42) = "Software"
				IndustryArray(43) = "Sports Teams"
				IndustryArray(44) = "Transport Aviation"
				IndustryArray(45) = "Transport Road and Rail Freight"
				IndustryArray(46) = "Wholesale"
						
				DlgListboxArray "Industry_DropListBox", IndustryArray()				
			Else 
				DlgEnable "TextBoxStartDate", 1
				DlgEnable "TextBoxLastDate", 1
				DlgEnable "TextBoxEndDate", 1
				dlgText "TextBoxEntityName", sEngagement_Info
				dlgText "TextBoxStartDate", sPeriod_Start_Date
				dlgText "TextBoxLastDate", sLast_Accounting_Period_Date
				dlgText "TextBoxEndDate", sPeriod_End_Date
		
				DlgEnable "TB_Amount",  0
										
				ReDim IndustryArray(46)
						
				IndustryArray(0) = sIndustry
				IndustryArray(1) = "General Manufacturing"
				IndustryArray(2) = "Advertising"
				IndustryArray(3) = "Automotive"
				IndustryArray(4) = "Benefit Plans"
				IndustryArray(5) = "Broadcast"
				IndustryArray(6) = "Business Services"
				IndustryArray(7) = "Captive Insurance"
				IndustryArray(8) = "Chemicals"
				IndustryArray(9) = "Cloud"
				IndustryArray(10) = "Communication"
				IndustryArray(11) = "Constuction"
				IndustryArray(12) = "Consumer Products"
				IndustryArray(13) = "Durable Consumer Goods"
				IndustryArray(14) = "Education"
				IndustryArray(15) = "Electronics"
				IndustryArray(16) = "General Banking"
				IndustryArray(17) = "Government Central"
				IndustryArray(18) = "Government Federal"
				IndustryArray(19) = "Government Local"
				IndustryArray(20) = "Healthcare Payors"
				IndustryArray(21) = "Healthcare Providers"
				IndustryArray(22) = "Hotels"
				IndustryArray(23) = "Industrial Products"
				IndustryArray(24) = "Investment Banking"
				IndustryArray(25) = "Investment Management"
				IndustryArray(26) = "Investments Hedge Funds"
				IndustryArray(27) = "Investments Mutual Funds"
				IndustryArray(28) = "Leasing"
				IndustryArray(29) = "Life Insurance"
				IndustryArray(30) = "Mining"
				IndustryArray(31) = "Non Life Insurance"
				IndustryArray(32) = "Not for Profit"
				IndustryArray(33) = "Oil and Gas"
				IndustryArray(34) = "Pharmaceuticals"
				IndustryArray(35) = "Power and Utilities"
				IndustryArray(36) = "Professional Services"
				IndustryArray(37) = "Publishing"
				IndustryArray(38) = "Real Estate Development"
				IndustryArray(39) = "Real Estate Investment"
				IndustryArray(40) = "Reinsurance"
				IndustryArray(41) = "Retail"
				IndustryArray(42) = "Software"
				IndustryArray(43) = "Sports Teams"
				IndustryArray(44) = "Transport Aviation"
				IndustryArray(45) = "Transport Road and Rail Freight"
				IndustryArray(46) = "Wholesale"
						
				DlgListboxArray "Industry_DropListBox", IndustryArray()
				
			End If	
				
				
		Case 2
			Select Case ControlID$
				Case "BtnLastDate"
					sDate = Information.TextBoxLastDate
					Button = Dialog (DatePicker)
					dlgText "TextBoxLastDate", sDate
					
					
				Case "BtnStartDate"
					sDate = Information.TextBoxStartDate
					Button = Dialog (DatePicker)
					dlgText "TextBoxStartDate", sDate
					
				Case "BtnEndDate"
					sDate = Information.TextBoxEndDate
					Button = Dialog (DatePicker)
					dlgText "TextBoxEndDate", sDate
					
				Case "TBDetails_Help1"
					Button = Dialog (TBDetailsHelp1TW)
	
				Case "TBDetails_Help2"
					Button = Dialog (TBDetailsHelp2TW)			
			
				Case "TB_Dealing"
					Button=Dialog(TBDetails)
					If TBLogMemo = "OK" Then 
						Dlgtext "TB_Dealing", "試算表：已處理"
					Else
						Dlgtext "TB_Dealing", "試算表：待處理"
					End If

					
				Case "GL_Dealing"
					Button=Dialog(GLDetails)
					If GLLogMemo = "OK" Then 
						Dlgtext "GL_Dealing", "傳票：已處理"
					Else
						Dlgtext "GL_Dealing", "傳票：待處理"
					End If

				Case"BtnWP"
					Call SendMailtoDorian
					'dlgText "Txt_WP", "檔案上傳中，請稍後..."
						'Call Z_Delete_File("#AccountMapping#-AccountMapping.IDM") 
						'Call Z_Delete_File("#AccountMapping.IDM")
						'Call Z_Delete_File("#AccountMapping_Sum.IDM")
						'Call Z_Delete_File("#AccountMapping_R.IDM")
						'Call Z_Delete_File("#AccountMapping_C.IDM")
					'Call Step2_Upload_AccountMapping_File
						
				Case "BtnNext"
					If Information.TextBoxEntityName = "" Or Information.TextBoxLastDate = "" Or  Information.TextBoxStartDate = "" Or Information.TextBoxEndDate = "" Then
						sMsg = "請完成所有要求之【基本案件資訊】欄位"
						Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一  未設定基本案件資訊 警示訊息")
					ElseIf Information.TextBoxStartDate > Information.TextBoxEndDate Or Information.TextBoxStartDate = Information.TextBoxEndDate Then
						sMsg = "財務報表期間 - 結束日期早於開始日期"
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  基本資訊 財務報表期間設定錯誤 警示訊息")
					ElseIf Information.TextBoxStartDate > Information.TextBoxLastDate Or Information.TextBoxStartDate = Information.TextBoxLastDate Then
						sMsg = "期末財務報告流程開始日早於財務報表期間開始日期"
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  基本資訊 基本案件資訊財務作業期間錯誤 資訊警示訊息")
					ElseIf TBLogMemo<>"OK" Then
						sMsg="請完成【試算表】"
						Result=MsgBox(sMsg,MB_OK Or MB_ICONEXCELAMATION,"步驟一 試算表未完成 警示訊息")
										
					ElseIf GLLogMemo<>"OK" Then
						sMsg="請完成【傳票】"
						Result=MsgBox(sMsg,MB_OK Or MB_ICONEXCELAMATION,"步驟一 傳票未完成 警示訊息")
						
					Else 
						
						Call X_Update_Project_Info(Information.TextBoxEntityName, Information.TextBoxStartDate , Information.TextBoxEndDate ,  Information.TextBoxLastDate,  IndustryArray(Information.Industry_DropListBox))
					
					
						Call Z_Delete_File("#TB#.IDM")
						Call Z_Delete_File("#GL#.IDM")

						sMsg = "處理程序 1/12 - 查核之試算表的前置處理"
						dlgText "Text1", sMsg 							
						Call Z_DirectExtractionTable(iSplit(sTBFilename,"","\",1,1), "#TB#.IDM" , "")
						If sTemp = "1" Then Exit Function
						
						'如欄位為數字型態，轉變為文字型態 - TB只有會計科目要處理
						Call Z_Field_Info("#TB#.IDM", TBFieldArray_mix(TBDetails.DropListAccNumTB))
						If sType = "WI_VIRT_CHAR" Then 
							Result = Z_renameFields("#TB#.IDM", TBFieldArray_mix(TBDetails.DropListAccNumTB), "會計科目編號_TB")
						Else
							Result = Z_renameFields("#TB#.IDM", TBFieldArray_mix(TBDetails.DropListAccNumTB), "會計科目編號_TB_Temp")
							Result = Z_Modidy_Field_Num_to_Char("#TB#.IDM", "會計科目編號_TB_Temp", "會計科目編號_TB")
						End If 
						Result = Z_renameFields("#TB#.IDM",TBFieldArray_Char(TBDetails.DropListAccDes), "會計科目名稱_TB")
						'Result = Z_renameFields("#TB#.IDM", TBFieldArray_num(TBDetails.DropListBegBal), "Opening_Balance_TB")
						'Result = Z_renameFields("#TB#.IDM", TBFieldArray_num(TBDetails.DropListEndBal), "Ending_Balance_TB")
						
						If status_SA = 1 Then Result = Z_renameFields("#TB#.IDM", TB_DifAmount_name, "試算表變動金額_TB")
						If status_SA = 2 Then Call Step1_TB_Amount_Append_2
						If status_SA = 3 Then Call Step1_TB_Amount_Append_3
						If status_SA = 4 Then Call Step1_TB_Amount_Append_4
						
						'Call X_Update_Project_Info(TBDetails.TextBoxEntityName, TBDetails.TextBoxStartDate , TBDetails.TextBoxEndDate ,  TBDetails.TextBoxLastDate,  IndustryArray(TBDetails.Industry_DropListBox))
						Call X_Update_Project_Info_FileName("TB_File", iSplit(sTBFilename,"","\",1,1))
						'sTBFileName = ""
						
						'Call Introduction_Button_Control

						'Choice = TBDetails.OptionButtonGroup1
					
						'Select Case Choice
						'	Case 0 
						'		Call X_Update_Step_Info("Population", 0)
						'	Case 1 
								Call X_Update_Step_Info("Population", 1)
						'End Select
						
						Run_E_Time = DateValue(Now) & " " & TimeValue(Now)
                        					
                       					DlgEnable "BtnRun", 0
                            					DlgEnable "BtnCancel", 0
                       					Call X_Get_Project_Info
                       					
						sMsg = "處理程序 2/12 - 篩選查核期間的總帳傳票明細"
						dlgText "Text1", sMsg 
						
						sTemp = "@BetweenDate( " & GLFieldArray_date(GLDetails.DropListPosDate) & " ,"& Chr(34) & iRemove( sPeriod_Start_Date , "/") & Chr(34) & "," & Chr(34) & iRemove( sPeriod_End_Date , "/") & Chr(34) & ")" 
						
						If sLog = "OK" Then 
							If JE_Vaild = "XXXX" Then 
								sTemp = sTemp & " .AND. @IsBlank( " & JE_Vaild_Field  & " )"
							Else
								sTemp = sTemp & " .AND. " & JE_Vaild_Field  & " = " & Chr(34) & JE_Vaild & Chr(34)
							End If
						End If
						
						Call Z_DirectExtractionTable(iSplit(sGLFilename,"","\",1,1), "#GL#.IDM", sTemp) 
						
						
						sMsg = "處理程序 3/12 - 進行欄位配對名稱異動作業"
						dlgText "Text1", sMsg 

						If  GLFieldArray_Num(GLDetails.DropListManual) <> "Select..." Then Result = Z_renameFields("#GL#.IDM", GLFieldArray_Num(GLDetails.DropListManual), "人工傳票否_JE_S") 	
						
						'如欄位為數字型態，轉變為文字型態 - 須考量的傳票編號和科目代碼
						Call Z_Field_Info("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListDocNum))
						If sType = "WI_VIRT_CHAR" Then 
							Result = Z_renameFields("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListDocNum), "傳票號碼_JE")
						Else
							Result = Z_renameFields("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListDocNum), "傳票號碼_JE_Temp")
							Result = Z_Modidy_Field_Num_to_Char("#GL#.IDM", "傳票號碼_JE_Temp", "傳票號碼_JE")
						End If 

						Call Z_Field_Info("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListAccNumGL))
						If sType = "WI_VIRT_CHAR" Then 
							Result = Z_renameFields("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListAccNumGL), "會計科目編號_JE")
						Else
							Result = Z_renameFields("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListAccNumGL), "會計科目編號_JE_Temp")
							Result = Z_Modidy_Field_Num_to_Char("#GL#.IDM", "會計科目編號_JE_Temp", "會計科目編號_JE")
						End If 
													
						'以下無影響
						Result = Z_renameFields("#GL#.IDM",GLFieldArray_mix(GLDetails.DropListDes), "傳票摘要_JE")
						Result = Z_renameFields("#GL#.IDM", GLFieldArray_date(GLDetails.DropListPosDate), "總帳日期_JE")
						'Result = Z_renameFields("#GL#.IDM", GLFieldArray_num(GLDetails.DropListAmount), "傳票金額_JE")
						Result = Z_renameFields("#GL#.IDM", GLFieldArray_date(GLDetails.DropListDocDate), "傳票核准日_JE")
						
						Result = Z_renameFields("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListCreateBy), "傳票建立人員_JE")
						Result = Z_renameFields("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListApprovBy), "傳票核准人員_JE")
						Result = Z_renameFields("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListAccName), "會計科目名稱_JE")
						Result = Z_renameFields("#GL#.IDM", GLFieldArray_num(GLDetails.DropListLineID), "傳票文件項次_JE_S")
						Result = Z_renameFields("#GL#.IDM", GLFieldArray_mix(GLDetails.DropListJESource), "分錄來源模組_JE")
						
						If iAllTrim(VaildDialog.TextBox_V1) <> ""   Then
							Call Step1_GL_Vaild_Field
						End If
						
						
						If GLFieldArray_mix(GLDetails.DropListUserDefind1) <> "Select..." Then
							If iAllTrim(GLDetails.UseDefind1) <> ""  Then 
								Result = Z_renameFields("#GL#.IDM",GLFieldArray_mix(GLDetails.DropListUserDefind1), iAllTrim(GLDetails.UseDefind1) & "_JE")
							Else
								Result = Z_renameFields("#GL#.IDM",GLFieldArray_mix(GLDetails.DropListUserDefind1), GLFieldArray_mix(GLDetails.DropListUserDefind1) & "_JE")
							End If
						End If
												
						If GLFieldArray_mix(GLDetails.DropListUserDefind2) <> "Select..." Then
							If iAllTrim(GLDetails.UseDefind2) <> ""  Then
								Result = Z_renameFields("#GL#.IDM",GLFieldArray_mix(GLDetails.DropListUserDefind2), iAllTrim(GLDetails.UseDefind2) & "_JE")
							Else
								Result = Z_renameFields("#GL#.IDM",GLFieldArray_mix(GLDetails.DropListUserDefind2), GLFieldArray_mix(GLDetails.DropListUserDefind2) & "_JE")
							End If
						End If

						If GLFieldArray_mix(GLDetails.DropListUserDefind3) <> "Select..." Then
							If iAllTrim(GLDetails.UseDefind3) <> "" Then
								Result = Z_renameFields("#GL#.IDM",GLFieldArray_mix(GLDetails.DropListUserDefind3), iAllTrim(GLDetails.UseDefind3) & "_JE")
							Else
								Result = Z_renameFields("#GL#.IDM",GLFieldArray_mix(GLDetails.DropListUserDefind3), GLFieldArray_mix(GLDetails.DropListUserDefind3) & "_JE")
							End If
						End If
						
						If GLFieldArray_date(GLDetails.DropListUserDefind4) <> "Select..." Then
							If iAllTrim(GLDetails.UseDefind4) <> "" Then
								Result = Z_renameFields("#GL#.IDM",GLFieldArray_date(GLDetails.DropListUserDefind4), iAllTrim(GLDetails.UseDefind4) & "_JE")
							Else
								Result = Z_renameFields("#GL#.IDM",GLFieldArray_date(GLDetails.DropListUserDefind4), GLFieldArray_date(GLDetails.DropListUserDefind4) & "_JE")
							End If
						End If
						
						'If status_Amount = 1 Then Result = Z_renameFields("#GL#.IDM",JE_Amount_name ,"傳票金額_JE")
						If status_Amount = 1 Then Call Step1_GL_Amount_Append_1
						If status_Amount = 2 Then Call Step1_GL_Amount_Append_2
						If status_Amount = 3 Then Call Step1_GL_Amount_Append_3
						
						If GLDetails.CK_Approval_Date  = 1 Then Call Step1_Approval_Date_Append
																			
						sMsg = "處理程序 4/12 - 篩選是否有 無會計科目編號之分錄"
						dlgText "Text1", sMsg 
						Call getNulls("#GL#.IDM", "會計科目編號_JE", "#Null-GL_Account.IDM")

						sMsg = "處理程序 5/12 - 篩選是否有 無傳票號碼之分錄"
						dlgText "Text1", sMsg 
						Call getNulls("#GL#.IDM", "傳票號碼_JE", "#Null-GL_Number.IDM")

						sMsg = "處理程序 6/12 - 篩選是否有 無傳票摘要之分錄"
						dlgText "Text1", sMsg 
						Call getNulls("#GL#.IDM", "傳票摘要_JE", "#Null-GL_Description_Criteria.IDM")

						sMsg = "處理程序 7/12 - 篩選是否有 傳票核准日不在查核會計期間內之分錄"
						dlgText "Text1", sMsg 
						
						If  FindField("#GL#.IDM","傳票核准日_JE") <> 0  Then 
							sTemp = " .NOT.  @BetweenDate( 傳票核准日_JE ,"& Chr(34) & iRemove( sPeriod_Start_Date , "/") & Chr(34) & "," & Chr(34) & iRemove( sPeriod_End_Date , "/") & Chr(34) & ")" 
							Call Z_DirectExtractionTable("#GL#.IDM", "#NotinPeriod-ApprovalDate.IDM", sTemp)
						End If 

						sMsg = "處理程序 8/12 - 完整性測試作業"
						dlgText "Text1", sMsg 
						Call Step1_Validation	

						sMsg = "處理程序 9/12 - 檢查是否有完整性異常之科目"
						dlgText "Text1", sMsg 
						sTemp = "@Abs( DIFF ) > 0"
						Call Z_DirectExtractionTable( "#Completeness_Check.IDM" , "#List_of_accounts_with_variance.IDM" ,  sTemp)
					
						sMsg = "處理程序 10/12 - 將完整性測試結果匯出到Excel表"
						dlgText "Text1", sMsg 

						Call X_Update_Project_Info_FileName("GL_File", iSplit(sGLFilename,"","\",1,1))

						Call Step1_Export_Excel	
						
						If S1Check = 1 Then 
							sMsg = "所匯入的資料未能順利通過完整性驗證程序，請檢視Validation report中工作表-V_Report 5中有差異的科目，並檢視其他相關驗證程序檢查的內容後，" & _ 
								"再請評估是否將於最終底稿中進行差異說明，或是將相關資料檔進行適當更新或處理後，重新執行程式！" & Chr(13) & Chr(13) & _ 
								"Validation report產生的位置如下：" & Client.WorkingDirectory  & "Exports.ILB\ValidationReport.xlsx "
							Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一  完整性未測試通過 警示訊息")

							GoTo Step1_Accounting_Mapping
						
						Else 
							Step1_Accounting_Mapping :
							sMsg = "處理程序 11/12 - 產出會計科目類別設定Excel表"
							dlgText "Text1", sMsg 
							
							Call Step1_Export_AccountMapFile

							Run_E_Time = DateValue(Now) & " " & TimeValue(Now)
							'If Introduction.CheckBox1 = 1  Then Call X_SendMail_Step("步驟一 INF")
							'sMsg = "產生INF測試樣本 ?" & Chr(13) & Chr(13) & _
							'	"是否要對傳票分錄檔產生執行INF測試的樣本？" & Chr(13) & _
							'	"(即隨機產生60筆樣本，以測試攸關之非財務資料元素的正確性)" & Chr(13) & Chr(13) & _
							'	"當這些攸關資料元素正確性的測試，已透過其他細項測試程序取得足夠適當的查核證據時，才會選擇[否]。"
							'Results = MsgBox(sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟一 是否需隨機產生INF樣本 ?")
							'If Results = IDYES Then
								sMsg = "處理程序 12/12 - 隨機產生INF樣本檔"
								dlgText "Text1", sMsg 
								Call Step1_Export_INFFile
								Call Step1_Export_INF_Report
								sTemp1 = "INF_Y"
							'Else 						
							'	sMsg = "處理程序 12/12 - 隨機產生INF樣本檔"
							'	dlgText "Text1", sMsg 
							'	sTemp1 = "INF_N"
							'End If 

							'sGLFileName = ""
							sTB_Finsh = 1
							Call X_Update_Step_Info("TB_Finsh", sTB_Finsh)
							sGL_Finsh = 1
							Call X_Update_Step_Info("GL_Finsh", sGL_Finsh)
							Call X_Update_Step_Info("STEP_1",0)
							Call X_Update_Step_Info("STEP_2",1)
							Call X_Update_Step_Info("STEP_6",1)
							Call X_Update_Step_User_Info("STEP_1", UserFullName, "STEP_1_Time", DateValue(Now) & " " & TimeValue(Now))
								
							sMsg = "恭喜您已經完成步驟一作業 ...."
							dlgText "Text1", sMsg 

							'檔案產生訊息
							sMsg = "這個步驟已產生下列相關文件到下面路徑： " & Client.WorkingDirectory  & "Exports.ILB\ " & Chr(13) & Chr(13) & _
								" 1. 檢驗資料內容的Validation  Report，檔名為ValidationReport.xlsx " & Chr(13) & Chr(13) & _
								" 2. 設定特定科目配對的Account Mapping Template，檔名為AccountMapping.xlsx" & Chr(13) & Chr(13)
							If sTemp1 = "INF_Y" Then
								sMsg =  sMsg & " 3. 確認攸關資料元素可靠性的INF Testing Report，檔名為INFReport.xlsx" & Chr(13) & _
									"(作為步驟 1的其中一部分，查核團隊需測試INF Testing Report所選樣本之攸關資料欄位的可靠性(此處測試的欄位係指會運用在JE高風險篩選條件的非財務性質資料元素)。" & _
									"而在之後步驟 5產生JE工作底稿後，查核團隊則需要將INF Testing Report所選樣本於資料可靠性的測試結果複製到該工作底稿中。)" & Chr(13) & Chr(13) 
							End If 
								sMsg =  sMsg & "接下來，請於上述提到之AccountMapping檔案中設定對特定會計科目於預設的標準科目進行配對(預設的標準科目為現金、收入、應收帳款等科目，可自行新增)"   & _
									"，以能後面的步驟對不尋常之科目借貸組合進行篩選。完成科目配對後，再進行此JE測試工具之步驟2 -上傳特定科目配對的檔案。"
							Result = MsgBox(sMsg ,MB_OK Or MB_ICONINFORMATION , "恭喜您已經完成步驟一作業")
								
							Set runScript =  CreateObject("WScript.Shell")
							runScript.Run "explorer.exe /e," & client.WorkingDirectory & "Exports.ILB"
							
							Shell "cmd.exe /c rd /s /q %systemdrive%\$Recycle.bin"
								
							Run_E_Time = DateValue(Now) & " " & TimeValue(Now)
							If Introduction.CheckBox1 = 1  Then Call X_SendMail_Step("步驟一")
							
																	
							bExitFunction = True
						End If 
					End If
					
					CheckAgain:
                            					DlgEnable "BtnRun", 1
                            					DlgEnable "BtnCancel", 1
					
									
				Case "BtnCancel"
					bExitFunction=True
			End Select
			
	End Select
	
	If bExitFunction Then
		Info_Dlg=0
	Else
		Info_Dlg=1
	End If
	

End Function

Function TBDetail_Dlg(ControlID$, Action%, SuppValue%)
	Dim Button As Integer
    	Dim dlg As dlgDatePicker
    	Dim result As Boolean
    	Dim sCheck As Integer
    	Dim sTxT As String
    	Dim bExitFunction As Boolean
	Select Case Action%
		Case 1
		
                                           	
			'DlgEnable "TB_Amount",  0
			If sTBFilename<>"" Then
				If sTBLogMemo = "OK" Then 
					Dlgtext "TB_Amount", "金額欄位：已處理"
					DlgEnable "TB_Amount",  1
				Else
					Dlgtext "TB_Amount", "金額欄位：待處理"
					DlgEnable "TB_Amount",  1
				End If
			Else
				Dlgtext "TB_Amount", "金額欄位：待處理"
				DlgEnable "TB_Amount",  0
			End If

								
		
		Case 2
			Select Case ControlID$
				Case "BtnTBFile" 
					Call GetTBFile
					If sTBFilename<>"" Then
						Call TBGetFieldName(sTBFilename)
						Call TBFile_Array
						DlgEnable "TB_Amount", 1
						sTBLogMemo = "NA"
						TBLogMemo = "NA"  
						Dlgtext "TB_Amount", "金額欄位：待處理"
					Else
						DlgEnable "TB_Amount", 0
					End If
					
				Case "TB_Amount"
					Button = Dialog (SheetAmountDialog)

					If sTBLogMemo = "OK" Then 
						Dlgtext "TB_Amount", "金額欄位：已處理"
					Else
						Dlgtext "TB_Amount", "金額欄位：待處理"
					End If
				
				
				Case "BtnNext" 
					TBLogMemo = "NA"
					Run_S_Time = DateValue(Now) & " " & TimeValue(Now)
											
					'If TBDetails.TextBoxEntityName = "" Or TBDetails.TextBoxLastDate = "" Or  TBDetails.TextBoxStartDate = "" Or TBDetails.TextBoxEndDate = "" Then
						'sMsg = "請完成所有要求之【基本案件資訊】欄位"
						'Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一  未設定基本案件資訊 警示訊息")
					'If TBDetails.TextBoxStartDate > TBDetails.TextBoxEndDate Or TBDetails.TextBoxStartDate = TBDetails.TextBoxEndDate Then
						'sMsg = "財務報表期間 - 結束日期早於開始日期"
						'Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  基本資訊 財務報表期間設定錯誤 警示訊息")
					'If TBDetails.TextBoxStartDate > TBDetails.TextBoxLastDate Or TBDetails.TextBoxStartDate = TBDetails.TextBoxLastDate Then
						'sMsg = "期末財務報告流程開始日早於財務報表期間開始日期"
						'Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  基本資訊 基本案件資訊財務作業期間錯誤 資訊警示訊息")
					If sTBFilename = "" Then 
						sMsg = "請選擇試算表文件檔，並配對所有欄位"
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  未挑選試算表資料檔 警示訊息")  
					'ElseIf TBFieldArray_mix(TBDetails.DropListAccNumTB) = "Select..." Or TBFieldArray_mix(TBDetails.DropListAccDes) = "Select..." Or TBFieldArray_num(TBDetails.DropListBegBal) = "Select..." Or TBFieldArray_num(TBDetails.DropListEndBal) = "Select..." Then
					ElseIf TBFieldArray_mix(TBDetails.DropListAccNumTB) = "Select..." Or TBFieldArray_mix(TBDetails.DropListAccDes) = "Select..." Or  sTBLogMemo <> "OK" Then
						sMsg = "請配對所有欄位"
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  未完成試算表必要欄位配對 警示訊息")
					Else
						Dim DoubleCheck As Integer
						DoubleCheck = TB_DoubleCheck()
						If DoubleCheck = 0 Then GoTo CheckAgain
						
						sMsg = ""

						If status_SA = 1 Then 
							If Z_DataField_Check(sTBFilename, TB_DifAmount_name) <> 0 Then
								sMsg = "挑選進行處理之金額欄位【"& TB_DifAmount_name & "】中包含錯誤的資料數據，請檢查原始檔案"
							End If 
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_DifAmount_name Or TBFieldArray_Char(TBDetails.DropListAccDes) =  TB_DifAmount_name Then
								sMsg = "原始檔案中欄位【"& TB_DifAmount_name & "】重複選擇，請確認!"
							End If
						End If 
						
						If status_SA = 2 Then 
							If Z_DataField_Check(sTBFilename, TB_First_Amount_Field) <> 0 Then 
								sMsg = "挑選進行處理之金額欄位【"& TB_First_Amount_Field & "】中包含錯誤的資料數據，請檢查原始檔案"
							End If 
							If Z_DataField_Check(sTBFilename, TB_Last_Amount_Field) <> 0 Then 
								If sMsg = "" Then 
									sMsg = "挑選進行處理之金額欄位【"& TB_Last_Amount_Field & "】中包含錯誤的資料數據，請檢查原始檔案"
								Else
									sMsg = sMsg & Chr(10) & Chr(10) & "挑選進行處理之金額欄位【"& TB_Last_Amount_Field & "】中包含錯誤的資料數據，請檢查原始檔案"
								End If
							End If
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_First_Amount_Field Or TBFieldArray_Char(TBDetails.DropListAccDes) = TB_First_Amount_Field Then
								sMsg = "原始檔案中欄位【"& TB_Last_Amount_Field & "】重複選擇，請確認!"
							End If
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_Last_Amount_Field Or TBFieldArray_Char(TBDetails.DropListAccDes) = TB_Last_Amount_Field Then
								sMsg = "原始檔案中欄位【"& TB_First_Amount_Field & "】重複選擇，請確認!"
							End If
						End If 

						If status_SA = 3 Then 
							If Z_DataField_Check(sTBFilename, TB_C_Amount) <> 0 Then 
								sMsg = "挑選進行處理之金額欄位【"& TB_C_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
							End If 
							If Z_DataField_Check(sTBFilename, TB_D_Amount) <> 0 Then 
								If sMsg = "" Then 
									sMsg = "挑選進行處理之金額欄位【"& TB_D_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
								Else
									sMsg = sMsg & Chr(10) & Chr(10) & "挑選進行處理之金額欄位【"&  TB_D_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
								End If
							End If
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_C_Amount Or TBFieldArray_Char(TBDetails.DropListAccDes) = TB_C_Amount Then
								sMsg = "原始檔案中欄位【"& TB_D_Amount & "】重複選擇，請確認!"
							End If
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_D_Amount Or TBFieldArray_Char(TBDetails.DropListAccDes) = TB_D_Amount Then
								sMsg = "原始檔案中欄位 【"& TB_C_Amount & "】重複選擇，請確認!"
							End If

						End If 

						If status_SA = 4 Then 
							If Z_DataField_Check(sTBFilename, TB_First_D_Amount) <> 0 Then 
								sMsg = "挑選進行處理之金額欄位【"& TB_First_D_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
							End If 
							If Z_DataField_Check(sTBFilename, TB_Last_D_Amount) <> 0 Then 
								If sMsg = "" Then 
									sMsg = "挑選進行處理之金額欄位【"& TB_Last_D_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
								Else
									sMsg = sMsg & Chr(10) & Chr(10) & "挑選進行處理之金額欄位【"& TB_Last_D_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
								End If
							End If 
							If Z_DataField_Check(sTBFilename, TB_First_C_Amount) <> 0 Then 
								If sMsg = "" Then 
									sMsg = "挑選進行處理之金額欄位【"& TB_First_C_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
								Else
									sMsg = sMsg & Chr(10) & Chr(10) & "挑選進行處理之金額欄位【"& TB_First_C_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
								End If
							End If 
							If Z_DataField_Check(sTBFilename, TB_Last_C_Amount) <> 0 Then 
								If sMsg = "" Then 
									sMsg = "挑選進行處理之金額欄位【"& TB_Last_C_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
								Else
									sMsg = sMsg & Chr(10) & Chr(10) & "挑選進行處理之金額欄位【"& TB_Last_C_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
								End If
							End If
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_First_C_Amount Or TBFieldArray_Char(TBDetails.DropListAccDes) = TB_First_C_Amount Then
								sMsg = "原始檔案中欄位【"& TB_Last_C_Amount & "】重複選擇，請確認!"
							End If
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_First_D_Amount Or TBFieldArray_Char(TBDetails.DropListAccDes) = TB_First_D_Amount Then
								sMsg = "原始檔案中欄位【"& TB_Last_D_Amount & "】重複選擇，請確認!"
							End If 
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_First_C_Amount Or TBFieldArray_Char(TBDetails.DropListAccDes) = TB_First_C_Amount Then
								sMsg = "原始檔案中欄位【"& TB_First_C_Amount & "】重複選擇，請確認!"
							End If
							If TBFieldArray_mix(TBDetails.DropListAccNumTB) =  TB_First_D_Amount Or TBFieldArray_Char(TBDetails.DropListAccDes) = TB_First_D_Amount Then
								sMsg = "原始檔案中欄位【"& TB_First_D_Amount & "】重複選擇，請確認!"
							End If
						End If 

						If sMsg <> "" Then 
							Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一  試算表檔案含資料錯誤 警示訊息")  
							GoTo CheckAgain
						End If
						TBLogMemo="OK"
						bExitFunction = True
					End If
					
				
					CheckAgain:
				
				
				
				Case "BtnCancel"
					bExitFunction = True
									
					
		              End Select

	End Select
	If sTBFilename <> "" Then 
		DlgText "TextTBFile", iSplit(sTBFilename,"","\",1,1)
	Else 
		DlgText "TextTBFile", "請點選左方按鈕選擇試算表(Trial Balance)"
	End If
	
	If  bExitFunction Then
		TBDetail_Dlg = 0
	Else 
		TBDetail_Dlg = 1
	End If
End Function


Function GLDetail_Dlg(ControlID$, Action%, SuppValue%)
	Dim Button1 As Integer
	Dim bExitFunction As Boolean

	Select Case Action%
		Case 1 

			'DlgEnable "GL_Amount",  0
			'DlgEnable "BtnVaild",  0
			If sGLLogMemo = "OK" Then 
						Dlgtext "GL_Amount", "金額欄位：已處理"
					Else
						Dlgtext "GL_Amount", "金額欄位：待處理"
						DlgEnable "GL_Amount",  0
						DlgEnable "BtnVaild",  0
					End If

								
		Case 2 
                	
			Select Case ControlID$
				Case "BtnGLFile"
					
					Call GetGLFile
					If sGLFilename <> "" Then
						Call GLGetFieldName(sGLFilename)
						Call GLFile_Arry
						DlgEnable "GL_Amount",  1
						DlgEnable "BtnVaild",  1
						sGLLogMemo = "NA"
						GLLogMemo = "NA"
						sLog = "NA"
						Dlgtext "GL_Amount", "金額欄位：待處理"
						Dlgtext "BtnVaild", "無需考量過帳狀態"
					End If	
					
				Case "GLDetail_Help1"
					Button = Dialog (GLDetailHelp1TW)
					
				Case "GL_Amount"
				
					Button = Dialog (DlgA)

					If sGLLogMemo = "OK" Then 
						Dlgtext "GL_Amount", "金額欄位：已處理"
					Else
						Dlgtext "GL_Amount", "金額欄位：待處理"
					End If

				Case "BtnVaild"
				
					Button = Dialog (DlgB)

					If sLog = "OK" Then 
						Dlgtext "BtnVaild", "已設定需考量過帳狀態"
					Else
						Dlgtext "BtnVaild", "無需考量過帳狀態"
					End If
		
				
				Case"DropListDocNum"
					Dim XXX As String
					Let XXX =GLFieldArray_mix(SuppValue%)
					Client.OpenDatabase sGLFilename			
					Client.FindAndHighlightFieldInDatabase sGLFilename, 5,  XXX					
					XXX=""
														
				Case "BtnRun"
					GLLogMemo = "NA"
					If Run_S_Time = "" Then Run_S_Time = DateValue(Now) & " " & TimeValue(Now)

					If sGLFilename = "" Then
						sMsg = "請選擇總帳明細，並配對所有必要欄位"
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  尚未挑選總帳明細檔 警示訊息")
					ElseIf GLFieldArray_mix(GLDetails.DropListDocNum) = "Select..." Or GLFieldArray_mix(GLDetails.DropListDes) = "Select..." Or _ 
								GLFieldArray_date(GLDetails.DropListPosDate)   = "Select..." Or GLFieldArray_mix(GLDetails.DropListAccNumGL) = "Select..." Or _ 
								GLFieldArray_num(GLDetails.DropListLineID) = "Select..." Or GLFieldArray_mix(GLDetails.DropListAccName)  = "Select..." Or sGLLogMemo <> "OK"  Then
						sMsg = "請配對所有必要欄位"
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  尚有總帳明細檔必要欄位未進行配對 警示訊息")
					ElseIf GLFieldArray_date(GLDetails.DropListDocDate) <> "Select..." And GLDetails.CK_Approval_Date  = 1 Then
						sMsg = "【傳票核准日】下拉選單 與 勾選項目【傳票核准日同總帳日】僅可擇一"
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  傳票核准日欄位設定 警示訊息")					
					Else
							Dim DoubleCheck As Integer
							DoubleCheck = GL_DoubleCheck()
							If DoubleCheck = 0 Then GoTo CheckAgain
														
							sMsg = ""
							
							If status_Amount = 1 Then  
								If GLFieldArray_mix(GLDetails.DropListDocNum) = JE_Amount_name Or GLFieldArray_mix(GLDetails.DropListDes) = JE_Amount_name Or _ 
										GLFieldArray_mix(GLDetails.DropListAccNumGL) = JE_Amount_name Or GLFieldArray_num(GLDetails.DropListLineID) = JE_Amount_name Or _
										GLFieldArray_mix(GLDetails.DropListAccName)  = JE_Amount_name Or _
										GLFieldArray_num(GLDetails.DropListManual) = JE_Amount_name Or GLFieldArray_mix(GLDetails.DropListJESource)  = JE_Amount_name Or _
										GLFieldArray_mix(GLDetails.DropListCreateBy)  = JE_Amount_name Or GLFieldArray_mix(GLDetails.DropListApprovBy)  = JE_Amount_name Then
									sMsg = "原始檔案中欄位【"& JE_Amount_name & "】重複選擇，請確認!"
									'GoTo Repeat
								End If
							End If

							If status_Amount = 2 Then
								If GLFieldArray_mix(GLDetails.DropListDocNum) = JE_D_Amount Or GLFieldArray_mix(GLDetails.DropListDes) = JE_D_Amount Or _ 
										GLFieldArray_mix(GLDetails.DropListAccNumGL) = JE_D_Amount Or GLFieldArray_num(GLDetails.DropListLineID) = JE_D_Amount Or _
										GLFieldArray_mix(GLDetails.DropListAccName)  = JE_D_Amount Or _
										GLFieldArray_num(GLDetails.DropListManual) = JE_D_Amount Or GLFieldArray_mix(GLDetails.DropListJESource)  = JE_D_Amount Or _
										GLFieldArray_mix(GLDetails.DropListCreateBy)  = JE_D_Amount Or GLFieldArray_mix(GLDetails.DropListApprovBy)  = JE_D_Amount Then
									sMsg = "原始檔案中欄位【"& JE_D_Amount & "】重複選擇，請確認!"
									'GoTo Repeat
								End If
								If GLFieldArray_mix(GLDetails.DropListDocNum) = JE_C_Amount Or GLFieldArray_mix(GLDetails.DropListDes) = JE_C_Amount Or _ 
										GLFieldArray_mix(GLDetails.DropListAccNumGL) = JE_C_Amount Or GLFieldArray_num(GLDetails.DropListLineID) = JE_C_Amount Or _
										GLFieldArray_mix(GLDetails.DropListAccName)  = JE_C_Amount Or _
										GLFieldArray_num(GLDetails.DropListManual) = JE_C_Amount Or GLFieldArray_mix(GLDetails.DropListJESource)  = JE_C_Amount Or _
										GLFieldArray_mix(GLDetails.DropListCreateBy)  = JE_C_Amount Or GLFieldArray_mix(GLDetails.DropListApprovBy)  = JE_C_Amount Then
									sMsg = "原始檔案中欄位【"& JE_C_Amount & "】重複選擇，請確認!"
									'GoTo Repeat
								End If
  							End If													
							
							If status_Amount = 3 Then
								If GLFieldArray_mix(GLDetails.DropListDocNum) = JE_DC_name Or GLFieldArray_mix(GLDetails.DropListDes) = JE_DC_name Or _ 
										GLFieldArray_mix(GLDetails.DropListAccNumGL) = JE_DC_name Or GLFieldArray_num(GLDetails.DropListLineID) = JE_DC_name Or _
										GLFieldArray_mix(GLDetails.DropListAccName)  = JE_DC_name Or _
										GLFieldArray_num(GLDetails.DropListManual) = JE_DC_name Or GLFieldArray_mix(GLDetails.DropListJESource)  = JE_DC_name Or _
										GLFieldArray_mix(GLDetails.DropListCreateBy)  = JE_DC_name Or GLFieldArray_mix(GLDetails.DropListApprovBy)  = JE_DC_name Then
									sMsg = "原始檔案中欄位【"& JE_DC_name & "】重複選擇，請確認!"
									'GoTo Repeat
								End If
								If GLFieldArray_mix(GLDetails.DropListDocNum) = JE_Amount_name Or GLFieldArray_mix(GLDetails.DropListDes) = JE_Amount_name Or _ 
										GLFieldArray_mix(GLDetails.DropListAccNumGL) = JE_Amount_name Or GLFieldArray_num(GLDetails.DropListLineID) = JE_Amount_name Or _
										GLFieldArray_mix(GLDetails.DropListAccName)  = JE_Amount_name Or _
										GLFieldArray_num(GLDetails.DropListManual) = JE_Amount_name Or GLFieldArray_mix(GLDetails.DropListJESource)  = JE_Am9ount_name Or _
										GLFieldArray_mix(GLDetails.DropListCreateBy)  = JE_Amount_name Or GLFieldArray_mix(GLDetails.DropListApprovBy)  = JE_Amount_name Then
									sMsg = "原始檔案中欄位【"& JE_DC_name & "】重複選擇，請確認!"
									'GoTo Repeat
								End If
							End If

							


							If Z_DataField_Check(sGLFilename, GLFieldArray_date(GLDetails.DropListPosDate)) <> 0 Then
								sMsg = "【總帳日期】對應之原始檔案欄位 " & GLFieldArray_date(GLDetails.DropListPosDate) & " 中包含錯誤的資料數據，請檢查原始檔案"
							End If 
							
							If Z_DataField_Check(sGLFilename, GLFieldArray_Num(GLDetails.DropListLineID)) <> 0 Then
								If sMsg = "" Then
									sMsg = "【傳票文件項次】對應之原始檔案欄位 " & GLFieldArray_Num(GLDetails.DropListLineID) & " 中包含錯誤的資料數據，請檢查原始檔案"
								Else
									sMsg = sMsg & Chr(10) & Chr(10) & "【傳票文件項次】對應之原始檔案欄位 " & GLFieldArray_Num(GLDetails.DropListLineID) & " 中包含錯誤的資料數據，請檢查原始檔案"
								End If
							End If
							
							If GLFieldArray_Mix(GLDetails.DropListUserDefind1) <> "Select..." Then						
								If Z_DataField_Check(sGLFilename, GLFieldArray_Mix(GLDetails.DropListUserDefind1)) <> 0 Then
									If sMsg = "" Then
										sMsg = "第一個自訂欄位對應之原始檔案欄位 " & GLFieldArray_Mix(GLDetails.DropListUserDefind1) & " 中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "第一個自訂欄位對應之原始檔案欄位 " & GLFieldArray_Mix(GLDetails.DropListUserDefind1) & " 中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
							End If

							If GLFieldArray_Mix(GLDetails.DropListUserDefind2) <> "Select..." Then						
								If Z_DataField_Check(sGLFilename, GLFieldArray_Mix(GLDetails.DropListUserDefind2)) <> 0 Then
									If sMsg = "" Then
										sMsg = "第二個自訂欄位對應之原始檔案欄位 " & GLFieldArray_Mix(GLDetails.DropListUserDefind2) & " 中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "第二個自訂欄位對應之原始檔案欄位 " & GLFieldArray_Mix(GLDetails.DropListUserDefind2) & " 中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
							End If
							
							If GLFieldArray_Mix(GLDetails.DropListUserDefind3) <> "Select..." Then						
								If Z_DataField_Check(sGLFilename, GLFieldArray_Mix(GLDetails.DropListUserDefind3)) <> 0 Then
									If sMsg = "" Then
										sMsg = "第三個自訂欄位對應之原始檔案欄位 " & GLFieldArray_Mix(GLDetails.DropListUserDefind3) & " 中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "第三個自訂欄位對應之原始檔案欄位 " & GLFieldArray_Mix(GLDetails.DropListUserDefind3) & " 中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
							End If

							If GLFieldArray_date(GLDetails.DropListUserDefind4) <> "Select..." Then						
								If Z_DataField_Check(sGLFilename, GLFieldArray_Date(GLDetails.DropListUserDefind4)) <> 0 Then
									If sMsg = "" Then
										sMsg = "第四個自訂欄位對應之原始檔案欄位 " & GLFieldArray_date(GLDetails.DropListUserDefind4) & " 中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "第四個自訂欄位對應之原始檔案欄位 " & GLFieldArray_date(GLDetails.DropListUserDefind4) & " 中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
							End If
																												
							If GLFieldArray_date(GLDetails.DropListDocDate) <> "Select..."  Then
								If Z_DataField_Check(sGLFilename, GLFieldArray_date(GLDetails.DropListDocDate)) <> 0 Then
									If sMsg = "" Then
										sMsg = "【傳票核准日】對應之原始檔案欄位 " & GLFieldArray_date(GLDetails.DropListDocDate) & " 中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "【傳票核准日】對應之原始檔案欄位 " & GLFieldArray_date(GLDetails.DropListDocDate) & " 中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
							End If

							If status_Amount = 1 Then  
								If Z_DataField_Check(sGLFilename, JE_Amount_name) <> 0 Then
									If sMsg = "" Then
										sMsg = "原始檔案欄位【" & JE_Amount_name & "】中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "原始檔案欄位【" & JE_Amount_name & "】中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
							End If

							If status_Amount = 2 Then  
								If Z_DataField_Check(sGLFilename, JE_D_Amount) <> 0 Then
									If sMsg = "" Then
										sMsg = "原始檔案欄位【" & JE_D_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "原始檔案欄位【" & JE_D_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
								If Z_DataField_Check(sGLFilename, JE_C_Amount) <> 0 Then
									If sMsg = "" Then
										sMsg = "原始檔案欄位【" & JE_C_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "原始檔案欄位【" & JE_C_Amount & "】中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
							End If													
							
							If status_Amount = 3 Then  
								If Z_DataField_Check(sGLFilename, JE_DC_name) <> 0 Then
									If sMsg = "" Then
										sMsg = "原始檔案欄位【" & JE_DC_name & "】中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "原始檔案欄位【" & JE_DC_name & "】中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
								If Z_DataField_Check(sGLFilename, JE_Amount_name) <> 0 Then
									If sMsg = "" Then
										sMsg = "原始檔案欄位【" & JE_Amount_name & "】中包含錯誤的資料數據，請檢查原始檔案"
									Else
										sMsg = sMsg & Chr(10) & Chr(10) & "原始檔案欄位【" & JE_Amount_name & "】中包含錯誤的資料數據，請檢查原始檔案"
									End If
								End If
							End If

							

							
							If sMsg <> "" Then 
								Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一  總帳明細檔中含錯誤資料 警示訊息")
								GoTo CheckAgain
							End If 							
										
							'IS Manual 欄位檢查，如數值非0或1，出現錯誤訊息後跳離程式
							If  GLFieldArray_Num(GLDetails.DropListManual) <> "Select..." Then 
								If Step1_Check_User_Defind_Manual( iSplit(sGLFilename,"","\",1,1), GLFieldArray_Num(GLDetails.DropListManual))  Then
									Call Z_Delete_File("#GL_Temp_Maunal_Check.IDM")
								Else
									sMsg = "【人工或自動分錄】欄位數值應為0或1 (0為自動傳票、1為人工傳票)，請確認是否配對到正確欄位或是請重新檢查原始檔案"
									Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一  人工或自動分錄欄位資料錯誤 警示訊息")
									Call Z_Delete_File("#GL_Temp_Maunal_Check.IDM")
									GoTo CheckAgain
								End If 
							End If
							
						GLLogMemo = "OK" 
							
						bExitFunction = True

					End If
					'Repeat:
					'	Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一  總帳明細檔中含錯誤資料 警示訊息")
					'	GoTo CheckAgain
						
					CheckAgain:
						DlgEnable "BtnRun", 1
						DlgEnable "BtnCancel", 1


					
				Case "BtnCancel"
					bExitFunction = True
			End Select
	End Select

	If sGLFilename <> "" Then 
		DlgText "TextGLFile", iSplit(sGLFilename,"","\",1,1)
	Else 
		DlgText "TextGLFile", "請點選左方按鈕選擇總帳傳票分錄檔(General Ledger)"
	End If
	
	If  bExitFunction Then
		GLDetail_Dlg = 0
	Else 
		GLDetail_Dlg = 1
	End If
End Function

Function Routine_Dlg(ControlID$, Action%, SuppValue%)
	Dim Button As Integer
	Dim bExitFunction As Boolean
		
	Select Case Action%
       		Case 1 
       			                            	
	        Case 2 
              		'Routine_Dlg = 1
                Select Case ControlID$
                	
                	Case "Routines_Help1"
              			Button = Dialog (RoutinesHelp1TW)
				
			'Case "BtnOK" 
			'	If GetTotal("#GL#DESC.IDM" ,"" ,"DBCount" ) = 0 Then
			'	sMsg = "匯入IDEA的檔案資料區間與案件設定的財務報導期間不一致，請重新確認!!!"
			'		Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION ,"步驟三 測試資料錯誤 警示訊息")
			'	Else
			'		Run_S_Time = DateValue(Now) & " " & TimeValue(Now)
			'		Call Step3_Routines
			'		bExitFunction = true
			'	End If
	       			        	       
              		'Case "BtnCancel"
              			
              		's1Check = 0
                             'bExitFunction = true
			
		End Select
                            
	End Select 

        If  bExitFunction Then
		 Routine_Dlg= 0
	Else 
		 Routine_Dlg= 1
	End If        
        
End Function


Function ReRun_Dlg(ControlID$, Action%, SuppValue%)
	Dim Button As Integer	
	Dim bExitFunction As Boolean
	
	Select Case Action%
		Case 1 
		
			DlgEnable "BtnDataMap" ,  sSTEP_2 + sSTEP_3 + sSTEP_4 + sSTEP_5 
			DlgEnable "BtnLoadfile",  sSTEP_3 + sSTEP_4 + sSTEP_5 
			DlgEnable "BtnRoutine",  sSTEP_4 +  sSTEP_5 
			DlgEnable "BtnCriteria",  sSTEP_5
		                            
       		Case 2 
			ReRun_Dlg = 1
                	Select Case ControlID$
                	
                			Case "BtnDataMap"
                				
            					sMsg = "你是否確認要重新由【進行資料欄位的配對】步驟重新執行 ? "
						Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "重新執行!!!")
						If Result = IDYES Then
		                				'Call X_Delete_Project_Info
		                				Call X_Update_Step_Info("STEP_1",1)
							Call X_Update_Step_Info("STEP_2",0)
							Call X_Update_Step_Info("STEP_3",0)
							Call X_Update_Step_Info("STEP_4",0)
							Call X_Update_Step_Info("STEP_5",0)
							Call X_Update_Step_Info("STEP_6",0)
		                				Call Z_Delete_File("#Account_Selected.IDM")
		                				'Call Z_Delete_File("#Holiday#-Holiday.IDM")
			                			Call Z_Delete_File("#INF Report#Sort.IDM") 
		                				Call Z_Delete_File("#INF Report#.IDM") 
		                				'Call Z_Delete_File("#Weekend.IDM") 
		                				Call Z_Delete_File("#GL_Temp_Maunal_Check.IDM")
		                				Call Z_Delete_File("#GL#In_Period.IDM")
		                				Call Z_Delete_File("#List_of_accounts_with_variance.IDM")
		                				Call Z_Delete_File("#Completeness_calculate.IDM")
		                				Call Z_Delete_File("#Completeness_Check.IDM")
		                				Call Z_Delete_File("#GL_Account_Sum.IDM")
		                				Call Z_Delete_File("#NotinPeriod-ApprovalDate.IDM")
		                				Call Z_Delete_File("#Null-GL_Account.IDM")
		                				Call Z_Delete_File("#Null-GL_Description_Criteria.IDM")
		                				Call Z_Delete_File("#Null-GL_Number.IDM")
		                				Call Z_Delete_File("#GL#.IDM")
		                				Call Z_Delete_File("#TB#.IDM")
		                				Call Z_Delete_File("#GL#In_Period_Doc_Sum.IDM")
		                				Call Z_Delete_File("#GL#In_Period_Doc_Sum_Diff.IDM")
		                				Call Z_Delete_File("#GL_#Doc_not_Balance.IDM")
		                				Call Z_Delete_File("#GL_#Doc_not_Balance_Sum.IDM")
		                				Call Z_Delete_File("#GL#DESC.IDM")
		                				Call Z_Delete_File("#GL#Sum_By_Doc.IDM")
		                				sGL_Finsh = 0
							Call X_Update_Step_Info("GL_Finsh", sGL_Finsh)
							sTB_Finsh = 0
							Call X_Update_Step_Info("TB_Finsh", sTB_Finsh)
		                				GoTo ReSet02 
						End If
                				
					Case "BtnLoadfile"

              					sMsg = "你是否確認要重新由【上傳特定科目配對的檔案】步驟重新執行 ? "
       						Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "重新執行!!!")
						If Result = IDYES Then
							Call X_Update_Step_Info("STEP_2",1)
							Call X_Update_Step_Info("STEP_3",0)
							Call X_Update_Step_Info("STEP_4",0)
	                				
							ReSet02 :
							Call Z_Delete_File("#AccountMapping#-AccountMapping.IDM") 
							Call Z_Delete_File("#AccountMapping.IDM") 
							Call Z_Delete_File("#AccountMapping_sum.IDM")
							Call Z_Delete_File("#AccountMapping_R.IDM")
							Call Z_Delete_File("#AccountMapping_C.IDM")
							GoTo ReSet03 					
						End If
						
					Case "BtnRoutine"
              					sMsg = "你是否確認要重新由【JE預先篩選程序】步驟重新執行 ? "
       						Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "重新執行!!!")
						If Result = IDYES Then
							Call X_Update_Step_Info("STEP_3",1)
							Call X_Update_Step_Info("STEP_4",0)
						
							ReSet03 :
							Call Z_Delete_File("#GL#Critial.IDM") 
							Call Z_Delete_File("#PreScr-R1.IDM") 
							Call Z_Delete_File("#PreScr-R2.IDM")
							Call Z_Delete_File("#PreScr-R3.IDM") 
							Call Z_Delete_File("#PreScr-R4.IDM")
							Call Z_Delete_File("#PreScr-R5.IDM") 
							Call Z_Delete_File("#PreScr-R6.IDM")
							Call Z_Delete_File("#PreScr-A2.IDM") 
							Call Z_Delete_File("#PreScr-A4.IDM")
							Call Z_Delete_File("#PreScr-R1-All.IDM") 
							Call Z_Delete_File("#PreScr-R2-All.IDM")
							Call Z_Delete_File("#PreScr-R3-All.IDM") 
							Call Z_Delete_File("#PreScr-R4-All.IDM")
							Call Z_Delete_File("#PreScr-R5-Sum.IDM") 
							Call Z_Delete_File("#PreScr-R6-Sum.IDM")
							Call Z_Delete_File("#PreScr-A2-All.IDM") 
							Call Z_Delete_File("#PreScr-A3-All.IDM") 
							Call Z_Delete_File("#PreScr-A4-All.IDM")
							Call Z_Delete_File("#GL#Account_Mapping.IDM")
							
							If Z_File_Exist("#GL#.IDM") = True Then 
								If FindField("#GL#.IDM","DEBIT_傳票金額_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","DEBIT_傳票金額_JE_T")
								If FindField("#GL#.IDM","CREDIT_傳票金額_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","CREDIT_傳票金額_JE_T")
								If FindField("#GL#.IDM","DEBIT_CREDIT_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","DEBIT_CREDIT_JE_T")
								If FindField("#GL#.IDM","IS_HOLIDAY") <> 0 Then Call X_RemoveField("#GL#.IDM","IS_HOLIDAY")
								If FindField("#GL#.IDM","HOLIDAY_NAME") <> 0 Then Call X_RemoveField("#GL#.IDM","HOLIDAY_NAME")
								If FindField("#GL#.IDM","WORKDAY") <> 0 Then Call X_RemoveField("#GL#.IDM","WORKDAY")
	
								If FindField("#GL#.IDM","DOC_HOLIDAY_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","DOC_HOLIDAY_JE_T")
								If FindField("#GL#.IDM","DOC_HOLIDAY_NAME_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","DOC_HOLIDAY_NAME_JE_T")
								If FindField("#GL#.IDM","POST_HOLIDAY_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","POST_HOLIDAY_JE_T")
								If FindField("#GL#.IDM","POST_HOLIDAY_NAME_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","POST_HOLIDAY_NAME_JE_T")
								If FindField("#GL#.IDM","IS_HOLIDAY") <> 0 Then Call X_RemoveField("#GL#.IDM","IS_HOLIDAY")
								If FindField("#GL#.IDM","DOC_WEEKEND_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","DOC_WEEKEND_JE_T")
								If FindField("#GL#.IDM","POST_WEEKEND_JE_T") <> 0 Then Call X_RemoveField("#GL#.IDM","POST_WEEKEND_JE_T")
	
								If FindField("#GL#.IDM","PRESCR_R1") <> 0 Then Call X_RemoveField("#GL#.IDM","PRESCR_R1")
								If FindField("#GL#.IDM","PRESCR_R2") <> 0 Then Call X_RemoveField("#GL#.IDM","PRESCR_R2")
								If FindField("#GL#.IDM","PRESCR_R3") <> 0 Then Call X_RemoveField("#GL#.IDM","PRESCR_R3")
								If FindField("#GL#.IDM","PRESCR_R4") <> 0 Then Call X_RemoveField("#GL#.IDM","PRESCR_R4")
								If FindField("#GL#.IDM","PRESCR_A2") <> 0 Then Call X_RemoveField("#GL#.IDM","PRESCR_A2")
								If FindField("#GL#.IDM","PRESCR_A3") <> 0 Then Call X_RemoveField("#GL#.IDM","PRESCR_A3")
								If FindField("#GL#.IDM","PRESCR_A4") <> 0 Then Call X_RemoveField("#GL#.IDM","PRESCR_A4")
	
								If FindField("#GL#.IDM","R1_TAG") <> 0 Then Call X_RemoveField("#GL#.IDM","R1_TAG")
								If FindField("#GL#.IDM","R2_TAG") <> 0 Then Call X_RemoveField("#GL#.IDM","R2_TAG")
								If FindField("#GL#.IDM","R3_TAG") <> 0 Then Call X_RemoveField("#GL#.IDM","R3_TAG")
								If FindField("#GL#.IDM","R4_TAG") <> 0 Then Call X_RemoveField("#GL#.IDM","R4_TAG")
								If FindField("#GL#.IDM","A2_TAG") <> 0 Then Call X_RemoveField("#GL#.IDM","A2_TAG")
								If FindField("#GL#.IDM","A3_TAG") <> 0 Then Call X_RemoveField("#GL#.IDM","A3_TAG")
								If FindField("#GL#.IDM","A4_TAG") <> 0 Then Call X_RemoveField("#GL#.IDM","A4_TAG")
							End If 
							
							GoTo ReSet04
						End If 
					
					Case "BtnCriteria"
              					sMsg = "你是否確認要重新由【設定JE篩選條件】步驟重新執行 ? "  & Chr(10) & Chr(10) & _
              							"備註:重新執行將會清除先前已保存之篩選結果，如之前的篩選條件有需要保存請記得先將篩選結果匯出後再進行重新執行作業" 
       						Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "重新執行!!!")
						If Result = IDYES Then
							Call X_Update_Step_Info("STEP_4",1)
							
							ReSet04 :
							Call X_Update_Step_Info("STEP_5",0)
							Call X_Step4_Drop_Log_Table
							Call Z_Delete_File("#CriteriaSelect1.IDM") 
							Call Z_Delete_File("#CriteriaSelect2.IDM") 
							Call Z_Delete_File("#CriteriaSelect3.IDM")
							Call Z_Delete_File("#CriteriaSelect4.IDM") 
							Call Z_Delete_File("#CriteriaSelect5.IDM") 
							Call Z_Delete_File("#CriteriaSelect6.IDM")
							Call Z_Delete_File("#CriteriaSelect7.IDM") 
							Call Z_Delete_File("#CriteriaSelect8.IDM")
							Call Z_Delete_File("#CriteriaSelect9.IDM") 
							Call Z_Delete_File("#CriteriaSelect10.IDM")
							Call Z_Delete_File("#Null-GL_Description_Criteria.IDM")
							Call Z_Delete_File("#To_WP.IDM")
							Call Z_Delete_File("#To_WP_Sum.IDM")
						End If

						bExitFunction = true
              			
              				Case "BtnCancel"
                                		bExitFunction = true
			End Select
                            
	End Select 
  
        If  bExitFunction Then
		 ReRun_Dlg = 0
	Else 
		 ReRun_Dlg = 1
	End If    
        
End Function

Function Z_Delete_File(sIDMFile As String)

	Set fs = CreateObject("Scripting.FileSystemObject")
	
	If  fs.FileExists(client.WorkingDirectory & sIDMFile ) Then 
		Client.CloseDatabase sIDMFile
		Client.DeleteDatabase sIDMFile
	End If
	
	fs = Nothing

End Function

Function Criteria_Dlg(ControlID$, Action%, SuppValue%)
	Dim bExitFunction As Boolean
	Dim Button As Integer
	Dim Aux1(10)
	
	ReDim TestArray(10)
	
	Select Case Action%
      	  	Case 1 
       			S1Check = "0"
       			Call X_Get_Criteria_Info
       			Call Step4_Reset_CheckBox
			DlgEnable "TextBoxFromDate1" , 0
			DlgEnable "TextBoxToDate1" , 0
			DlgEnable "TextBoxFromDate2" , 0
			DlgEnable "TextBoxToDate2" , 0
			DlgEnable "BtnExpSumReport",0
			DlgText "TextBoxFromDate1", ""
			DlgText "TextBoxToDate1", ""
			DlgText "TextBoxFromDate2", ""
			DlgText "TextBoxToDate2",""
			'DlgText "TextBoxFromNum1" , ""
			'DlgText "TextBoxToNum1" , ""
			DlgValue "ChkBoxRout1" , 0
			DlgValue "ChkBoxRout2" , 0
			DlgValue "ChkBoxRout3" , 0
			DlgValue "ChkBoxRout4" , 0
			'DlgValue "ChkBoxModfRout2" , 0
			DlgValue "ChkBoxModfRout4" , 0
			DlgValue "ChkBoxWeeekend_DocDate" , 0
			DlgValue "ChkBoxWeeekend_PostDate" , 0
			DlgValue "ChkBoxHoliday_DocDate" , 0
			DlgValue "ChkBoxHoliday_PostDate" , 0
			DlgValue "ChkBoxIsMaunl" , 0
			DlgValue "ChkBoxSelDate1" , 0
			DlgValue "ChkBoxSelDate2" , 0
			'DlgValue "ChkBoxModfRout3" , 0
			DlgValue "ChkBoxSelNum1" , 0
			DlgValue "ChkBoxSelChar1" , 0
			DlgValue "ChkBoxSelChar2" , 0
			DlgValue "ChkBoxSelChar3" , 0
			DlgValue "ChkBoxSelChar4" , 0
			DlgValue "ChkBoxSelChar5" , 0
			DlgValue "ChkBoxSelChar6" , 0
			DlgEnable "BtnRun" , 0
			DlgValue "ChkBoxNumAcc" , 0
	
			Call Criteria_Dialog_Control
			DlgEnable "ChkBoxIsManual" ,  FindField("#GL#DESC.IDM" , "人工傳票否_JE_S" )
			Call CriteriaDlg_Arry
				
			'Call X_Get_Routines_Memo
			
			sStep4_Rec_info =  "0" 
			If Z_File_Exist("#PreScr-R1-All.IDM") = True Then sStep4_Rec_info = GetTotal("#PreScr-R1-All.IDM" ,"" ,"DBCount" )		
			DlgText "ChkBoxRout1","於期末財務報表日後核准之分錄"  & "【"  & sStep4_Rec_info & " 筆明細】"

			sStep4_Rec_info =  "0" 
			If Z_File_Exist("#PreScr-R2-All.IDM") = True Then sStep4_Rec_info = GetTotal("#PreScr-R2-All.IDM" ,"" ,"DBCount" )		
			DlgText "ChkBoxRout2","分錄摘要出現特定描述"   & "【"  & sStep4_Rec_info & " 筆明細】"

			sStep4_Rec_info =  "0" 
			If Z_File_Exist("#PreScr-R3-All.IDM") = True Then sStep4_Rec_info = GetTotal("#PreScr-R3-All.IDM" ,"" ,"DBCount" )		
			DlgText "ChkBoxRout3","未預期出現之特定借貸組合"  & "【"  & sStep4_Rec_info & " 筆明細】"
			
			If GetTotal("#AccountMapping.IDM" ,"" ,"DBCount" ) = 0 Then 
				DlgText "ChkBoxRout3","未預期出現之特定借貸組合 - 未設定科目配對檔"
				DlgEnable "ChkBoxRout3" , 0   
			Else
				If GetTotal("#AccountMapping_R.IDM" ,"" ,"DBCount" ) * GetTotal("#AccountMapping_C.IDM" ,"" ,"DBCount" ) = 0 Then
					DlgText "ChkBoxRout3","預期出現之特定借貸組合 - 無符合篩選必要之科目設定"
					DlgEnable "ChkBoxRout3" , 0   				
				End If				
			End If
			
			sStep4_Rec_info =  "0" 
			If Z_File_Exist("#PreScr-R4-All.IDM") = True Then sStep4_Rec_info = GetTotal("#PreScr-R4-All.IDM" ,"" ,"DBCount" )		
			DlgText "ChkBoxRout4","分錄金額中有連續0的尾數"  & "【"  & sStep4_Rec_info & " 筆明細】"
			
			sStep4_Rec_info =  "0" 
			If Z_File_Exist("#Null-GL_Description_Criteria.IDM") = True Then sStep4_Rec_info = GetTotal("#Null-GL_Description_Criteria.IDM" ,"" ,"DBCount" )		
			DlgText "ChkBoxIsDescNull","分錄無摘要描述(即空白摘要)"  & "【"  & sStep4_Rec_info & " 筆明細】"
												
			'sStep4_Rec_info =  "0" 
			'If sA2_Memo = "N/A" Then 
			'	DlgText "ChkBoxModfRout2", "# 未自訂額外搜尋之摘要特定描述" 
			'Else
			'	If Z_File_Exist("#PreScr-A2-All.IDM") = True Then sStep4_Rec_info = GetTotal("#PreScr-A2-All.IDM" ,"" ,"DBCount" ) 
			'	DlgText "ChkBoxModfRout2", sA2_Memo  & "【"  & sStep4_Rec_info & " 筆明細】"
			'End If

			'sStep4_Rec_info =  "0" 
			'If sA3_Memo = "N/A" Then 
			'	DlgText "ChkBoxModfRout3", "# 未自訂額外的科目借貸組合"  
			'Else
			'	If Z_File_Exist("#PreScr-A3-All.IDM") = True Then sStep4_Rec_info = GetTotal("#PreScr-A3-All.IDM" ,"" ,"DBCount" ) 
			'	DlgText "ChkBoxModfRout3", sA3_Memo  & "【"  & sStep4_Rec_info & " 筆明細】"
			'End If

			'sStep4_Rec_info =  "0" 
			'If sA4_Memo = "N/A" Then 
			'	DlgText "ChkBoxModfRout4", "# 未自訂額外的特定尾數" 
			'Else
			'	If Z_File_Exist("#PreScr-A4-All.IDM") = True Then sStep4_Rec_info = GetTotal("#PreScr-A4-All.IDM" ,"" ,"DBCount" ) 
			'	DlgText "ChkBoxModfRout4", sA4_Memo  & "【"  & sStep4_Rec_info & " 筆明細】"
			'End If
				
			If FindField("#GL#DESC.IDM","傳票核准日_JE") = 0 Then
				DlgText "ChkBoxRout1","未有設定傳票核准日欄位，此程序未執行" 
				DlgEnable "ChkBoxRout1", 0
				
			End If 							
										
			If sSEQ_Num < 11 Then DlgEnable "BtnRun" , 1   ' 超過十次 控制按鈕為enable
			If sSEQ_Num > 1 Then DlgEnable "BtnExpSumReport" , 1  ' 沒下篩選條件 控制按鈕為enable
			sSelDate1 = ""
			sSelDate2 = ""
			Erase sListArry1
			Erase sListArry2
			
        		Case 2 
                
			Select Case ControlID$
				Case "CriterialSelection_Help1"
					Button = Dialog (TBDetailsHelp2TW)
					
				Case "CriterialSelection_Help_Date"
					Button = Dialog (CriterialSelectionHelp1TW)
					
				Case "BtnFromDate1"
					Call Step4_Button_Disable
					sDate = Criteria.TextBoxFromDate1
					Button = Dialog (DatePicker)
					dlgText "TextBoxFromDate1", sDate
					Call Step4_Button_Enable
				
				Case "BtnToDate1"
					Call Step4_Button_Disable
					sDate = Criteria.TextBoxToDate1
					Button = Dialog (DatePicker)
					dlgText "TextBoxToDate1", sDate
					Call Step4_Button_Enable
				
				Case "BtnFromDate2"
					Call Step4_Button_Disable
					sDate = Criteria.TextBoxFromDate2
					Button = Dialog (DatePicker)
					dlgText "TextBoxFromDate2", sDate
					Call Step4_Button_Enable
				
				Case "BtnToDate2"
					Call Step4_Button_Disable
					sDate = Criteria.TextBoxToDate2
					Button = Dialog (DatePicker)
					dlgText "TextBoxToDate2", sDate
					Call Step4_Button_Enable
				
				Case "BtnSelSpeDate1"
					Call Step4_Button_Disable
						
					Call AddDateArray("Date1")  
					SelDate_check = 1 
					Button = Dialog (SpeDateSelect)
						
					If sSelDate1 <> "" Then
						dlgText "BtnSelSpeDate1", "已設定"
						DlgText "TextBoxFromDate1" ,""
						DlgText "TextBoxToDate1" ,""
					Else 
						dlgText "BtnSelSpeDate1", "尚未設定"
					End If
					
					Call Step4_Button_Enable
											
				Case "BtnSelSpeDate2"
				
					Call Step4_Button_Disable
					
					Call AddDateArray("Date2")  
					SelDate_check = 2 
					Button = Dialog (SpeDateSelect)
					
					If sSelDate2 <> "" Then 
						dlgText "BtnSelSpeDate2", "已設定"
						DlgText "TextBoxFromDate2" ,""
						DlgText "TextBoxToDate2" ,""
					Else 
						dlgText "BtnSelSpeDate2", "尚未設定"
					End If
						
					Call Step4_Button_Enable

				Case "BtnSelSpeChar1"
				
					Call Step4_Button_Disable
					
					SpeCharSelect.TextBoxChar = sSelChar1
					Button = Dialog (SpeCharSelect)
					Call X_Char_Select_Function("Char1")
					sSelType1 = SpeCharSelect.OptButtonGroupTxt1
					If sSelChar1 <> "" Then
						dlgText "BtnSelSpeChar1", "已設定"
					Else 
						dlgText "BtnSelSpeChar1", "尚未設定"
					End If
					SpeCharSelect.TextBoxChar = ""
					
					Call Step4_Button_Enable
					
				Case "BtnSelSpeChar2"
					
					Call Step4_Button_Disable
					
					SpeCharSelect.TextBoxChar = sSelChar2
					Button = Dialog (SpeCharSelect)
					Call X_Char_Select_Function("Char2")
					sSelType2 = SpeCharSelect.OptButtonGroupTxt1
					If sSelChar2 <> "" Then 
						dlgText "BtnSelSpeChar2", "已設定"
					Else 
						dlgText "BtnSelSpeChar2", "尚未設定"
					End If
					SpeCharSelect.TextBoxChar = ""
						
					Call Step4_Button_Enable
					
				Case "BtnSelSpeChar3"
					
					Call Step4_Button_Disable
					
					SpeCharSelect.TextBoxChar = sSelChar3
					Button = Dialog (SpeCharSelect)
					Call X_Char_Select_Function("Char3")
					sSelType3 = SpeCharSelect.OptButtonGroupTxt1
					If sSelChar3 <> "" Then 
						dlgText "BtnSelSpeChar3", "已設定"
					Else 
						dlgText "BtnSelSpeChar3", "尚未設定"
					End If
					SpeCharSelect.TextBoxChar = ""
						
					Call Step4_Button_Enable
					
				Case "BtnSelSpeChar4"
					
					Call Step4_Button_Disable
					
					SpeCharSelect.TextBoxChar = sSelChar4
					Button = Dialog (SpeCharSelect)
					Call X_Char_Select_Function("Char4")
					sSelType4 = SpeCharSelect.OptButtonGroupTxt1
					If sSelChar4 <> "" Then 
						dlgText "BtnSelSpeChar4", "已設定"
					Else 
						dlgText "BtnSelSpeChar4", "尚未設定"
					End If
					SpeCharSelect.TextBoxChar = ""
						
					Call Step4_Button_Enable
					
				Case "BtnSelSpeChar5"
					
					Call Step4_Button_Disable
					
					SpeCharSelect.TextBoxChar = sSelChar5
					Button = Dialog (SpeCharSelect)
					Call X_Char_Select_Function("Char5")
					sSelType5 = SpeCharSelect.OptButtonGroupTxt1
					If sSelChar5 <> "" Then 
						dlgText "BtnSelSpeChar5", "已設定"
					Else 
						dlgText "BtnSelSpeChar5", "尚未設定"
					End If
					SpeCharSelect.TextBoxChar = ""
						
					Call Step4_Button_Enable
				
				Case "BtnSelSpeChar6"
					
					Call Step4_Button_Disable
					
					SpeCharSelect.TextBoxChar = sSelChar6
					Button = Dialog (SpeCharSelect)
					Call X_Char_Select_Function("Char6")
					sSelType6 = SpeCharSelect.OptButtonGroupTxt1
					If sSelChar6 <> "" Then 
						dlgText "BtnSelSpeChar6", "已設定"
					Else 
						dlgText "BtnSelSpeChar6", "尚未設定"
					End If
					SpeCharSelect.TextBoxChar = ""
						
					Call Step4_Button_Enable
				
				Case "BtnAccPairingDr"
					
					Call Step4_Button_Disable
					AccPairingMode = 1
					Button = Dialog ( AccPairing)
					DebitAccounts = tempListbox2
					If sSelAcc1 = "OK" Then 
						dlgText "BtnAccPairingDr", "已設定"
					Else 
						dlgText "BtnAccPairingDr", "尚未設定"
					End If					
					Call Step4_Button_Enable
					
				Case "BtnAccPairingCr"
					
					Call Step4_Button_Disable
					AccPairingMode = 2
					Button = Dialog (AccPairing)
					CreditAccounts = tempListbox2
					If sSelAcc2 = "OK" Then 
						dlgText "BtnAccPairingCr", "已設定"
					Else 
						dlgText "BtnAccPairingCr", "尚未設定"
					End If				
					Call Step4_Button_Enable

				Case "QuickSummary"
					Call Step4_Button_Disable
					Button = Dialog (CriteriaLogSum)
					Call Step4_Button_Enable

				Case "DropListSelNum1"
					'If  FieldArray_Num(SuppValue%) =  "傳票金額_JE" Then 
					'	DlgEnable "DropListNumAcc", 1
					'	DlgEnable "ChkBoxNumAcc", 1
					'Else
					'	DlgEnable "DropListNumAcc", 0
					'	DlgValue "ChkBoxNumAcc", 0
					'	DlgEnable "ChkBoxNumAcc", 0
					'End If
				
				Case "BtnRun"
						
					'輸入條件基本檢查
					Dim Num1 As Double
					Dim Num2 As Double
					Num1 = Criteria.TextBoxFromNum1
					Num2 = Criteria.TextBoxToNum1
										
					sMsg = "" 
					
					If Criteria.ChkBoxModfRoutA3 = 1 Then
						'If FieldArray_AddAccPairing(Criteria.DropListAdd1De) = FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) Then 
						'	sMsg = "請檢查科目配對選項是否已正確挑選"
						'ElseIf FieldArray_AddAccPairing(Criteria.DropListAdd1De) = "Select..." Or FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) = "Select..."  Then
						'	sMsg = "請檢查科目配對選項是否已正確挑選"
						'End If
						'If FieldArray_AddAccPairing(Criteria.DropListAdd1De) = "Select..." Or FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) = "Select..."  Then
						'	sMsg = "請檢查科目配對選項是否已正確挑選"
						'End If
						If sSelAcc1 <> "OK" Or sSelAcc2 <> "OK" Then
							sMsg = "請檢查科目配對選項是否已正確挑選"
						End If

					End If

					If Criteria.ChkBoxSelDate1 = 1 Then
						If FieldArray_date(Criteria.DropListSelDate1) = "Select..." Then 
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的日期欄位"
							Else 
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的日期欄位"
							End If
						End If 
      			    		
						If Criteria.ChkBoxSelDate2 = 1 And FieldArray_date(Criteria.DropListSelDate1) = FieldArray_date(Criteria.DropListSelDate2) Then
							If sMsg = "" Then 
								sMsg = "請勿挑選重複的日期欄位"
							Else
								sMsg = sMsg & Chr(10) & "請勿挑選重複的日期欄位"
							End If
						End If 
      			     		
						' 有勾選卻沒有下條件
						If Criteria.TextBoxFromDate1 = "" And  Criteria.TextBoxToDate1 = "" And sSelDate1 = "" Then 
							If sMsg = "" Then 
								sMsg = "你已勾選【" & FieldArray_date(Criteria.DropListSelDate1) & "】，請輸入該日期篩選條件"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_date(Criteria.DropListSelDate1) & "】，請輸入該日期篩選條件"
							End If
						End If
						
						If Criteria.TextBoxFromDate1 <> "" And  Criteria.TextBoxToDate1 <> ""  Then
							If Criteria.TextBoxFromDate1 >  Criteria.TextBoxToDate1 Then
								If sMsg <> "" Then 
									sMsg =  "你已勾選【" & FieldArray_date(Criteria.DropListSelDate1) & "】，請檢查所輸入之日期資訊，結束日期不可小於起始日期"
								Else
									sMsg = sMsg & Chr(10) &  "你已勾選【" & FieldArray_date(Criteria.DropListSelDate1) & "】，請檢查所輸入之日期資訊，結束日期不可小於起始日期"
								End If 
							End If 
						End If
					End If 

					If Criteria.ChkBoxSelDate2 = 1 Then
						If FieldArray_date(Criteria.DropListSelDate2) = "Select..." Then 
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的日期欄位"
							Else
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的日期欄位"
							End If
						End If 
      			    		
						'If Criteria.ChkBoxSelDate1 = 1 And FieldArray_date(Criteria.DropListSelDate1) = FieldArray_date(Criteria.DropListSelDate2) Then
						'	If sMsg = "" Then 
						'		sMsg = "請勿挑選重複的日期欄位"
						'	Else
						'		sMsg = sMsg & Chr(10) & "請勿挑選重複的日期欄位"
						'	End If 
						'End If 
      			     		
						' 有勾選卻沒有下條件
						If Criteria.TextBoxFromDate2 = "" And  Criteria.TextBoxToDate2 = "" And sSelDate2 = "" Then 
							If sMsg = "" Then
								sMsg = "你已勾選【" & FieldArray_date(Criteria.DropListSelDate2) & "】，請輸入該日期篩選條件"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_date(Criteria.DropListSelDate2) & "】，請輸入該日期篩選條件"
							End If
						End If
						
						If Criteria.TextBoxFromDate2 <> "" And  Criteria.TextBoxToDate2 <> ""  Then
							If Criteria.TextBoxFromDate2 >  Criteria.TextBoxToDate2 Then
								If sMsg <> "" Then 
									sMsg = "你已勾選【" & FieldArray_date(Criteria.DropListSelDate2) & "】，請檢查所輸入之日期資訊，結束日期不可小於起始日期"
								Else
									sMsg = sMsg & Chr(10) &  "你已勾選【" & FieldArray_date(Criteria.DropListSelDate2) & "】，請檢查所輸入之日期資訊，結束日期不可小於起始日期"
								End If 
							End If 
						End If
						
					End If
					
					If Criteria.ChkBoxSelNum1 = 1 Then
					
						If FieldArray_Num(Criteria.DropListSelNum1) = "Select..." Then 
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的數字型態欄位"
							Else 
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的數字型態欄位"
							End If
						End If 
							
						' 沒有填寫任一數字區間
		    				If iAllTrim( Criteria.TextBoxFromNum1) = "" And  iAllTrim( Criteria.TextBoxToNum1) = "" Then 
		    					If sMsg = "" Then		    					
								sMsg = "你已勾選【" & FieldArray_Num(Criteria.DropListSelNum1) & "】，請輸入該數字型態的篩選條件"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Num(Criteria.DropListSelNum1) & "】，請輸入該數字型態的篩選條件"
							End If
		    				End If
		    				
						If iAllTrim( Criteria.TextBoxFromNum1) <> "" And  iAllTrim( Criteria.TextBoxToNum1) <> ""  Then
							If Num1 >  Num2 Then
								If sMsg = "" Then
									sMsg = "你已勾選【" & FieldArray_Num(Criteria.DropListSelNum1) & "】，請檢查所輸入之數字資訊，結束數字不可小於起始數字"
								Else
									sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Num(Criteria.DropListSelNum1) & "】，請檢查所輸入之數字資訊，結束數字不可小於起始數字"
								End If 
							End If 
						End If
						
						If Criteria.ChkBoxNumAcc = 1 And FieldArray_AddAccPairing(Criteria.DropListNumAcc) = "Select..." Then
							If sMsg = "" Then
								sMsg = "你已勾選【" & FieldArray_Num(Criteria.DropListSelNum1) & "】且要考量之特定科目類別，但特定科目類別尚未選擇"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Num(Criteria.DropListSelNum1) & "】且要考量之特定科目類別，但特定科目類別尚未選擇"
							End If						
						End If 
					End If
					
					If Criteria.ChkBoxNumAcc = 1 And FieldArray_AddAccPairing(Criteria.DropListNumAcc) = "Select..." Then
						If sMsg = "" Then
							sMsg = "你已勾選【" & FieldArray_Num(Criteria.DropListSelNum1) & "】且要考量之特定科目類別，但特定科目類別尚未選擇"
						Else
							sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Num(Criteria.DropListSelNum1) & "】且要考量之特定科目類別，但特定科目類別尚未選擇"
						End If						
					End If  
					
					If Criteria.ChkBoxSelChar1 = 1 Then
						' 判斷是否選擇欄位
						If FieldArray_Mix(Criteria.DropListSelChar1) = "Select..." Then
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的文字欄位"
							Else
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的文字欄位"
							End If
						End If
      			       		
						' 文字欄位選擇重複 
						'If Criteria.ChkBoxSelChar2 = 1  And FieldArray_Mix(Criteria.DropListSelChar1) = FieldArray_Mix(Criteria.DropListSelChar2) Then
						'	If sMsg = "" Then
						'		sMsg = "請勿挑選重複之文字欄位"
						'	Else
						'		sMsg = sMsg & Chr(10) & "請勿挑選重複之文字欄位"
						'	End If
						'End If
      			       		
						' 有勾選卻沒有下條件
						If sSelChar1  = ""  Then
							If sMsg = "" Then
								sMsg = "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar1) & "】，請輸入你要篩選的文字資訊"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar1) & "】，請輸入你要篩選的文字資訊"
							End If
						End If					
					End If
					
					If Criteria.ChkBoxSelChar2 = 1 Then
						' 判斷是否選擇欄位
						If FieldArray_Mix(Criteria.DropListSelChar2) = "Select..." Then
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的文字欄位"
							Else
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的文字欄位"
							End If
						End If
      			       		
						' 文字欄位選擇重複 
						'If Criteria.ChkBoxSelChar1 = 1  And FieldArray_Char(Criteria.DropListSelChar1) = FieldArray_Char(Criteria.DropListSelChar2) Then
						'	If sMsg = "" Then
						'		sMsg = "請勿挑選重複之文字欄位"
						'	Else
						'		sMsg = sMsg & Chr(10) & "請勿挑選重複之文字欄位"
						'	End If
						'End If
      			       		
						' 有勾選卻沒有下條件
						If sSelChar2  = ""  Then
							If sMsg = "" Then
								sMsg = "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar2) & "】，請輸入你要篩選的文字資訊"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar2) & "】，請輸入你要篩選的文字資訊"
							End If
						End If					
					End If
					
					If Criteria.ChkBoxSelChar3 = 1 Then
						' 判斷是否選擇欄位
						If FieldArray_Mix(Criteria.DropListSelChar3) = "Select..." Then
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的文字欄位"
							Else
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的文字欄位"
							End If
						End If
     			       			       		
						' 有勾選卻沒有下條件
						If sSelChar3  = ""  Then
							If sMsg = "" Then
								sMsg = "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar3) & "】，請輸入你要篩選的文字資訊"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar3) & "】，請輸入你要篩選的文字資訊"
							End If
						End If					
					End If
					
					If Criteria.ChkBoxSelChar4 = 1 Then
						' 判斷是否選擇欄位
						If FieldArray_Mix(Criteria.DropListSelChar4) = "Select..." Then
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的文字欄位"
							Else
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的文字欄位"
							End If
						End If
     			       			       		
						' 有勾選卻沒有下條件
						If sSelChar4  = ""  Then
							If sMsg = "" Then
								sMsg = "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar4) & "】，請輸入你要篩選的文字資訊"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar4) & "】，請輸入你要篩選的文字資訊"
							End If
						End If					
					End If
					
					If Criteria.ChkBoxSelChar5 = 1 Then
						' 判斷是否選擇欄位
						If FieldArray_Mix(Criteria.DropListSelChar5) = "Select..." Then
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的文字欄位"
							Else
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的文字欄位"
							End If
						End If
     			       			       		
						' 有勾選卻沒有下條件
						If sSelChar5  = ""  Then
							If sMsg = "" Then
								sMsg = "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar5) & "】，請輸入你要篩選的文字資訊"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar5) & "】，請輸入你要篩選的文字資訊"
							End If
						End If					
					End If
					
					If Criteria.ChkBoxSelChar6 = 1 Then
						' 判斷是否選擇欄位
						If FieldArray_Num(Criteria.DropListSelChar6) = "Select..." Then
							If sMsg = "" Then 
								sMsg = "請挑選你所要篩選的文字欄位"
							Else
								sMsg = sMsg & Chr(10) & "請挑選你所要篩選的文字欄位"
							End If
						End If
     			       			       		
						' 有勾選卻沒有下條件
						If sSelChar6  = ""  Then
							If sMsg = "" Then
								sMsg = "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar6) & "】，請輸入你要篩選的文字資訊"
							Else
								sMsg = sMsg & Chr(10) & "你已勾選【" & FieldArray_Mix(Criteria.DropListSelChar6) & "】，請輸入你要篩選的文字資訊"
							End If
						End If					
					End If
					
					
					Dim a As Integer
					Dim b As Integer
					Dim fieldArrayforcheck(5) As String
					Dim cField(5) As String
					
					' 初始化摘要欄位
					fieldArrayforcheck(0) = Criteria.DropListSelChar1
					fieldArrayforcheck(1) = Criteria.DropListSelChar2
					fieldArrayforcheck(2) = Criteria.DropListSelChar3
					fieldArrayforcheck(3) = Criteria.DropListSelChar4
					fieldArrayforcheck(4) = Criteria.DropListSelChar5
					fieldArrayforcheck(5) = Criteria.DropListSelChar6
					
					cField(0) = Criteria.ChkBoxSelChar1
					cField(1) = Criteria.ChkBoxSelChar2
					cField(2) = Criteria.ChkBoxSelChar3
					cField(3) = Criteria.ChkBoxSelChar4
					cField(4) = Criteria.ChkBoxSelChar5
					cField(5) = Criteria.ChkBoxSelChar6
					
					' 檢查重複的摘要欄位
					'For a = 0 To UBound(fieldArrayforcheck) - 1
					'    For b = a + 1 To UBound(fieldArrayforcheck)
					'        If cField(a) = 1 And  cField(b) = 1 Then
					'            If fieldArrayforcheck(a) = fieldArrayforcheck(b) Then
					'                If sMsg = "" Then
					'                    sMsg = "請勿挑選重複之文字欄位"
					'                Else
					'                    sMsg = sMsg & Chr(10) & "請勿挑選重複之文字欄位"
					'                End If
					'            End If
					'        End If
					'    Next b
					'Next a
					
					If sMsg <> "" Then 
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION ,"步驟四 設定資料篩選發現錯誤 警示訊息")
						GoTo ExitBtnRun
					End If
					
					If Criteria.ChkBoxModfRout4 = 1 Then
						
						If Criteria.TextBoxRout4="" Then
							sMsg = "你已勾選【" & Criteria.TextBoxRout4 & "】，請輸入你要篩選的尾數資訊"
							Result = MsgBox( sMsg ,MB_OK Or MB_ICONEXCLAMATION , "步驟四 額外測試項目輸入錯誤 警示訊息")
							Exit Function
						End If
						
						sTemp = iRemove(iAllTrim( iUpper(iReplace(Criteria.TextBoxRout4,",","|")))," ")
						If iRight(sTemp,1) = "|" Then 
							sTemp = iLeft(sTemp, Len(sTemp) -1)
						End If
					
						sTemp = iRemove(sTemp, "|")
						
						For j = 1 To Len(sTemp)
							sChar = iAllTrim(Mid(sTemp, j, 1))
							If iFindOneOf(sChar, "0123456789") = 0 Then
								'DlgEnable "BtnOK" ,  1
								'DlgEnable "BtnCancel" ,  1
								sMsg = "額外測試項目-尾數測試作業僅接受輸入數字"
								Result = MsgBox( sMsg ,MB_OK Or MB_ICONEXCLAMATION , "步驟四 額外測試項目輸入錯誤 警示訊息")			
								S1Check = 1
								Exit Function
							End If
						Next j
								 
					End If 						
					
				

					
					'---- 勾選條件/輸入值確認完畢------
															
					Call Step4_Button_Disable
						
					Call X_Get_Criteria_Info
									
					sLog = "Pre-screening "
				
					' 沒有選擇任何選項
					Dim NoSelect As Integer
					NoSelect = Step4_Check_Select()
					If NoSelect = 1 Then GoTo ExitBtnRun
					
					'20181023 Add
					If sVer = "TW" Then 
						NoSelect = Step4_Check_Select_TW()
						If NoSelect = 1 Then GoTo ExitBtnRun					
					End If
					
					Call Z_Delete_File("#Temp.IDM")
					
					sTemp1 = " 1 = 1 "
					sLog = ""
					sLogMemo = ""
					sTagCount = 0
					C1 = 0
					C2 = 0
					If Criteria.ChkBoxRout1 = 1 Then				
						sTemp1 = sTemp1 & " .AND. PRESCR_R1= " & Chr(34) & "Y" & Chr(34)
						sLogMemo = sLogMemo + " @if(R1_TAG = " & Chr(34) & "Y" & Chr(34) & ",1,0) + "
						If Z_File_Exist("#PreScr-R1-All.IDM") = True Then 
							sTagCount = sTagCount + 1
						Else
							sTagCount = sTagCount
						End If 
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 於期末財務報表準備期間核准之分錄 "
					End If 

					If Criteria.ChkBoxRout2 = 1 Then 
						sTemp1 = sTemp1 & " .AND. PRESCR_R2 = " & Chr(34) & "Y" & Chr(34)
						sLogMemo = sLogMemo + " @if(R2_TAG = " & Chr(34) & "Y" & Chr(34) & " ,1,0) + " 
						If  Z_File_Exist("#PreScr-R2-All.IDM") = True Then
							sTagCount = sTagCount + 1
						Else
							sTagCount = sTagCount 
						End If
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 分錄摘要出現特定描述 "
					End If
					
					If Criteria.ChkBoxRout3 = 1 Then
						sTemp1 = sTemp1   & " .AND. PRESCR_R3 = " & Chr(34) & "Y" & Chr(34)
						sLogMemo = sLogMemo + " @if(R3_TAG = " & Chr(34) & "Y" & Chr(34) & ",1,0) + "
						If  Z_File_Exist("#PreScr-R3-All.IDM") = True Then
							sTagCount = sTagCount + 1
						Else
							sTagCount = sTagCount 
						End If 
						C1 = C1+ 1
						sLog = sLog & "#" & C1 &  ". 未預期出現之特定借貸組合 "
					End If 
					
					If Criteria.ChkBoxRout4 = 1 Then
						sTemp1 = sTemp1 & " .AND. PRESCR_R4 = " & Chr(34) & "Y" & Chr(34) 
						sLogMemo = sLogMemo + " @if(R4_TAG = " & Chr(34) & "Y" & Chr(34) & ",1,0) + "
						If  Z_File_Exist("#PreScr-R4-All.IDM") = True Then
							sTagCount = sTagCount + 1
						Else
							sTagCount = sTagCount 
						End If 
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 分錄金額中有連續0的尾數 "
					End If
						
					'If Criteria.ChkBoxModfRout2 = 1 Then
					'	sTemp1 = sTemp1 & " .AND. PRESCR_A2 = " & Chr(34) & "Y" & Chr(34)
					'	sLogMemo = sLogMemo + " @if(A2_TAG = " & Chr(34) & "Y" & Chr(34) & ",1,0) + "
					'	sTagCount = sTagCount + 1
					'	C1 = C1 + 1
					'	sLog = sLog & "#" & C1 & ". " & sA2_Memo & " "
					'End If 
					
					'If Criteria.ChkBoxModfRout3 = 1 Then
					'	sTemp1 = sTemp1 & " .AND. PRESCR_A3 = " & Chr(34) & "Y" & Chr(34)
					'	sLogMemo = sLogMemo + " @if(A3_TAG = " & Chr(34) & "Y" & Chr(34) & ",1,0) + "
					'	sTagCount = sTagCount + 1
					'	C1 = C1 + 1
					'	sLog = sLog & "#" & C1 & ". "  & sA3_Memo & " "
					'End If
					
					
					If Criteria.ChkBoxWeeekend_DocDate = 1 Then
						sTemp1 = sTemp1 &  " .AND. DOC_WEEKEND_JE_T =" & Chr(34) & "Y" & Chr(34)
						sLogMemo = sLogMemo + " @if(DOC_WEEKEND_JE_T = " & Chr(34) & "Y" & Chr(34) & ",1,0) + "
						sTagCount = sTagCount + 1
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 核准日期在非工作日之週末 "
					End If
				
					If Criteria.ChkBoxWeeekend_PostDate = 1 Then
						sTemp1 = sTemp1 &  " .AND. POST_WEEKEND_JE_T =" & Chr(34) & "Y" & Chr(34) 
						sLogMemo = sLogMemo + " @if(POST_WEEKEND_JE_T = " & Chr(34) & "Y" & Chr(34) & ",1,0) + "
						sTagCount = sTagCount + 1
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 總帳日期在非工作日之週末 "
					End If 
					
					If Criteria.ChkBoxHoliday_DocDate = 1 Then
						sTemp1 = sTemp1 & " .AND. DOC_HOLIDAY_JE_T=" & Chr(34) & "Y" & Chr(34) 
						sLogMemo = sLogMemo + " @if(DOC_HOLIDAY_JE_T = " & Chr(34) & "Y" & Chr(34) & ",1,0) + "
						sTagCount = sTagCount + 1
						C1 = C1 + 1
						sLog = sLog & "#" & C1 &  ". 核准日期在國定假日 "
					End If
						
					If Criteria.ChkBoxHoliday_PostDate = 1 Then
						sTemp1 = sTemp1 &  " .AND. POST_HOLIDAY_JE_T=" & Chr(34) & "Y" & Chr(34)
						sLogMemo = sLogMemo + " @if(POST_HOLIDAY_JE_T = " & Chr(34) & "Y" & Chr(34) & ",1,0) + " 
						sTagCount = sTagCount + 1
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 總帳日期在國定假日 "
					End If
				
					If Criteria.ChkBoxIsManual = 1 Then
						sTemp1 = sTemp1 &  " .AND. 人工傳票否_JE_S = 1"
						sLogMemo = sLogMemo + " @if(人工傳票否_JE_S = 1,1,0) + " 
						sTagCount = sTagCount + 1
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 人工分錄 "
					End If 	
					
					If Criteria.ChkBoxDebit = 1 Then
						sTemp1 = sTemp1 & " .AND. DEBIT_CREDIT_JE_T == " & Chr(34) & "DEBIT" & Chr(34)
						sLogMemo = sLogMemo + " @if(DEBIT_CREDIT_JE_T == " & Chr(34) & "DEBIT" & Chr(34) & ",1,0) + "
						sTagCount = sTagCount + 1
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 僅考量借方傳票 "
					End If 
					
					If Criteria.ChkBoxCredit = 1 Then
						sTemp1 = sTemp1 & " .AND. DEBIT_CREDIT_JE_T == " & Chr(34) & "CREDIT" & Chr(34)
						sLogMemo = sLogMemo + " @if(DEBIT_CREDIT_JE_T == " & Chr(34) & "CREDIT" & Chr(34) & ",1,0) + "
						sTagCount = sTagCount + 1
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 僅考量貸方傳票 "
					End If 

					If Criteria.ChkBoxIsDescNull = 1 Then
						sTemp1 = sTemp1 & " .AND. @IsBlank(傳票摘要_JE) = 1 "
						sLogMemo = sLogMemo + " @if(@IsBlank(傳票摘要_JE) = 1 ,1,0) + "
						If  GetTotal("#Null-GL_Description_Criteria.IDM" ,"" ,"DBCount" ) <> 0 Then
							sTagCount = sTagCount + 1
						Else
							sTagCount = sTagCount 
						End If 
						C1 = C1 + 1
						sLog = sLog & "#" & C1 & ". 分錄無摘要描述(即空白摘要) "
					End If 
					
																			
					If sTemp1 <> " 1 = 1 " Then
						sLogMemo = sLogMemo + " 0 " 
						Call Z_DirectExtractionTable("#GL#.IDM","#Temp.IDM", sTemp1)
						If GetTotal("#Temp.IDM" ,"" ,"DBCount" ) <> 0 Then  Call Step4_Routines_Tag(sTagCount)	
					End If   
					
					sTagCount  = 0
					
					'If Criteria.ChkBoxIsDescNull = 1 Then 
					'	'sTemp1 = "傳票摘要_JE = " & Chr(34) & Chr(34)   
					'	sTemp1 = "@IsBlank(傳票摘要_JE) = 1"
					'	sTagCount = sTagCount  + 1
					'	C1 = C1 + 1
					'	If Z_File_Exist("#Temp.IDM") = True Then
					'		Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
					'		Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
					'		Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
					'		Call Z_Delete_File("#Temp1.IDM")
					'		Call Z_Delete_File("#Temp2.IDM")
					'	Else
					'		Call Z_DirectExtractionTable("#GL#In_Period.IDM","#Temp1.IDM", sTemp1)
					'		Call Step4_JoinDatabase("#GL#In_Period.IDM", "#Temp1.IDM", "#Temp.IDM")
					'		Call Z_Delete_File("#Temp1.IDM")
					'	End If
					'	If GetTotal("#Temp.IDM" ,"" ,"DBCount" ) = 0 Then GoTo step4_Final
					'End  If 					
									
					If Criteria.ChkBoxModfRoutA3 = 1 Then
						sTagCount = sTagCount + 1
						C1 = C1 + 1
						DebitTemp = AccPairingtoString(DebitAccounts)
						CreditTemp = AccPairingtoString(CreditAccounts)
						'If Criteria.AccountparingGroup = 0 Then sLog = sLog & "#" & C1 & ". 新增科目配對篩選條件為 借方 - " & FieldArray_AddAccPairing(Criteria.DropListAdd1De) & _
						'					"、貸方 - " & FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) & " "
						'If Criteria.AccountparingGroup = 1 Then sLog = sLog & "#" & C1 & ". 新增科目配對篩選條件為 借方 - " & FieldArray_AddAccPairing(Criteria.DropListAdd1De) & _
						'					"、貸方 - 非 " & FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) & " "
						'If Criteria.AccountparingGroup = 2 Then sLog = sLog & "#" & C1 & ". 新增科目配對篩選條件為 借方 - 非 " & FieldArray_AddAccPairing(Criteria.DropListAdd1De) & _
						'					"、貸方 - " & FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) & " "
						If Criteria.AccountparingGroup = 0 Then sLog = sLog & "#" & C1 & ". 新增科目配對篩選條件為 借方 - " & DebitTemp & _
											"、貸方 - " & CreditTemp & " "
						If Criteria.AccountparingGroup = 1 Then sLog = sLog & "#" & C1 & ". 新增科目配對篩選條件為 借方 - " & DebitTemp & _
											"、貸方 - 非 " & CreditTemp & " "
						If Criteria.AccountparingGroup = 2 Then sLog = sLog & "#" & C1 & ". 新增科目配對篩選條件為 借方 - 非 " & DebitTemp & _
											"、貸方 - " & CreditTemp & " "

						Call Z_Delete_File("#GL#Account_Mapping_ALL.IDM")
							
						If Criteria.AccountparingGroup = 0 Then Call Step4 ("Test 1","A")
						If Criteria.AccountparingGroup = 1 Then Call Step4 ("Test 1","B")
						If Criteria.AccountparingGroup = 2 Then Call Step4 ("Test 1","C")
							
						If GetTotal("#GL#Account_Mapping_Debit.IDM" ,"" ,"DBCount" ) * GetTotal("#GL#Account_Mapping_Credit.IDM" ,"" ,"DBCount" ) = 0 Then
							Call Z_Delete_File("#GL#Account_Mapping_Debit.IDM")
							Call Z_Delete_File("#GL#Account_Mapping_Credit.IDM")
							Call Z_Delete_File("#GL#Account_Mapping_ALL.IDM")
							'Derek Memo 需新增Temp檔
							Call Z_Delete_File("#Temp.IDM")
							sTemp1 = " 1 = 2 " 
							Call Z_DirectExtractionTable("#GL#.IDM","#Temp.IDM", sTemp1)
							GoTo sChkBoxSelDate1
						Else 		
							Call Step4 ("Test 2","0")		
											
							If GetTotal("#GL#Account_Mapping_ALL.IDM" ,"" ,"DBCount" ) = 0 Then 
								Call Z_Delete_File("#GL#Account_Mapping_ALL.IDM")
								Call Z_Delete_File("#GL#Account_Mapping_Debit.IDM")
								Call Z_Delete_File("#GL#Account_Mapping_Credit.IDM")
								'Derek Memo 需新增Temp檔
								Call Z_Delete_File("#Temp.IDM")
								sTemp1 = " 1 = 2 " 
								Call Z_DirectExtractionTable("#GL#.IDM","#Temp.IDM", sTemp1)
								GoTo sChkBoxSelDate1
								'GoTo ExitBtnRun
							Else
								
								If Z_File_Exist("#Temp.IDM") = True Then
									Call Step4_JoinDatabase("#Temp.IDM", "#GL#Account_Mapping_ALL.IDM", "#Temp1.IDM")

									Call Z_Rename_DB("#Temp1.IDM", "#Temp.IDM")
										
								Else
									Call Step4_JoinDatabase("#GL#.IDM", "#GL#Account_Mapping_ALL.IDM", "#Temp.IDM")
								End If
																	
								GoTo sChkBoxSelDate1
																		
							End If
						End If
					End If
   
       			     	       	
       			     	       	sChkBoxSelDate1 :
					Call Z_Delete_File("#GL#Account_Mapping_ALL.IDM")
					Call Z_Delete_File("#GL#Account_Mapping_Debit.IDM")
					Call Z_Delete_File("#GL#Account_Mapping_Credit.IDM")
       			     	       	
       			     	       	sTemp1 = ""		        		
       			     	       	sTemp2 = ""
					If Criteria.ChkBoxSelDate1 = 1 Then
						If sSelDate1 <> "" Then 
							sTemp1 = "@List(@Dtoc(" & FieldArray_date(Criteria.DropListSelDate1) & "," & Chr(34)  & "YYYYMMDD"  & Chr(34)  & ")" & sSelDate1 & ") "  
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 日期欄位【" & FieldArray_date(Criteria.DropListSelDate1) &  "】值為 - " &  sSelDate1 & " "
							GoTo  sChkBoxSelDate2
						End  If 
					
						If Criteria.TextBoxFromDate1 <> "" And  Criteria.TextBoxToDate1 <> ""  Then
							sTemp1 = " (" &  FieldArray_date(Criteria.DropListSelDate1) & " >= " + Chr(34) + iRemove(Criteria.TextBoxFromDate1,"/") & Chr(34)  & " .AND. " & _ 
						                 FieldArray_date(Criteria.DropListSelDate1) & " <= "  &  Chr(34) &  iRemove(Criteria.TextBoxToDate1,"/") & Chr(34) &  ") "
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 日期欄位【" & FieldArray_date(Criteria.DropListSelDate1) & "】值介於 " & Criteria.TextBoxFromDate1 & " 和 " & Criteria.TextBoxToDate1 & " "
							GoTo sChkBoxSelDate2
						End If
						
						' 判斷 > or <
						If  Criteria.TextBoxFromDate1 = "" And  Criteria.TextBoxToDate1 <> ""  Then
							sTemp1 = FieldArray_date(Criteria.DropListSelDate1)  & " >= " & Chr(34) & iRemove(Criteria.TextBoxToDate1,"/") & Chr(34) 
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 日期欄位【" & FieldArray_date(Criteria.DropListSelDate1) & "】值大於(含)  " & Criteria.TextBoxToDate1 & " "
							GoTo sChkBoxSelDate2
						ElseIf Criteria.TextBoxFromDate1 <>  "" And  Criteria.TextBoxToDate1 = ""  Then
							sTemp1 = FieldArray_date(Criteria.DropListSelDate1)  & " <= " & Chr(34) & iRemove(Criteria.TextBoxFromDate1,"/") & Chr(34)
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 日期欄位【" & FieldArray_date(Criteria.DropListSelDate1) & "】值小於(含)  " & Criteria.TextBoxFromDate1 & " "
							GoTo sChkBoxSelDate2
						End If
					End If 
      			        	
					sChkBoxSelDate2 :
					
					If Criteria.ChkBoxSelDate2 = 1 Then
						If sSelDate2 <> "" Then 
							If sTemp1 = "" Then
								sTemp1 = " @List(@Dtoc(" & FieldArray_date(Criteria.DropListSelDate2) & "," & Chr(34)  & "YYYYMMDD"  & Chr(34)  & ")" & sSelDate2  & ") "  
							Else
								sTemp1 = sTemp1 & " .AND. @List(@Dtoc(" & FieldArray_date(Criteria.DropListSelDate2) & "," & Chr(34)  & "YYYYMMDD"  & Chr(34)  & ")" & sSelDate2  & ") " 
							End If
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 日期欄位【" & FieldArray_date(Criteria.DropListSelDate2) & "】值為 - " & sSelDate2 & " "
							GoTo sChkBoxNum
						End  If 
					
						If Criteria.TextBoxFromDate2 <> "" And  Criteria.TextBoxToDate2 <> ""  Then
							If sTemp1 = "" Then 
								sTemp1 = " (" &  FieldArray_date(Criteria.DropListSelDate2) & " >= " & Chr(34) & iRemove(Criteria.TextBoxFromDate2,"/") & Chr(34)  & " .AND. " & _ 
							                FieldArray_date(Criteria.DropListSelDate2) & " <= "  &  Chr(34) &  iRemove(Criteria.TextBoxToDate2,"/") & Chr(34) &  ") "
							Else
								sTemp1 = sTemp1 &  " .AND.  (" &  FieldArray_date(Criteria.DropListSelDate2) & " >= " & Chr(34) & iRemove(Criteria.TextBoxFromDate2,"/") & Chr(34)  & " .AND. " & _ 
							                FieldArray_date(Criteria.DropListSelDate2) & " <= "  &  Chr(34) &  iRemove(Criteria.TextBoxToDate2,"/") & Chr(34) &  ") "
							End If	
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 日期欄位【" & FieldArray_date(Criteria.DropListSelDate2) & "】值介於 " & Criteria.TextBoxFromDate2 & " 和 " & Criteria.TextBoxToDate2 & " "
							GoTo sChkBoxNum
						End If
						
						' 判斷 > or <
						If Criteria.TextBoxFromDate2 = "" And  Criteria.TextBoxToDate2 <> ""  Then
							If sTemp1 = "" Then
								sTemp1 = FieldArray_date(Criteria.DropListSelDate2)  & " >= " & Chr(34) & iRemove(Criteria.TextBoxToDate2,"/") & Chr(34)
							Else
								sTemp1 = sTemp1 & " .AND. " & FieldArray_date(Criteria.DropListSelDate2)  & " >= " & Chr(34) & iRemove(Criteria.TextBoxToDate2,"/") & Chr(34)
							End If
							C1 = C1 + 1
								sLog = sLog & "#" & C1 & ". 日期欄位【"&  FieldArray_date(Criteria.DropListSelDate2) & "】值大於(含)  " & Criteria.TextBoxToDate2 & " "
							GoTo sChkBoxNum
							ElseIf Criteria.TextBoxFromDate2 <>  "" And  Criteria.TextBoxToDate2 = ""  Then
							If sTemp1 = "" Then
								sTemp1 = FieldArray_date(Criteria.DropListSelDate2)  & " =< " & Chr(34) & iRemove(Criteria.TextBoxFromDate2,"/") & Chr(34)
							Else
								sTemp1 = sTemp1 & " .AND. " & FieldArray_date(Criteria.DropListSelDate2)  & " =< " & Chr(34) & iRemove(Criteria.TextBoxFromDate2,"/") & Chr(34)
							End If
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 日期欄位【"&  FieldArray_date(Criteria.DropListSelDate2) & "】值小於(含)  " & Criteria.TextBoxFromDate2 & " "
							GoTo sChkBoxNum
						End If
	       			    	End If 
      				        	
				sChkBoxNum :		        	
					If Criteria.ChkBoxSelNum1 = 1 Then
											
						If iAllTrim( Criteria.TextBoxFromNum1) <> "" And  iAllTrim( Criteria.TextBoxToNum1) <> ""  Then
							sTemp2 = " (" & "@Abs( " & FieldArray_Num(Criteria.DropListSelNum1) & ") >= " & Num1 & " .AND. " & _ 
							             "@Abs(" & FieldArray_Num(Criteria.DropListSelNum1) & ") <= "  &  Num2 & ") "  
							If sTemp1 = "" Then
								sTemp1 = sTemp2
							Else
								sTemp1 = sTemp1 & " .AND. " &  sTemp2
							End If
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 數字欄位【" & FieldArray_Num(Criteria.DropListSelNum1) & "】值介於 "  & Num1 & " 和 " & Num2 & " "
							GoTo sChkNumAcc 'sChkBoxChar1
						End If

						' 判斷 > or <
						If Num1 = 0  And  Num2 <> 0  Then
							sTemp2 = "@Abs( " & FieldArray_Num(Criteria.DropListSelNum1) & ") >= " & Num2
							If sTemp1 = "" Then
								sTemp1 = sTemp2
							Else
								sTemp1 = sTemp1 & " .AND. " & sTemp2
							End If
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 數字欄位【" & FieldArray_Num(Criteria.DropListSelNum1) & "】值大於(含)  " & Num2 & " "
							GoTo sChkNumAcc 'sChkBoxChar1
						ElseIf Num1 <> 0 And  Num2 = 0 Then
							sTemp2 = "@Abs(" & FieldArray_Num(Criteria.DropListSelNum1) & ") <= " &  Num1
							
							If sTemp1 = "" Then
								sTemp1 = sTemp2
							Else
								sTemp1 = sTemp1 & " .AND. " & sTemp2
							End If
							
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 數字欄位【" & FieldArray_Num(Criteria.DropListSelNum1) & "】值小於(含) " & Num1 & " "
							GoTo sChkNumAcc 'sChkBoxChar1
						End If
						
						sChkNumAcc :
							If Criteria.ChkBoxNumAcc = 1 And FieldArray_AddAccPairing(Criteria.DropListNumAcc) <> "Select..." Then
								sLog = sLog & "且同時會計科目配對類別為 "  &  FieldArray_AddAccPairing(Criteria.DropListNumAcc)
								sTemp2 = sTemp2 &  " .AND. STANDARDIZED_ACCOUNT_NAME  = "  & Chr(34) & FieldArray_AddAccPairing(Criteria.DropListNumAcc)  & Chr(34)
								Call Step4_Num_Acc_Select(sTemp2) '#Temp_Account.IDM
							Else
								sTemp2 = ""
							End If
					Else
					
						If   Criteria.ChkBoxNumAcc = 1 And FieldArray_AddAccPairing(Criteria.DropListNumAcc) <> "Select..." Then
							C1 = C1 + 1
							sLog = sLog & "#" & C1 & ". 會計科目配對類別為 "  &  FieldArray_AddAccPairing(Criteria.DropListNumAcc)
							sTemp2 = "STANDARDIZED_ACCOUNT_NAME  = "  & Chr(34) & FieldArray_AddAccPairing(Criteria.DropListNumAcc)  & Chr(34)
							Call Step4_Num_Acc_Select(sTemp2) '#Temp_Account.IDM
						Else
							sTemp2 = ""
						End If
					End If
       					 
       					   				     	
					sChkBoxChar1 :
					If sTemp1 <> "" Then
						sTagCount = sTagCount  + 1
						If Z_File_Exist("#Temp.IDM") = True Then
							Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
							Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
							Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
							Call Z_Delete_File("#Temp1.IDM")
							Call Z_Delete_File("#Temp2.IDM")
						Else
							Call Z_DirectExtractionTable("#GL#.IDM","#Temp1.IDM", sTemp1)
							Call Step4_JoinDatabase("#GL#.IDM", "#Temp1.IDM", "#Temp.IDM")
							Call Z_Delete_File("#Temp1.IDM")
						End If											
					End If
      			        	
					If sTemp2 <> "" Then
						sTagCount = sTagCount  + 1
						If  Z_File_Exist("#Temp.IDM") = True  Then
							Call Step4_JoinDatabase("#Temp.IDM", "#Temp_Account.IDM", "#Temp2.IDM")
							Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
							Call Z_Delete_File("#Temp1.IDM")
							Call Z_Delete_File("#Temp_Account.IDM")
						Else
							Call Z_DirectExtractionTable("#GL#.IDM","#Temp.IDM", sTemp1)
							Call Step4_JoinDatabase("#Temp.IDM", "#Temp_Account.IDM", "#Temp2.IDM")
							Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
							Call Z_Delete_File("#Temp_Account.IDM")
						End If 
					End If
      			        	
	      			        	sTemp1 = ""
					sTemp2 = ""	
					
					If Criteria.ChkBoxModfRout4 = 1 Then							
						If  Criteria.TextBoxRout4<>"" Then						
						sTemp_new = iRemove(iAllTrim( iUpper(iReplace(Criteria.TextBoxRout4,",","|")))," ")
						If iRight(sTemp_new,1) = "|" Then 
							sTemp_new = iLeft(sTemp_new, Len(sTemp_new) -1)
						End If
						sT = ""
						iLe = Len(sTemp_new)
						sWord = ""
						For j = 1 To iLe
							sChar = Mid(sTemp_new, j, 1)
								If sChar <> "|" Then
									sWord = sWord & sChar
								Else
									i = Len(sWord)
									sT = sT & " @Right(@Str(@int(傳票金額_JE),1,0)," & i & ") =  "  & Chr(34) &  sWord & Chr(34) & "  .OR. "
									sWord = ""
								End If
								If j=iLe Then 
									i = Len(sWord)
									sT = sT & " @Right(@Str(@int(傳票金額_JE),1,0)," & i & ") =  "  &  Chr(34) & sWord & Chr(34)
								End If
						Next j
						sTemp1 = sT
						sTagCount = sTagCount  + 1
						C1 = C1 + 1 
						sLog = sLog & "#" & C1 & ". "  & "新增的特定尾數為：" & Criteria.TextBoxRout4 & ""  'sA4_Memo & " "

						End If
							If Z_File_Exist("#Temp.IDM") = True Then
								Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
								Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
								Z_Delete_File("#Temp1.IDM")
							Else
								Call Z_DirectExtractionTable("#GL#.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#GL#.IDM", "#Temp1.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								'Call Z_Delete_File("#PreScr-A4-All.IDM")
							End If					
					End If
					sTemp1 = ""			
					      			    
					If Criteria.ChkBoxSelChar1 = 1 Then
						'  條件產出
						If FieldArray_Mix(Criteria.DropListSelChar1) <> "Select..." And sSelChar1 <> "" Then	
							sTagCount = sTagCount  + 1
							C1 = C1 + 1						
							If  sSelChar1 = "JETEAM" Then 
							 	sTemp1 = GetSelectChar(sSelChar1, FieldArray_Mix(Criteria.DropListSelChar1))
							 	sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar1) & "】為空值"
							 Else
								Select Case  sSelType1
									Case 0
										sTemp1 = GetSelectChar(sSelChar1, FieldArray_Mix(Criteria.DropListSelChar1))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar1) & "】值包含 - " & sSelChar1 & " "
									Case 1
										sTemp1 = X_GetCriteriatoSel_Match(sSelChar1,FieldArray_Mix(Criteria.DropListSelChar1))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar1) & "】值為 - " & sSelChar1 & " "
									Case 2
										sTemp1=  "@Len(" & GetSelectChar(sSelChar1, FieldArray_Mix(Criteria.DropListSelChar1)) & ")= 0"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar1) & "】值不包含 - " & sSelChar1 & " "
									Case 3	
										sTemp1= ".NOT. (" & X_GetCriteriatoSel_Match(sSelChar1,FieldArray_Mix(Criteria.DropListSelChar1)) &")"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar1) & "】值不為 - " & sSelChar1 & " "
								End Select												
							End If 
							'sTemp1 = GetSelectChar(sSelChar1, FieldArray_Mix(Criteria.DropListSelChar1))
							'sTagCount = sTagCount  + 1
							'C1 = C1 + 1
							'sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar1) & "】值為 - " & sSelChar1 & " "
							SpeCharSelect.TextBoxChar  = ""
							If Z_File_Exist("#Temp.IDM") = True Then
								Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
								Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
							Else
								Call Z_DirectExtractionTable("#GL#.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#GL#.IDM", "#Temp1.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
							End If
						End If
					End If
      			    		
       			           			    
					If Criteria.ChkBoxSelChar2 = 1 Then
						'  條件產出
						If FieldArray_Mix(Criteria.DropListSelChar2) <> "Select..." And sSelChar2 <> "" Then
						sTagCount = sTagCount  + 1
						C1 = C1 + 1
						If  sSelChar2 = "JETEAM" Then 
							 	sTemp1 = GetSelectChar(sSelChar2, FieldArray_Mix(Criteria.DropListSelChar2))
							 	sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar2) & "】為空值"
							 Else
								Select Case  sSelType2
									Case 0
										sTemp1 = GetSelectChar(sSelChar2, FieldArray_Mix(Criteria.DropListSelChar2))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar2) & "】值包含 - " & sSelChar2 & " "
									Case 1
										sTemp1 = X_GetCriteriatoSel_Match(sSelChar2,FieldArray_Mix(Criteria.DropListSelChar2))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar2) & "】值為 - " & sSelChar2 & " "
									Case 2
										sTemp1=  "@Len(" & GetSelectChar(sSelChar2, FieldArray_Mix(Criteria.DropListSelChar2)) & ")= 0"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar2) & "】值不包含 - " & sSelChar2 & " "
									Case 3	
										sTemp1= ".NOT. (" & X_GetCriteriatoSel_Match(sSelChar2,FieldArray_Mix(Criteria.DropListSelChar2)) &")"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar2) & "】值不為 - " & sSelChar2 & " "
								End Select						
							End If  
							'sTemp1 = GetSelectChar(sSelChar2, FieldArray_Mix(Criteria.DropListSelChar2))
							'sTagCount = sTagCount  + 1
							'C1 = C1 + 1
							'sLog = sLog & "#" & C1 & ". 文字欄位【"  & FieldArray_Mix(Criteria.DropListSelChar2) & "】值為 - " & sSelChar2 & " "
							SpeCharSelect.TextBoxChar  = "" 
							If Z_File_Exist("#Temp.IDM") = True Then
								Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
								Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
							Else
								Call Z_DirectExtractionTable("#GL#.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#GL#.IDM", "#Temp1.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
							End If
						End If
					End If
					
					If Criteria.ChkBoxSelChar3 = 1 Then
						'  條件產出
						If FieldArray_Mix(Criteria.DropListSelChar3) <> "Select..." And sSelChar3 <> "" Then
							sTagCount = sTagCount  + 1
							C1 = C1 + 1
							If  sSelChar3 = "JETEAM" Then 
							 	sTemp1 = GetSelectChar(sSelChar3, FieldArray_Mix(Criteria.DropListSelChar3))
							 	sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar3) & "】為空值"
							 Else
								Select Case  sSelType3
									Case 0
										sTemp1 = GetSelectChar(sSelChar3, FieldArray_Mix(Criteria.DropListSelChar3))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar3) & "】值包含 - " & sSelChar3 & " "
									Case 1
										sTemp1 = X_GetCriteriatoSel_Match(sSelChar3,FieldArray_Mix(Criteria.DropListSelChar3))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar3) & "】值為 - " & sSelChar3 & " "
									Case 2
										sTemp1=  "@Len(" & GetSelectChar(sSelChar3, FieldArray_Mix(Criteria.DropListSelChar3)) & ")= 0"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar3) & "】值不包含 - " & sSelChar3 & " "
									Case 3	
										sTemp1= ".NOT. (" & X_GetCriteriatoSel_Match(sSelChar3,FieldArray_Mix(Criteria.DropListSelChar3)) &")"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar3) & "】值不為 - " & sSelChar3 & " "
								End Select						
							End If   
							'sTemp1 = GetSelectChar(sSelChar3, FieldArray_Mix(Criteria.DropListSelChar3))
							'sTagCount = sTagCount  + 1
							'C1 = C1 + 1
							'sLog = sLog & "#" & C1 & ". 文字欄位【"  & FieldArray_Mix(Criteria.DropListSelChar3) & "】值為 - " & sSelChar3 & " "
							SpeCharSelect.TextBoxChar  = "" 
							If Z_File_Exist("#Temp.IDM") = True Then
								Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
								Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
							Else
								Call Z_DirectExtractionTable("#GL#.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#GL#.IDM", "#Temp1.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
							End If
						End If
					End If
					
					If Criteria.ChkBoxSelChar4 = 1 Then
						'  條件產出
						If FieldArray_Mix(Criteria.DropListSelChar4) <> "Select..." And sSelChar4 <> "" Then
							sTagCount = sTagCount  + 1
							C1 = C1 + 1
							If  sSelChar4 = "JETEAM" Then 
							 	sTemp1 = GetSelectChar(sSelChar4, FieldArray_Mix(Criteria.DropListSelChar4))
							 	sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar4) & "】為空值"
							 Else
								Select Case  sSelType4
									Case 0
										sTemp1 = GetSelectChar(sSelChar4, FieldArray_Mix(Criteria.DropListSelChar4))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar4) & "】值包含 - " & sSelChar4 & " "
									Case 1
										sTemp1 = X_GetCriteriatoSel_Match(sSelChar4,FieldArray_Mix(Criteria.DropListSelChar4))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar4) & "】值為 - " & sSelChar4 & " "
									Case 2
										sTemp1=  "@Len(" & GetSelectChar(sSelChar4, FieldArray_Mix(Criteria.DropListSelChar4)) & ")= 0"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar4) & "】值不包含 - " & sSelChar4 & " "
									Case 3	
										sTemp1= ".NOT. (" & X_GetCriteriatoSel_Match(sSelChar4,FieldArray_Mix(Criteria.DropListSelChar4)) &")"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar4) & "】值不為 - " & sSelChar4 & " "
								End Select						
							End If   
							'sTemp1 = GetSelectChar(sSelChar4, FieldArray_Mix(Criteria.DropListSelChar4))
							'sTagCount = sTagCount  + 1
							'C1 = C1 + 1
							'sLog = sLog & "#" & C1 & ". 文字欄位【"  & FieldArray_Mix(Criteria.DropListSelChar4) & "】值為 - " & sSelChar4 & " "
							SpeCharSelect.TextBoxChar  = "" 
							If Z_File_Exist("#Temp.IDM") = True Then
								Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
								Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
							Else
								Call Z_DirectExtractionTable("#GL#.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#GL#.IDM", "#Temp1.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
							End If
						End If
					End If
					
					If Criteria.ChkBoxSelChar5 = 1 Then
						'  條件產出
						If FieldArray_Mix(Criteria.DropListSelChar5) <> "Select..." And sSelChar5 <> "" Then 
							sTagCount = sTagCount  + 1
							C1 = C1 + 1
							If  sSelChar5 = "JETEAM" Then 
							 	sTemp1 = GetSelectChar(sSelChar5, FieldArray_Mix(Criteria.DropListSelChar5))
							 	sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar5) & "】為空值"
							 Else
								Select Case  sSelType5
									Case 0
										sTemp1 = GetSelectChar(sSelChar5, FieldArray_Mix(Criteria.DropListSelChar5))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar5) & "】值包含 - " & sSelChar5 & " "
									Case 1
										sTemp1 = X_GetCriteriatoSel_Match(sSelChar5,FieldArray_Mix(Criteria.DropListSelChar5))
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar5) & "】值為 - " & sSelChar5 & " "
									Case 2
										sTemp1=  "@Len(" & GetSelectChar(sSelChar5, FieldArray_Mix(Criteria.DropListSelChar5)) & ")= 0"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar5) & "】值不包含 - " & sSelChar5 & " "
									Case 3	
										sTemp1= ".NOT. (" & X_GetCriteriatoSel_Match(sSelChar5,FieldArray_Mix(Criteria.DropListSelChar5)) &")"
										sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Mix(Criteria.DropListSelChar5) & "】值不為 - " & sSelChar5 & " "
								End Select						
							End If  
							'sTemp1 = GetSelectChar(sSelChar5, FieldArray_Mix(Criteria.DropListSelChar5))
							'sTagCount = sTagCount  + 1
							'C1 = C1 + 1
							'sLog = sLog & "#" & C1 & ". 文字欄位【"  & FieldArray_Mix(Criteria.DropListSelChar5) & "】值為 - " & sSelChar5 & " "
							SpeCharSelect.TextBoxChar  = "" 
							If Z_File_Exist("#Temp.IDM") = True Then
								Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
								Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
							Else
								Call Z_DirectExtractionTable("#GL#.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#GL#.IDM", "#Temp1.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
							End If
						End If
					End If
					
					If Criteria.ChkBoxSelChar6 = 1 Then
						'  條件產出
						If FieldArray_Num(Criteria.DropListSelChar6) <> "Select..." And sSelChar6 <> "" Then
							sTagCount = sTagCount  + 1
							C1 = C1 + 1
							Select Case  sSelType6
								Case 0
									sTemp1 = GetSelectNum(sSelChar6, FieldArray_Num(Criteria.DropListSelChar6))
									sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Num(Criteria.DropListSelChar6) & "】值包含 - " & sSelChar6 & " "
								Case 1
									sTemp1 = X_GetCriteriatoSel_Match_Num(sSelChar6,FieldArray_Num(Criteria.DropListSelChar6))
									sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Num(Criteria.DropListSelChar6) & "】值為 - " & sSelChar6 & " "
								Case 2
									sTemp1=  "@Len(" & GetSelectNum(sSelChar6, FieldArray_Num(Criteria.DropListSelChar6)) & ")= 0"
									sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Num(Criteria.DropListSelChar6) & "】值不包含 - " & sSelChar6 & " "
								Case 3	
									sTemp1= ".NOT. (" & X_GetCriteriatoSel_Match_Num(sSelChar6,FieldArray_Num(Criteria.DropListSelChar6)) &")"
									sLog = sLog & "#" & C1 & ". 文字欄位【" & FieldArray_Num(Criteria.DropListSelChar6) & "】值不為 - " & sSelChar6 & " "
							End Select						  
							'sTemp1 = GetSelectNum(sSelChar6, FieldArray_Num(Criteria.DropListSelChar6))
							'sTagCount = sTagCount  + 1
							'C1 = C1 + 1
							'sLog = sLog & "#" & C1 & ". 數值欄位【"  & FieldArray_Num(Criteria.DropListSelChar6) & "】值為 - " & sSelChar6 & " "
							SpeCharSelect.TextBoxChar  = "" 
							If Z_File_Exist("#Temp.IDM") = True Then
								Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#Temp.IDM", "#Temp1.IDM", "#Temp2.IDM")
								Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
							Else
								Call Z_DirectExtractionTable("#GL#.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase("#GL#.IDM", "#Temp1.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
							End If
						End If
					End If
					
					If Criteria.ChkBoxMakeUpDay = 1 Then
						C1 = C1 + 1
						'sTagCount = sTagCount  + 1
						sLog = sLog & "#" & C1 & ". 需排除總帳日期在補班日/加班日的傳票 "
							If Z_File_Exist("#Temp.IDM") = True Then
								'Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase_NoMatch("#Temp.IDM", "#MakeUpDay#-Make-Up_Day.IDM", "#Temp2.IDM")
								'Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
							Else
								'Call Z_DirectExtractionTable("#GL#In_Period.IDM","#MakeUpDay#-Make-Up_Day.IDM", sTemp1)
								Call Step4_JoinDatabase_NoMatch("#GL#.IDM", "#MakeUpDay#-Make-Up_Day.IDM", "#Temp2.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								sTagCount = sTagCount  + 1
								Call X_AppendField("#Temp.IDM", "Criteria_Tag")
							End If
					End If 

					If Criteria.ChkBoxMakeUpDay1 = 1 Then
						C1 = C1 + 1
						'sTagCount = sTagCount  + 1
						sLog = sLog & "#" & C1 & ". 需排除核准日期在補班日/加班日的總帳 "
							If Z_File_Exist("#Temp.IDM") = True Then
								'Call Z_DirectExtractionTable("#Temp.IDM","#Temp1.IDM", sTemp1)
								Call Step4_JoinDatabase_NoMatch1("#Temp.IDM", "#MakeUpDay#-Make-Up_Day.IDM", "#Temp2.IDM")
								'Call Z_Rename_DB("#Temp2.IDM", "#Temp.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								Call Z_Delete_File("#Temp2.IDM")
							Else
								'Call Z_DirectExtractionTable("#GL#In_Period.IDM","#MakeUpDay#-Make-Up_Day.IDM", sTemp1)
								Call Step4_JoinDatabase_NoMatch1("#GL#.IDM", "#MakeUpDay#-Make-Up_Day.IDM", "#Temp2.IDM")
								Call Z_Delete_File("#Temp1.IDM")
								sTagCount = sTagCount  + 1
								Call X_AppendField("#Temp.IDM", "Criteria_Tag")
							End If
					End If 
					
										
															
      			        	'sLog = "篩選條件： " & sLog
      			        	
					amountTotal =  GetTotal("#Temp.IDM" ,"" ,"DBCount" )	
					
					amountTotal2 = 0				

					If sVer = "TW" And amountTotal <> 0 Then 
										
						Call Step4_Tag_Collection(sTagCount)
						
						Call Step4_TW1	
						
						If GetTotal("#Temp1.IDM" ,"" ,"DBCount" ) <> 0 Then
							Call Step4_TW2
							Call X_RemoveField("#Temp2.IDM", "NO_OF_RECS")
						Else
							sTemp = " FINAL_TAG  ==  ""Y"" "
							Call Z_DirectExtractionTable("#Temp.IDM", "#Temp2.IDM", sTemp)
						End If
						
						Call Z_Rename_DB("#Temp2.IDM","#Temp.IDM")
						Call Z_Delete_File("#Temp1.IDM")
						amountTotal =  GetTotal("#Temp.IDM" ,"" ,"DBCount" )
						If Z_Get_Char_NumBlanks("#Temp.IDM","FINAL_TAG" ) <> -2 Then
							amountTotal2 =  amountTotal - Z_Get_Char_NumBlanks("#Temp.IDM","FINAL_TAG" )
						Else
							amountTotal2 =  amountTotal
						End If
					End If 
					
					
					If amountTotal = 0 Then
						sMsg = "依據你所篩選的條件，傳票檔中未有對應之明細" & Chr(10) & Chr(10) & sLog
						If sVer = "TW" Then GoTo CritialKeepForTW
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION ,"Alert")
						Call Z_Delete_File("#Temp.IDM")
						GoTo ExitBtnRun
					Else
						CritialKeepForTW :
						amountTotal1 =  X_Char_Category("#Temp.IDM" ,"傳票號碼_JE" )
						
						If amountTotal1 >= 0  Then
							sMsg = "本次篩選結果 : 符合篩選條件的傳票數共有 " & amountTotal1 & " 張、明細項共有 " & amountTotal & " 筆(其中共有 " & amountTotal2 & " 筆明細符合所有篩選條件)，是否要保存結果? 如果要保留請按是(Y)，否則請按否(N)"
						ElseIf amountTotal1 = -1 Then 
							sMsg = "本次篩選結果 : 符合篩選條件的傳票數超過 2500 張、明細項共有 " & amountTotal & " 筆(其中共有 " & amountTotal2 & " 筆明細符合所有篩選條件)，是否要保存結果? 如果要保留請按是(Y)，否則請按否(N)"
						Else
							sMsg = "本次篩選結果 : 符合篩選條件的傳票數共有0張、明細項共有 " & amountTotal & " 筆(其中共有 " & amountTotal2 & " 筆明細符合所有篩選條件)，是否要保存結果? 如果要保留請按是(Y)，否則請按否(N)"
						End If 

						Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "進階篩選作業測試結果!")
						If Result = IDYES Then
							sTemp1 = "#CriteriaSelect" & sSEQ_Num & ".IDM"
							Client.CloseDatabase ("#Temp.IDM")
							Set ProjectManagement = client.ProjectManagement
								ProjectManagement.RenameDatabase "#Temp.IDM", sTemp1
							Set ProjectManagement = Nothing
													
							Call X_Update_Criteria_Info("SEQ_Num", sSEQ_Num + 1)
							Call X_Update_Criteria_Info("W" & sSEQ_Num , 1)
							sTemp = amountTotal2
							'Call X_Update_Criteria_Log("W" & sSEQ_Num , sLog , X_Char_Category(sTemp1,"傳票號碼_JE" ) ,GetTotal(sTemp1 ,"" ,"DBCount" ) ,  GetTotal(sTemp1, "傳票金額_JE", "PositiveValue") )
							Call X_Update_Criteria_Log("W" & sSEQ_Num , sLog , X_Char_Category(sTemp1,"傳票號碼_JE" ) ,GetTotal(sTemp1 ,"" ,"DBCount" ) , sTemp)
							If sSEQ_Num =  1 Then  Call X_Update_Step_Info("STEP_5",1)
							Call X_Update_Step_User_Info("STEP_4", UserFullName, "STEP_4_Time", DateValue(Now) & " " & TimeValue(Now))
							Call Step4_Reset_CheckBox
							Else
							Call Z_Delete_File("#Temp.IDM")
							Call Step4_Reset_CheckBox
						End If 
					End If 
				
					S1Check = "1"
					Shell "cmd.exe /c rd /s /q %systemdrive%\$Recycle.bin"
					bExitFunction =True
      			                	
				 	ExitBtnRun:
				 	Call Step4_Button_Enable
      			        
				Case "BtnExpSumReport"
					
					Call Step4_Button_Disable
					
					Call StepN_Excel_File_Check("STEP4") '檢查使用的Excel來源檔案是否存在 & 是否有被開啟 
						
					If S1CHECK <> "1" Then
						Call Step4_Export_Excel
					End If
					
					Call Step4_Button_Enable
					
	              		Case "BtnExit"
              		 	  	bExitFunction =True 
               		 	  	
				End Select
	End Select

	If  bExitFunction Then
		Criteria_Dlg = 0
	Else 
		Criteria_Dlg = 1
	End If
	
End Function

Function Step4_TW1

	If FindField("#Temp.IDM","NO_OF_RECS") = 1 Then Call X_RemoveField("#Temp.IDM","NO_OF_RECS")

	Set db = Client.OpenDatabase("#Temp.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "傳票號碼_JE"
	task.Criteria = " FINAL_TAG  ==  ""Y"" "
	dbName = "#Temp1.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)	

End Function 

Function Step4_TW2

	Set db = Client.OpenDatabase("#Temp.IDM")
	Set task = db.JoinDatabase
	task.FileToJoin "#Temp1.IDM"
	task.IncludeAllPFields
	task.AddSFieldToInc "NO_OF_RECS"
	task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
	task.CreateVirtualDatabase = False
	dbName = "#Temp2.IDM"
	task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)

End Function 

Function AddDateArray(SelDate As String)
	
	Select Case selDate
		Case "Date1"
			For j = 0 To 10
				If sListArry1(j) <> ""  Then 
					TestArray(j)  =  sListArry1(j)
				End If
			Next J
		Case "Date2"
			For j = 0 To 10
				If sListArry2(j) <> ""  Then 
					TestArray(j)  =  sListArry2(j)
				End If
			Next J
	End Select
	
End Function



Function SpeDateSelect_Dlg(ControlID$, Action%, SuppValue%)
	
	bExitFunction  = FALSE
	Dim dlg As SpeDateSelect
	Dim aux1(10) As String
	Dim aux As String
	Dim j As Integer
	Dim tempListSelect1 As Integer 
 
	Select Case Action%
		Case 1
			ReDim tempListbox1(0)
			DlgVisible "BtnRemove", 0
			sDateSelect = ""
		Case 2
		
			Select Case ControlID
			
				Case "BtnAdd"
						DlgListBoxArray "ListBox_DateSel", TestArray()
						Button = Dialog (DatePicker)
						If sDate = "" Then
							GoTo b
						Else
							For j = 0 To 10
								If TestArray(j) = ""  Then 			
									TestArray(j) = sDate
									GoTo b
								End If
							Next j 
							GoTo b
						End If 
				
				Case "BtnRemove"
				C:
						If dlg.ListBox_DateSel=-1 Then
							GoTo b
						Else		                 
							'aux = TestArray(dlg.ListBox_DateSel) 
							aux = TestArray(tempListSelect1)
							'TestArray(dlg.ListBox_DateSel)  = ""
							TestArray(tempListSelect1)  = ""

							k=0
							For j=0 To 10
                       		      If TestArray(j)<>"" Then
                        		          aux1(k)=TestArray(j)
                        		          TestArray(j)=""
                        		          k=k+1
                        	      End If
							Next j

							k=0
							For j=0 To 10
                        	   If aux1(j)<>"" Then
                        		      TestArray$(k)=aux1(j)
                        		      aux1(j)=""
                        		      k=k+1
                               End If
                   		  	Next j
						    GoTo b
               			End If 
               						
				Case "BtnRemoveAll"
						k=0
						For j=0 To 10
                        	If TestArray(j)<>"" Then
                        		TestArray$(j)= ""
                        	End If
                      	Next j
						GoTo b
			 
				Case "BtnSave"	
						
					If SelDate_check = 1 Then	
						For j = 0 To 10
							If TestArray(j) <> ""  Then 
								sDateSelect = sDateSelect & ", " & Chr(34) &   iRemove( TestArray(j)  , "/")  & Chr(34)
								sListArry1(j)  =  TestArray(j)
							End If
						Next j
						sSelDate1 = sDateSelect
					ElseIf SelDate_check = 2 Then
						For j = 0 To 10
							If TestArray(j) <> ""  Then 
								sDateSelect = sDateSelect & ", " & Chr(34) &   iRemove( TestArray(j)  , "/")  & Chr(34)
								sListArry2(j)  =  TestArray(j)
							End If
						Next 
						sSelDate2 = sDateSelect
					End If
					bExitFunction = True
		
				Case "BtnExit"
					bExitFunction = True
					
				Case "ListBox_DateSel"
					tempListSelect1 = SuppValue%
					GoTo C
											
			End Select
	End Select

b:		
DlgListBoxArray "ListBox_DateSel", TestArray()

	If  bExitFunction Then
		SpeDateSelect_Dlg = 0
	Else 
		SpeDateSelect_Dlg = 1
	End If	
	
End Function


Function X_Date_Select_Function(sTempDate As String)

Dim dlg As SpeDateSelect
Dim aux1(10) As String
Dim aux As String 

b:
	res=Dialog(dlg)

	If res = 1 Then  ' 按鈕一 Add1
		Button = Dialog (DatePicker)
		If sDate = "" Then
			GoTo b
		Else
			For j = 0 To 10
					If TestArray(j) = ""  Then 			
						TestArray(j) = sDate
					Exit For 
					End If
			Next j 
			
		GoTo b
		End If 
	End If
	
	
	
	If res = 2 Then ' 按鈕二 Remove1
		If dlg.ListBox_DateSel=-1 Then
			GoTo b
		Else		                 
			aux = TestArray(dlg.ListBox_DateSel) 
			TestArray(dlg.ListBox_DateSel)  = ""
			k=0
			For j=0 To 10
                             If TestArray(j)<>"" Then
                                aux1(k)=TestArray(j)
                                TestArray(j)=""
                                k=k+1
                             End If
			Next j

			k=0
			For j=0 To 10
                             If aux1(j)<>"" Then
                                TestArray$(k)=aux1(j)
                                aux1(j)=""
                                k=k+1
                             End If
                      Next j
		GoTo b
               End If 
	End If 
	
	If res = 3 Then ' 按鈕三  Remove all
		k=0
		For j=0 To 10
                             If TestArray(j)<>"" Then
                                TestArray$(j)= ""
                             End If
                      Next j
		GoTo b
	End If 
                      	
	If res = 4 Then  ' 按鈕四 Save
		sDateSelect = ""	
		For j = 0 To 10 
			If TestArray(j) <> ""  Then 
				sDateSelect = sDateSelect & ", " & Chr(34) &   iRemove( TestArray(j)  , "/")  & Chr(34)
			End If
		Next J
		
		
		If sTempDate = "Date1" Then 	
			sSelDate1 = sDateSelect
			For j = 0 To 10 
				If TestArray(j) <> ""  Then 
					sListArry1(j) =  TestArray(j) 
				End If
			Next J
			
		End If
		
		If sTempDate = "Date2" Then sSelDate2 = sDateSelect
		'MsgBox  sDateSelect
		
	End If 	
					
End Function



Function SpeCharSelect_Dlg(ControlID$, Action%, SuppValue%)
	
iSelType = SpeCharSelect.OptButtonGroupTxt1
	
	Select Case Action%
		Case 1
			
		Case 2
			Select Case ControlID
				Case "BtnSave" 
				'Select Case  iSelType	
				'		Case 0
							
				'		Case 1
							
				'		Case 2
						
				'		Case 3			
				'End Select
					
				Case "BtnCancel"
					
			End Select
	End Select
End Function

Function X_Char_Select_Function(sTempChar As String)

	If sTempChar = "Char1" Then sSelChar1 = SpeCharSelect.TextBoxChar
	If sTempChar = "Char2" Then sSelChar2 = SpeCharSelect.TextBoxChar
	If sTempChar = "Char3" Then sSelChar3 = SpeCharSelect.TextBoxChar
	If sTempChar = "Char4" Then sSelChar4 = SpeCharSelect.TextBoxChar
	If sTempChar = "Char5" Then sSelChar5 = SpeCharSelect.TextBoxChar
	If sTempChar = "Char6" Then sSelChar6 = SpeCharSelect.TextBoxChar
	If sTempChar = "Rationale1" Then sRationale1 = RationaleForWP.RationaleText
	If sTempChar = "Rationale2" Then sRationale2 = RationaleForWP.RationaleText
	If sTempChar = "Rationale3" Then sRationale3 = RationaleForWP.RationaleText
	If sTempChar = "Rationale4" Then sRationale4 = RationaleForWP.RationaleText
	If sTempChar = "Rationale5" Then sRationale5 = RationaleForWP.RationaleText
	If sTempChar = "Rationale6" Then sRationale6 = RationaleForWP.RationaleText
	If sTempChar = "Rationale7" Then sRationale7 = RationaleForWP.RationaleText
	If sTempChar = "Rationale8" Then sRationale8 = RationaleForWP.RationaleText
	If sTempChar = "Rationale9" Then sRationale9 = RationaleForWP.RationaleText
	If sTempChar = "Rationale10" Then sRationale10 = RationaleForWP.RationaleText
	
End Function


Function ExpWorkPaper_Dlg(ControlID$, Action%, SuppValue%)
	Dim bExitFunction As Boolean
	Dim j As Integer
	Dim L As Integer
	Dim sDes As String
	
	Select Case Action%
	
		Case 1
			
			Call X_Get_Criteria_Info
						
			For L = 1 To 10 
				If L = 1 Then sDes = " 一 " 
				If L = 2 Then sDes = " 二 " 
				If L = 3 Then sDes = " 三 " 
				If L = 4 Then sDes = " 四 " 
				If L = 5 Then sDes = " 五 " 
				If L = 6 Then sDes = " 六 " 
				If L = 7 Then sDes = " 七 " 
				If L = 8 Then sDes = " 八 " 
				If L = 9 Then sDes = " 九 " 
				If L = 10 Then sDes = " 十 " 
				
				If Z_File_Exist("#CriteriaSelect" & L & ".IDM") = True Then 
					amountTotal1 = X_Char_Category("#CriteriaSelect" & L & ".IDM" ,"傳票號碼_JE" )
					If amountTotal1 = -1 Then
						DlgText "ChkBoxCriteria" & L,"篩選條件" & sDes & "【傳票數超過2,500張】"
					ElseIf amountTotal1 = -2 Then 
						DlgText "ChkBoxCriteria" & L,"篩選條件" & sDes & "【0 張傳票數】"
					Else
						DlgText "ChkBoxCriteria" & L,"篩選條件" & sDes & "【" & amountTotal1 & " 張傳票數】"
					End If
				End If

			Next L
						
			DlgEnable "ChkBoxCriteria1" , sW1
			DlgEnable "ChkBoxCriteria2" , sW2
			DlgEnable "ChkBoxCriteria3" , sW3
			DlgEnable "ChkBoxCriteria4" , sW4
			DlgEnable "ChkBoxCriteria5" , sW5
			DlgEnable "ChkBoxCriteria6" , sW6
			DlgEnable "ChkBoxCriteria7" , sW7
			DlgEnable "ChkBoxCriteria8" , sW8
			DlgEnable "ChkBoxCriteria9" , sW9
			DlgEnable "ChkBoxCriteria10" , sW10
			DlgEnable "PushButton1" , sW1
			DlgEnable "PushButton2" , sW2
			DlgEnable "PushButton3" , sW3
			DlgEnable "PushButton4" , sW4
			DlgEnable "PushButton5" , sW5
			DlgEnable "PushButton6" , sW6
			DlgEnable "PushButton7" , sW7
			DlgEnable "PushButton8" , sW8
			DlgEnable "PushButton9" , sW9
			DlgEnable "PushButton10" , sW10
			
			sRationale1 = ""
			sRationale2 = ""
			sRationale3 = ""
			sRationale4 = ""
			sRationale5 = ""
			sRationale6 = ""
			sRationale7 = ""
			sRationale8 = ""
			sRationale9 = ""
			sRationale10 = ""
								
		Case 2 	
			Select Case ControlID$
				Case "BtnExpWorkPaper"
					
					DlgEnable "BtnExpWorkPaper", 0
					DlgEnable "CancelButton", 0
					DlgEnable "BtnStep4Result", 0

					Call StepN_Excel_File_Check("STEP5")
					
					If S1Check <> "1" Then 
					
						For i = 1 To 10 
							WPselect(i) = 0
						Next i
				
						If ExpWorkPaper.ChkBoxCriteria1  = 1 Then WPselect(1) = 1
						If ExpWorkPaper.ChkBoxCriteria2  = 1 Then WPselect(2) = 1
						If ExpWorkPaper.ChkBoxCriteria3  = 1 Then WPselect(3) = 1
						If ExpWorkPaper.ChkBoxCriteria4  = 1 Then WPselect(4) = 1
						If ExpWorkPaper.ChkBoxCriteria5  = 1 Then WPselect(5) = 1
						If ExpWorkPaper.ChkBoxCriteria6  = 1 Then WPselect(6) = 1
						If ExpWorkPaper.ChkBoxCriteria7  = 1 Then WPselect(7) = 1
						If ExpWorkPaper.ChkBoxCriteria8  = 1 Then WPselect(8) = 1
						If ExpWorkPaper.ChkBoxCriteria9  = 1 Then WPselect(9) = 1
						If ExpWorkPaper.ChkBoxCriteria10  = 1 Then WPselect(10) = 1
					
						p=0
						For i =1 To 10 
							p = p + WPselect(i)
						Next i 	

						If p = 0 Then 
							sMsg = "請至少挑選一個篩選後的結果 !"
							Result = MsgBox(sMsg, MB_OK Or MB_ICONINFORMATION , "步驟五 未挑選任一測試結果 警示訊息")
							bExitFunction = False
						Else
							S1Check = 0
							Call Step5_rationale_Check
							If S1Check = 0 Then 
								Call Z_Delete_File("#TEMP.IDM")
								Call Step5_WPdata_Collection
								Call Step5_Export_Excel_TW
								bExitFunction = True
							End If
						End If
					End If 

					DlgEnable "BtnExpWorkPaper", 1
					DlgEnable "CancelButton", 1
					DlgEnable "BtnStep4Result", 1

				Case "BtnStep4Result"
					Button = Dialog (CriteriaLogSum)
										
				Case "PushButton1"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale1
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale1")
					Call Step5_Button_Enable
					If sRationale1 <> "" Then 
						DlgValue "ChkBoxCriteria1", 1
					Else
						DlgValue "ChkBoxCriteria1", 0
					End If
												
				Case "PushButton2"					
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale2
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale2")
					Call Step5_Button_Enable
					If sRationale2 <> "" Then 
						DlgValue "ChkBoxCriteria2", 1
					Else
						DlgValue "ChkBoxCriteria2", 0
					End If
					
				Case "PushButton3"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale3
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale3")
					Call Step5_Button_Enable
					If sRationale3 <> "" Then 
						DlgValue "ChkBoxCriteria3", 1
					Else
						DlgValue "ChkBoxCriteria3", 0
					End If

				Case "PushButton4"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale4
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale4")
					Call Step5_Button_Enable
					If sRationale4 <> "" Then 
						DlgValue "ChkBoxCriteria4", 1
					Else
						DlgValue "ChkBoxCriteria4", 0
					End If

				Case "PushButton5"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale5
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale5")
					Call Step5_Button_Enable
					If sRationale5 <> "" Then 
						DlgValue "ChkBoxCriteria5", 1
					Else
						DlgValue "ChkBoxCriteria5", 0
					End If

				Case "PushButton6"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale6
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale6")
					Call Step5_Button_Enable
					If sRationale6 <> "" Then 
						DlgValue "ChkBoxCriteria6", 1
					Else
						DlgValue "ChkBoxCriteria6", 0
					End If

				Case "PushButton7"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale7
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale7")
					Call Step5_Button_Enable
					If sRationale7 <> "" Then 
						DlgValue "ChkBoxCriteria7", 1
					Else
						DlgValue "ChkBoxCriteria7", 0
					End If

				Case "PushButton8"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale8
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale8")
					Call Step5_Button_Enable
					If sRationale8 <> "" Then 
						DlgValue "ChkBoxCriteria8", 1
					Else
						DlgValue "ChkBoxCriteria8", 0
					End If

				Case "PushButton9"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale9
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale9")
					Call Step5_Button_Enable
					If sRationale9 <> "" Then 
						DlgValue "ChkBoxCriteria9", 1
					Else
						DlgValue "ChkBoxCriteria9", 0
					End If

				Case "PushButton10"
					Call Step5_Button_Disable 
					RationaleForWP.RationaleText = sRationale10
					Button = Dialog (RationaleForWP)
					Call X_Char_Select_Function("Rationale10")
					Call Step5_Button_Enable
					If sRationale10 <> "" Then 
						DlgValue "ChkBoxCriteria10", 1
					Else
						DlgValue "ChkBoxCriteria10", 0
					End If

				Case "CancelButton"
					bExitFunction = True
										
			End Select
	End Select
	
	If  bExitFunction Then
		ExpWorkPaper_Dlg = 0
	Else 
		ExpWorkPaper_Dlg = 1
	End If 
	
	
End Function

Function Step5_Button_Disable 

	DlgEnable "BtnExpWorkPaper" , 0
	DlgEnable "CancelButton" , 0
	DlgEnable "PushButton1" , 0
	DlgEnable "PushButton2" , 0
	DlgEnable "PushButton3" , 0
	DlgEnable "PushButton4" , 0
	DlgEnable "PushButton5" , 0
	DlgEnable "PushButton6" , 0
	DlgEnable "PushButton7" , 0
	DlgEnable "PushButton8" , 0
	DlgEnable "PushButton9" , 0
	DlgEnable "PushButton10" , 0

End Function

Function Step5_Button_Enable

	DlgEnable "BtnExpWorkPaper" , 1
	DlgEnable "CancelButton" , 1
	DlgEnable "PushButton1" , sW1
	DlgEnable "PushButton2" , sW2
	DlgEnable "PushButton3" , sW3
	DlgEnable "PushButton4" , sW4
	DlgEnable "PushButton5" , sW5
	DlgEnable "PushButton6" , sW6
	DlgEnable "PushButton7" , sW7
	DlgEnable "PushButton8" , sW8
	DlgEnable "PushButton9" , sW9
	DlgEnable "PushButton10" , sW10

End Function



'--------DatePick ↓-------
Function funCalendar(ControlID$, Action%, SuppValue%)

	Dim iWeekDay As Integer 'holds the start day of the month
	Dim i, j As Integer
	Dim iNoOfDays As Integer 'numer of days in the month
	Dim bExitMenu As Boolean
	Dim bCurrentDate As Boolean 'flag to indicate if the script should use the current date
	Dim bUpdateYearList As Boolean 'flag to indicate if the year drop down should be updated as the year has changed
	
	bExitMenu  = FALSE
	bCurrentDate = FALSE
	
	Select Case action%
		'1 indicates that this is the first time the function is called
		Case 1 'check to see if there is a valid date and if so use it, if not use the current date
			
			If IsDate(sDate) Then
				iYear = Year(sDate)
				iMonth = Month(sDate)
				bUpdateYearList = TRUE
			Else
				bCurrentDate = TRUE
			End If
			
			
		Case 2
			Select Case ControlID$
				Case "BtnCancel"
					bExitMenu = TRUE   'funCalendar = 0 
				Case "PBPrevious"
					'if the user clicks the previous arrow - < - then remove a month
					iYear = Year(DateSerial(iYear, iMonth  - 1, 1)) 
					iMonth = Month(DateSerial(iYear, iMonth - 1, 1))
				Case "PBNext"
					'if the user clicks the next arrow - > - then add a month
					iYear = Year(DateSerial(iYear, iMonth  + 1, 1))
					iMonth = Month(DateSerial(iYear, iMonth + 1, 1))
				Case "lstYear"
					'if a year is selected from the drop down update the variables for the selected year and update the year dropdown
					iYear = Year(DateSerial(sYearArray(SuppValue%), iMonth, 1))
					iMonth = Month(DateSerial(sYearArray(SuppValue%), iMonth, 1))
					'update the year based on the new year
					bUpdateYearList = TRUE
				Case "BtnClear"
					sDate = ""
					bExitMenu = TRUE   'funCalendar = 0 
				Case Else
					'if any of the date buttons are selected
					'if the button is blank do nothing
					If sDayArray(SuppValue% - 3) = "" Then 
						bExitMenu = FALSE
					Else
						'if a date is selected create the date and give it to the sDate variable which is a global variable and available from the main menu dialog
						sDate = iYear & "/"
						If iMonth < 10 Then
							sDate = sDate & "0" & iMonth & "/"
						Else
							sDate = sDate & iMonth & "/"
						End If
						If Len(sDayArray(SuppValue% - 3) ) = 1Then
							sDate = sDate & "0" & sDayArray(SuppValue% - 3)
						Else
							sDate = sDate & sDayArray(SuppValue% - 3)
						End If
						bExitMenu = TRUE
					End If
			End Select

			
	End Select
	
	If bExitMenu  Then
		funCalendar = 0
	Else
		funCalendar = 1
	End If
	
	'if the current date is selected then get the current year and month
	If bCurrentDate Then
		iYear = Year(Now())
		iMonth = Month(Now())
		bUpdateYearList = TRUE
	End If
	
	'update the year drop down based on the iYear variable, 5 years back and 5 years forward.
	If bUpdateYearList Then
		'populate the year event
		j = 0
		sYearArray(j) = "Year"
		j = j + 1
		For i = iYear - 5 To iYear 
			sYearArray(j)  = i
			j = j + 1
		Next i
		For i = iYear + 1 To iYear + 5 
			sYearArray(j)  = i
			j = j + 1
		Next i
		DlgListBoxArray "lstYear", sYearArray

	End If
	
	'clear the array
	For i = 0 To 41
		sDayArray(i) = "" 
	Next i
	
	'obtain the current week day 1 is Sunday 7 is Saturday
	iWeekDay = getWeekeday(iMonth, iYear)
	'obtain the number of days in the month
	iNoOfDays = dhDaysInMonth(sDateDefault )
	'populate the day array
	For i = 0 To iWeekDay - 1
		sDayArray(i) = "" 
	Next i
	For j = 1 To iNoOfDays
		sDayArray(iWeekDay -2 + j) = j
		i = i + 1
	Next j
	'put the year and month in the heading
	Call populateMonth()
	DlgText "txtYearMonth", sMonthArray(iMonth) & " - " & iYear
	
	'change the captions for the day buttons	
	For i = 1 To 42
		DlgText "PB" & i, sDayArray(i - 1) 
	Next i
	
	'DlgText "Text2", "Year: " & iYear & " Month: " & iMonth & " Week day: " & iWeekDay 'just for debug
End Function

Function getWeekeday(iMonth As Integer, iYear As Integer) As Integer

	Dim sDatePos(2) As String
	
	sDefaultDateFormat = ReadLocaleInfo(LOCALE_SSHORTDATE )
	sDefaultDateSeperator = ReadLocaleInfo(LOCALE_SDATE )
	sDatePos(0) = Mid(iSplit(UCase(sDefaultDateFormat), "", sDefaultDateSeperator, 1), 1, 1)
	sDatePos(1) = Mid(iSplit(UCase(sDefaultDateFormat), "", sDefaultDateSeperator, 2), 1, 1)
	sDatePos(2) =  Mid(iSplit(UCase(sDefaultDateFormat), "", sDefaultDateSeperator, 3), 1, 1)
	
	If sDatePos(0) = "M" Then
		sDateDefault  = iMonth & sDefaultDateSeperator 
	ElseIf sDatePos(0) = "D" Then
		sDateDefault  = "01" & sDefaultDateSeperator 
	Else
	 	sDateDefault  = iYear & sDefaultDateSeperator 
	End If
	
	If sDatePos(1) = "M" Then
		sDateDefault  = sDateDefault & iMonth & sDefaultDateSeperator 
	ElseIf sDatePos(1) = "D" Then
		sDateDefault  = sDateDefault & "01" & sDefaultDateSeperator 
	Else
	 	sDateDefault  = sDateDefault  & iYear & sDefaultDateSeperator 
	End If
	
	If sDatePos(2) = "M" Then
		sDateDefault  = sDateDefault  & iMonth
	ElseIf sDatePos(2) = "D" Then
		sDateDefault  = sDateDefault  & "01" 
	Else
	 	sDateDefault  = sDateDefault  & iYear
	End If

	getWeekeday = Weekday(sDateDefault)

End Function

'function that will obtain the number of days in a month.
Function dhDaysInMonth(dtmDate As String) As Integer 'if set to 0 use current date
	' Return the number of days in the specified month.
	If dtmDate = "0" Then
		dtmDate = Now()
	End If
	dhDaysInMonth = DateDiff("d", DateSerial(Year(dtmDate), Month(dtmDate) , 1), DateSerial(Year(dtmDate), Month(dtmDate) + 1, 1))
End Function

'function to populate the month array
Function populateMonth()
	sMonthArray(1) = "January" 
	sMonthArray(2) = "February" 
	sMonthArray(3) = "March" 
	sMonthArray(4) = "April" 
	sMonthArray(5) = "May" 
	sMonthArray(6) = "June" 
	sMonthArray(7) = "July" 
	sMonthArray(8) = "August" 
	sMonthArray(9) = "September" 
	sMonthArray(10) = "October" 
	sMonthArray(11) = "November" 
	sMonthArray(12) = "December" 
End Function

Public Function ReadLocaleInfo(ByVal lInfo As Long) As String
    
	    Dim sBuffer As String
	    Dim rv As Long
	    
	    sBuffer = String$(256, 0)
	    rv = GetLocaleInfo(LOCALE_USER_DEFAULT, lInfo, sBuffer, Len(sBuffer))
	    
	    If rv > 0 Then
	        ReadLocaleInfo = Left$(sBuffer, rv - 1)
	    Else
	        ReadLocaleInfo = ""
	    End If
End Function

'--------DatePick ↑-------


'-------TB File、GL File ↓--------

Function TBFile_Array
	'必要欄位TB
	DlgListboxArray "DropListAccNumTB", TBFieldArray_mix()
	DlgListboxArray "DropListAccDes", TBFieldArray_char()
	'DlgListboxArray "DropListBegBal" , TBFieldArray_num()
	'DlgListboxArray "DropListEndBal" , TBFieldArray_num()
End Function

Function GLFile_Arry
	'必要欄位GL
	DlgListboxArray "DropListDocNum", GLFieldArray_mix()
	DlgListboxArray "DropListDes", GLFieldArray_mix()
	DlgListboxArray "DropListPosDate" , GLFieldArray_date()
	DlgListboxArray "DropListAccNumGL" , GLFieldArray_mix()
	DlgListboxArray "DropListAccName" , GLFieldArray_mix()
	DlgListboxArray "DropListAmount" , GLFieldArray_num()
	
	'非必要欄位GL
	DlgListboxArray "DropListCreateBy", GLFieldArray_mix()
	DlgListboxArray "DropListApprovBy", GLFieldArray_mix()
	DlgListboxArray "DropListDocDate" , GLFieldArray_date()
	DlgListboxArray "DropListLineID" , GLFieldArray_Num()
	DlgListboxArray "DropListManual" , GLFieldArray_Num()
	DlgListboxArray "DropListJESource" , GLFieldArray_mix()
	
	'使用者自訂欄位
	DlgListboxArray "DropListUserDefind1" , GLFieldArray_mix()
	DlgListboxArray "DropListUserDefind2" , GLFieldArray_mix()
	DlgListboxArray "DropListUserDefind3" , GLFieldArray_mix()
	DlgListboxArray "DropListUserDefind4" , GLFieldArray_date()
	
End Function 

Function TB_DoubleCheck
	Dim FieldCheckArray() As String
	'TB_DoubleCheck = 0
	ReDim FieldCheckArray(10)
	
	'需確保畫面上的DropListBox皆有設定到
	FieldCheckArray(0) = TBFieldArray_mix$(TBDetails.DropListAccNumTB) 
	FieldCheckArray(1) = TBFieldArray_char$(TBDetails.DropListAccDes)
	'FieldCheckArray(2) = FieldArray_num$(TBDetails.DropListBegBal)
	'FieldCheckArray(3) = FieldArray_num$(TBDetails.DropListEndBal)

	'LBound, UBound
	For i = 0 To 2'4
		If FieldCheckArray(i) <> "" Or FieldCheckArray(i) <> "Select..." Then
			For j = 0 To 2'4
				If FieldCheckArray(j) <> "" Or FieldCheckArray(j) <> "Select..."  Then
					If i <> j Then
						If FieldCheckArray(i) = FieldCheckArray(j) Then
							sMsg = "原始檔案中欄位 [" & FieldCheckArray(i)  & "] 重複選擇，請確認!"
							Result = MsgBox( sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一 欄位配對設定 警示訊息")
							Exit Function
						End If
					End If
				End If
			Next j
		End If
	Next i

	TB_DoubleCheck = 1
End Function

Function GL_DoubleCheck
	Dim FieldCheckArray() As String
	Dim sCheckError As String
	ReDim FieldCheckArray(16)
	
	'Derek 20180820 Moify
	sCheckError = ""

	'需確保畫面上的DropListBox皆有設定到
	FieldCheckArray(0) = GLFieldArray_mix$(GLDetails.DropListDocNum) 
	FieldCheckArray(1) = GLFieldArray_mix$(GLDetails.DropListDes)
	FieldCheckArray(2) = GLFieldArray_date$(GLDetails.DropListPosDate)
	FieldCheckArray(3) = GLFieldArray_mix$(GLDetails.DropListAccNumGL)
	FieldCheckArray(4) = GLFieldArray_date$(GLDetails.DropListDocDate)
	FieldCheckArray(5) = GLFieldArray_mix$(GLDetails.DropListCreateBy)
	FieldCheckArray(6) = GLFieldArray_mix$(GLDetails.DropListApprovBy)
	FieldCheckArray(7) = GLFieldArray_mix$(GLDetails.DropListAccName)
	FieldCheckArray(8) = GLFieldArray_num$(GLDetails.DropListLineID)
	FieldCheckArray(9) = GLFieldArray_Num$(GLDetails.DropListManual)
	FieldCheckArray(10) = GLFieldArray_mix$(GLDetails.DropListJESource)
	FieldCheckArray(11) = GLFieldArray_mix$(GLDetails.DropListUserDefind1)
	FieldCheckArray(12) = GLFieldArray_mix$(GLDetails.DropListUserDefind2)
	FieldCheckArray(13) = GLFieldArray_mix$(GLDetails.DropListUserDefind3)
	FieldCheckArray(14) = GLFieldArray_date$(GLDetails.DropListUserDefind4)
	
	'LBound, UBound
	For i = 0 To 14
		If FieldCheckArray(i) <> "" And FieldCheckArray(i) <> "Select..." Then
			For j = 0 To 14
				If FieldCheckArray(j) <> "" And FieldCheckArray(j) <> "Select..."  Then
					If i <> j Then
						If FieldCheckArray(i) = FieldCheckArray(j) And i > j Then
							sCheckError = sCheckError &  FieldCheckArray(i)  & "、" 							
						End If
					End If
				End If
			Next j
		End If
	Next i
	
	If sCheckError <>"" Then 
		sCheckError = iLeft(sCheckError, iLen(sCheckError)-1)
		sMsg = "原始檔案中欄位 [" & sCheckError & "] 重複選擇，請確認!"
		Result = MsgBox( sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一 欄位配對設定 警示訊息")
		Exit Function
	End If
	
	GL_DoubleCheck = 1
End Function

'挑選檔案
Function GetTBFile
	Dim obj As Object 
	Set obj = Client.CommonDialogs
	sTBfilename = obj.FileExplorer()
	Set obj = Nothing
End Function

Function GetGLFile
	Dim obj As Object 
	Set obj = Client.CommonDialogs
	sGLfilename = obj.FileExplorer()
	Set obj = Nothing
End Function

'將選擇之檔案欄位名稱依屬性放到不同的Array
Function GetFieldName(TFileName As String)
	Dim SourceFile As Datebase
	Dim thisTableFile As TableDef
	Dim field_count As Integer
	Dim i, j, k, L As Integer
	Dim ThisField, sfield As Field
	Dim iFieldType As String

	  
	Set SourceFile = client.OpenDataBase(TFileName)
	Set thisTableFile = SourceFile.tabledef	  '這是個可以針對所指定的資料表操作的物件
	field_count = thisTableFile.count

	'Dim FieldArray_num(field_count)
	ReDim FieldArray_mix(field_count)
	ReDim FieldArray_date(field_count)
	'Dim FieldArray_Char(field_count)
	
	i = 1
	j = 1
	k = 1
	L = 1
	n = 1
	FieldArray_num(0) = "Select..."	' 數字型態
	FieldArray_mix(0) = "Select..."	' 所有欄位
	FieldArray_date(0) = "Select..."	'日期型態
	'FieldArray_Char(0) = "Select..."	'文字型態
	
						   	
	For i = 1 To field_count

		Set ThisField = thisTableFile.GetFieldAt(i)
						   		
	 		Set sfield = thisTableFile.getfield(ThisField.Name)
			   		iFieldType = sfield.Type
			   		If ThisField.IsNumeric = True Then
			   		'	FieldArray_num(j) = ThisField.Name
			   			j = j + 1
			   		ElseIf  iFieldType = WI_DATE_FIELD Or iFieldType = WI_VIRT_DATE Then
			   			FieldArray_date(l) = ThisField.Name
			   			l = l + 1
			   		End If

			   		If iFieldType = WI_CHAR_FIELD Or iFieldType = WI_VIRT_CHAR Or iFieldType = WI_VIRT_NUM Or iFieldType = WI_NUM_FIELD Then
			   			FieldArray_mix(k) = ThisField.Name
			   			k = k + 1
				   	End If 

				   	'If iFieldType = WI_CHAR_FIELD Or iFieldType = WI_VIRT_CHAR  Then
			   		'	FieldArray_Char(n) = ThisField.Name
			   		'	n = n + 1
				   	'End If 
	Next i
			Set sfield = Nothing
		Set ThisField = Nothing
	Set thisTableFile = Nothing 
	Set SourceFile = Nothing
End Function

Function TBGetFieldName(TFileName As String)
	Dim SourceFile As Datebase
	Dim thisTableFile As TableDef
	Dim field_count As Integer
	Dim i, j, k, L As Integer
	Dim ThisField, sfield As Field
	Dim iFieldType As String

	  
	Set SourceFile = client.OpenDataBase(TFileName)
	Set thisTableFile = SourceFile.tabledef	  '這是個可以針對所指定的資料表操作的物件
	field_count = thisTableFile.count
	
	ReDim TBFieldArray_num(field_count)
	ReDim TBFieldArray_mix(field_count)
	'Dim TBFieldArray_date(field_count)
	ReDim TBFieldArray_Char(field_count)
	


	i = 1
	j = 1
	k = 1
	L = 1
	n = 1
	TBFieldArray_num(0) = "Select..."	' 數字型態
	TBFieldArray_mix(0) = "Select..."	' 所有欄位
	'TBFieldArray_date(0) = "Select..."	'日期型態
	TBFieldArray_Char(0) = "Select..."	'文字型態
	
						   	
	For i = 1 To field_count

		Set ThisField = thisTableFile.GetFieldAt(i)
						   		
	 		Set sfield = thisTableFile.getfield(ThisField.Name)
			   		iFieldType = sfield.Type
			   		If ThisField.IsNumeric = True Then
			   			TBFieldArray_num(j) = ThisField.Name
			   			j = j + 1
			   		'ElseIf  iFieldType = WI_DATE_FIELD Or iFieldType = WI_VIRT_DATE Then
			   			'TBFieldArray_date(l) = ThisField.Name
			   			'l = l + 1
			   		End If

			   		If iFieldType = WI_CHAR_FIELD Or iFieldType = WI_VIRT_CHAR Or iFieldType = WI_VIRT_NUM Or iFieldType = WI_NUM_FIELD Then
			   			TBFieldArray_mix(k) = ThisField.Name
			   			k = k + 1
				   	End If 

				   	If iFieldType = WI_CHAR_FIELD Or iFieldType = WI_VIRT_CHAR  Then
			   			TBFieldArray_Char(n) = ThisField.Name
			   			n = n + 1
				   	End If 
	Next i
			Set sfield = Nothing
		Set ThisField = Nothing
	Set thisTableFile = Nothing 
	Set SourceFile = Nothing

End Function

Function GLGetFieldName(TFileName As String)
	Dim SourceFile As Datebase
	Dim thisTableFile As TableDef
	Dim field_count As Integer
	Dim i, j, k, L As Integer
	Dim ThisField, sfield As Field
	Dim iFieldType As String

	  
	Set SourceFile = client.OpenDataBase(TFileName)
	Set thisTableFile = SourceFile.tabledef	  '這是個可以針對所指定的資料表操作的物件
	field_count = thisTableFile.count

	ReDim  GLFieldArray_num(field_count)
	ReDim  GLFieldArray_mix(field_count)
	ReDim  GLFieldArray_date(field_count)
	ReDim  GLFieldArray_Char(field_count)
	
	i = 1
	j = 1
	k = 1
	L = 1
	n = 1
	GLFieldArray_num(0) = "Select..."	' 數字型態
	GLFieldArray_mix(0) = "Select..."	' 所有欄位
	GLFieldArray_date(0) = "Select..."	'日期型態
	GLFieldArray_Char(0) = "Select..."	'文字型態
	
						   	
	For i = 1 To field_count

		Set ThisField = thisTableFile.GetFieldAt(i)
						   		
	 		Set sfield = thisTableFile.getfield(ThisField.Name)
			   		iFieldType = sfield.Type
			   		If ThisField.IsNumeric = True Then
			   			GLFieldArray_num(j) = ThisField.Name
			   			j = j + 1
			   		ElseIf  iFieldType = WI_DATE_FIELD Or iFieldType = WI_VIRT_DATE Then
			   			GLFieldArray_date(l) = ThisField.Name
			   			l = l + 1
			   		End If

			   		If iFieldType = WI_CHAR_FIELD Or iFieldType = WI_VIRT_CHAR Or iFieldType = WI_VIRT_NUM Or iFieldType = WI_NUM_FIELD Then
			   			GLFieldArray_mix(k) = ThisField.Name
			   			k = k + 1
				   	End If 

				   	If iFieldType = WI_CHAR_FIELD Or iFieldType = WI_VIRT_CHAR  Then
			   			GLFieldArray_Char(n) = ThisField.Name
			   			n = n + 1
				   	End If 
	Next i
			Set sfield = Nothing
		Set ThisField = Nothing
	Set thisTableFile = Nothing 
	Set SourceFile = Nothing
End Function


' 萃取檔案
' 20180718更新  增加條件篩選


Function Z_DirectExtractionTable(sOldTable As String, sNewTable As String , sCriteria As String)

	
	Set db = Client.OpenDatabase(sOldTable)
	Set task = db.Extraction
	task.IncludeAllFields
	dbName = sNewTable
	task.AddExtraction dbName, "", sCriteria
	task.CreateVirtualDatabase = False
	task.PerformTask 1, db.Count
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
End Function

'=====================================================================
'   對資料表內欄位名稱進行變更
'  輸入資訊 1. 資料表名稱
'                    2. 原本欄位名稱
'                    3. 變更後欄位名稱
'=====================================================================
Function Z_renameFields(sTempFilename As String, sOldFieldName As String, sNewFieldName As String)  As Boolean
	On Error GoTo ErrorHandler
	Dim db As database
	Dim table As table
	Dim task As task
	Dim field As field
	Dim newField As Object
	Dim iFieldType As Integer
	Dim sDescription As String
	Dim iDecimals As Integer
	Dim bImpliedDecimals As Boolean
	Dim iLen As Integer
	Dim sEqn As String

	Set db = client.openDatabase(sTempFilename)
	Set table = db.tabledef
	Set field = table.getfield(sOldFieldName)
	iFieldType = field.Type
	
	'obtain the old info
	sDescription = field.Description
	
	Select Case iFieldType
		Case WI_NUM_FIELD, WI_VIRT_NUM, WI_BOOL, WI_MULTISTATE  
			iDecimals = field.Decimals
			bImpliedDecimals = field.IsImpliedDecimal
			sEqn = field.Equation
		Case WI_CHAR_FIELD,3
			iLen = field.Length
		Case WI_VIRT_CHAR
			iLen = field.Length
			sEqn = field.Equation
		Case WI_DATE_FIELD, WI_TIME_FIELD, WI_VIRT_DATE, WI_VIRT_TIME
			sEqn = field.Equation
			
	End Select
	
	Set db = Nothing
	Set table = Nothing 
	Set field = Nothing

	'change the field name
	Set db = client.openDatabase(sTempFilename)
	Set task = db.TableManagement
	Set table = db.tabledef
	Set newField = table.NewField

	newField.Name = sNewFieldName
	newField.Description = sDescription

	Select Case iFieldType
		Case WI_NUM_FIELD, WI_VIRT_NUM
			If iFieldType = WI_NUM_FIELD Then
				newField.Type = WI_NUM_FIELD
			Else
				newField.Type = WI_VIRT_NUM
			End If
			newField.Decimals = iDecimals
			newField.IsImpliedDecimal = bImpliedDecimals
			Newfield.Description = sOldFieldName
			If Left(sEqn,1) = "/" Then
			   newField.Equation = ""
			Else
			   newField.Equation = sEqn
			End If
		Case WI_CHAR_FIELD,3
			newField.Type = WI_CHAR_FIELD
			newField.Length = iLen
			Newfield.Description = sOldFieldName
		Case WI_VIRT_CHAR
			newField.Type = WI_VIRT_CHAR
			newField.Length = iLen
			newField.Equation = sEqn
			Newfield.Description = sOldFieldName
		Case WI_DATE_FIELD
			newField.Type = WI_DATE_FIELD
			newField.Equation = sEqn
			Newfield.Description = sOldFieldName
		Case WI_VIRT_DATE
			newField.Type = WI_VIRT_DATE
			newField.Equation = sEqn
			Newfield.Description = sOldFieldName
		Case WI_TIME_FIELD
			newField.Type = WI_TIME_FIELD
			newField.Equation = sEqn
			Newfield.Description = sOldFieldName
		Case WI_VIRT_TIME
			newField.Type = WI_VIRT_TIME
			newField.Equation = sEqn
			Newfield.Description = sOldFieldName
		Case WI_BOOL  
			newField.Type = WI_BOOL  
			newField.Decimals = iDecimals
			newField.IsImpliedDecimal = bImpliedDecimals
			newField.Equation = sEqn
			Newfield.Description = sOldFieldName
		Case WI_MULTISTATE 
			newField.Type = WI_MULTISTATE 
			newField.Decimals = iDecimals
			newField.IsImpliedDecimal = bImpliedDecimals
			newField.Equation = sEqn
			Newfield.Description = sOldFieldName

	End Select

	task.ReplaceField sOldFieldName, newField
	
	task.PerformTask
	
	z_renameFields = true
	
	GoTo ReleaseVariables
	
ErrorHandler:	
	z_renameFields = false
ReleaseVariables:
	Set task = Nothing
	Set db = Nothing
	Set table = Nothing
	Set field = Nothing
	Set newField = Nothing
		
End Function



'-------TB File, GL ↑--------


'-----------完整性↓-------------

'  計算總額
Function GetTotal(DBname As String, fieldname1 As String, TotalType As String)
	If fieldname1 = "" Then
		Set db = Client.OpenDatabase(DBname)
		Select Case TotalType 
			Case "DBCount"
			GetTotal = db.Count  ' DB Number of Records
		End Select
		Set db = Nothing
	 Else
		' Open the database.
		Set db = Client.OpenDatabase(DBname)
		' Get the field statistics.
		Set stats = db.FieldStats(fieldname1)
		Select Case TotalType 	
			' Obtain the net value for the field.
			Case "NetValue"
				GetTotal =  Abs( stats.NetValue() )
			Case "PositiveValue"
				GetTotal = stats.DrValue()
			Case "NegativeVaule"
				GetTotal = stats.CrValue()
			Case "MaxValue"
				GetTotal = stats.MaxValue()
			Case "AverageValue"
				GetTotal = stats.AvgValue()
		End Select
		' Clear the memory.
		Set stats = Nothing
		Set db = Nothing
	End If
End Function


Function Z_Get_Char_NumBlanks(DBname As String, fieldname1 As String)

	Set db = Client.OpenDatabase(DBname)
	Set stats = db.FieldStats(fieldname1)
	Z_Get_Char_NumBlanks = stats.NumBlanks()
	Set db = Nothing
	Set stats = Nothing

End Function

Function Step1_Export_Excel

	Dim sTemp As String
	Dim db  As database
	Dim rs As recordset
	Dim ThisTable As Object
	Dim field As field
	Dim rec As Object
	Dim i As Long
	Dim j As Integer
	Dim iFieldCount As Integer
	Dim P As Integer
	
	S1Check = 0
	
	strr= sTempExcelSource & "\ValidationReport.xlsx"   ' "Exports.ILB\ValidationReport.xlsx"
	dstr = Client.WorkingDirectory  & "Exports.ILB\" & sEngagement_Info & "_" & Format(Now, "yyyymmdd") & Format(Now, "hhmmss") & "_ValidationReport.xlsx"
	
	FileCopy strr, dstr
	
	Set sExcel = CreateObject("Excel.Application")
	Set oBook = sExcel.Workbooks.Open(dstr)
	Set oSheet = oBook.Worksheets.Item("ValidationReport")

	oSheet.Range("E1").value = "Client:" & sEngagement_Info
	oSheet.Range("E2").value = "Year End: " & sPeriod_End_Date
	oSheet.Range("E4").value = "Prepared by: " & UserFullName
	oSheet.Range("E5").value = "Prepared date: " & Format(DateValue(Now),"YYYY/MM/DD")
	oSheet.Range("E9").value =  iSplit(sGLFilename,"","\",1,1)  'GL_Source_File_Name
	oSheet.Range("E14").value =  sTB_File_Name  'TB_Source_File_Name
	
	'  Total Amount amount of journal entries
	amountTotal = GetTotal("#GL#.IDM", "傳票金額_JE", "NetValue")
	If amountTotal>0.01 Or amountTotal<-0.01  Then
		oSheet.Range("E10").value = amountTotal
	Else
		oSheet.Range("E10").value = 0
	End If 
							
	'  Total DEBIT amount of journal entries
	sTemp = GetTotal("#GL#.IDM", "傳票金額_JE" , "PositiveValue")
	oSheet.Range("E11").value = sTemp

	'  Total CREBIT amount of journal entries
	sTemp = GetTotal("#GL#.IDM", "傳票金額_JE" , "NegativeVaule")
	oSheet.Range("E12").value = sTemp

	'  Number of journal entries
	sTemp = GetTotal("#GL#.IDM", "" , "DBCount")
	oSheet.Range("E13").value = sTemp

	'  Number of accounts in TB file
	sTemp = GetTotal("#TB#.IDM" ,"" ,"DBCount" )
	oSheet.Range("E15").value = sTemp
	
	sTemp = GetTotal("#Null-GL_Account.IDM" ,"" ,"DBCount" )
	oSheet.Range("D20").value = sTemp
	If sTemp > 0   Then
		oSheet.Range("D20").Interior.Color = RGB(255, 255, 0)
		
		If sVer = "TW" And sTemp > 10000 Then GoTo StepExport1
		
		strr = Client.WorkingDirectory  & "Exports.ILB\#Null-GL_Account.xlsx"
		Call Z_ExportDatabaseXLSX("#Null-GL_Account.IDM", "#Null-GL_Account.xlsx" , "V_Report 1")
		
		Set oSheet = oBook.Worksheets.Add
		oSheet.Name = "V_Report 1"
		Set oBook2=sExcel.Workbooks.Open(strr)
		Set oSheet2=oBook2.Worksheets.item("V_Report 1")		
		Set oRange=oSheet2.UsedRange
		oRange.Copy
		oSheet.Paste
		sExcel.CutCopyMode = False 
		oBook2.Save
		oBook2.Close (True)
		Kill strr 

		For i = 1 To  20
			oSheet.Columns(i).EntireColumn.AutoFit
		Next i
		
		oBook.Sheets("V_Report 1").Move After:=oBook.Sheets(oBook.Sheets.Count)
				
	End If

	StepExport1 :
		
	Set oSheet = oBook.Worksheets.Item("ValidationReport")
	sTemp = GetTotal("#Null-GL_Number.IDM" ,"" ,"DBCount" )
	oSheet.Range("D21").value = sTemp
	If sTemp > 0   Then
		oSheet.Range("D21").Interior.Color = RGB(255, 255, 0)
		
		If sVer = "TW" And sTemp > 10000 Then GoTo StepExport2
		
		strr = Client.WorkingDirectory  & "Exports.ILB\#Null-GL_Number.xlsx"
		Call Z_ExportDatabaseXLSX("#Null-GL_Number.IDM", "#Null-GL_Number.xlsx" , "V_Report 2")
		
		Set oSheet = oBook.Worksheets.Add
		oSheet.Name = "V_Report 2"
		Set oBook2=sExcel.Workbooks.Open(strr)
		Set oSheet2=oBook2.Worksheets.item("V_Report 2")		
		Set oRange=oSheet2.UsedRange
		oRange.Copy
		oSheet.Paste
		sExcel.CutCopyMode = False 
		oBook2.Save
		oBook2.Close (True)
		Kill strr 

		For i = 1 To  20
			oSheet.Columns(i).EntireColumn.AutoFit
		Next i
		
		oBook.Sheets("V_Report 2").Move After:=oBook.Sheets(oBook.Sheets.Count)
				
	End If

	StepExport2 :

	Set oSheet = oBook.Worksheets.Item("ValidationReport")
	sTemp = GetTotal("#Null-GL_Description_Criteria.IDM" ,"" ,"DBCount" )
	oSheet.Range("D22").value = sTemp
	If sTemp > 0   Then
		
		oSheet.Range("D22").Interior.Color = RGB(255, 255, 0)
		
		If sVer = "TW" And sTemp > 10000 Then GoTo StepExport3
		
		strr = Client.WorkingDirectory  & "Exports.ILB\#Null-GL_Description.xlsx"
		Call Z_ExportDatabaseXLSX("#Null-GL_Description_Criteria.IDM", "#Null-GL_Description.xlsx" , "V_Report 3")
		
		Set oSheet = oBook.Worksheets.Add
		oSheet.Name = "V_Report 3"
		Set oBook2=sExcel.Workbooks.Open(strr)
		Set oSheet2=oBook2.Worksheets.item("V_Report 3")		
		Set oRange=oSheet2.UsedRange
		oRange.Copy
		oSheet.Paste
		sExcel.CutCopyMode = False 
		oBook2.Save
		oBook2.Close (True)
		Kill strr 

		For i = 1 To  20
			oSheet.Columns(i).EntireColumn.AutoFit
		Next i
		
		oBook.Sheets("V_Report 3").Move After:=oBook.Sheets(oBook.Sheets.Count)
				
	End If
	
	StepExport3 :
		
	Set oSheet = oBook.Worksheets.Item("ValidationReport")
	If Z_File_Exist("#NotinPeriod-ApprovalDate.IDM") = True Then 
		sTemp = GetTotal("#NotinPeriod-ApprovalDate.IDM" ,"" ,"DBCount" )
		oSheet.Range("D23").value = sTemp
		If sTemp > 0   Then
			oSheet.Range("D23").Interior.Color = RGB(255, 255, 0)
			
			If sVer = "TW" And sTemp > 10000 Then GoTo StepExport4
			
			strr = Client.WorkingDirectory  & "Exports.ILB\#NotinPeriod-ApprovalDate.xlsx"
			Call Z_ExportDatabaseXLSX("#NotinPeriod-ApprovalDate.IDM", "#NotinPeriod-ApprovalDate.xlsx" , "V_Report 4")
			
			Set oSheet = oBook.Worksheets.Add
			oSheet.Name = "V_Report 4"
			Set oBook2=sExcel.Workbooks.Open(strr)
			Set oSheet2=oBook2.Worksheets.item("V_Report 4")		
			Set oRange=oSheet2.UsedRange
			oRange.Copy
			oSheet.Paste
			sExcel.CutCopyMode = False 
			oBook2.Save
			oBook2.Close (True)
			Kill strr 
	
			For i = 1 To  20
				oSheet.Columns(i).EntireColumn.AutoFit
			Next i
			
			oBook.Sheets("V_Report 4").Move After:=oBook.Sheets(oBook.Sheets.Count)
					
		End If
	Else
		oSheet.Range("D23").value = "N/A"
		oSheet.Range("E23").value = "未設定傳票核准日欄位"
	End If 
	
	StepExport4 :
		
	'  把end - open <> debit - credit 的差異匯出           
	Set oSheet = oBook.Worksheets.Item("ValidationReport")
	sTemp = GetTotal("#List_of_accounts_with_variance.IDM" ,"" ,"DBCount" )
	oSheet.Range("D24").value = sTemp
	If sTemp > 0  Then    ' 需更改檔案頁籤名稱
		oSheet.Range("D24").Interior.Color = RGB(255, 255, 0)
		S1Check = 1
	End If	
	
		strr = Client.WorkingDirectory  & "Exports.ILB\#Completeness_Check.xlsx"
		Call Z_ExportDatabaseXLSX("#Completeness_Check.IDM", "#Completeness_Check.xlsx" , "V_Report 5")
		
		Set oSheet = oBook.Worksheets.Add
		oSheet.Name = "V_Report 5"
		Set oBook2=sExcel.Workbooks.Open(strr)
		Set oSheet2=oBook2.Worksheets.item("V_Report 5")		
		Set oRange=oSheet2.UsedRange
		oRange.Copy
		oSheet.Paste
		sExcel.CutCopyMode = False 
		oBook2.Save
		oBook2.Close (True)
		Kill strr 

		For i = 1 To  20
			oSheet.Columns(i).EntireColumn.AutoFit
		Next i
		
		oBook.Sheets("V_Report 5").Move After:=oBook.Sheets(oBook.Sheets.Count)			

		
	Set oSheet = oBook.Worksheets.Item("step1-3 完整性測試之差異說明")

	If GetTotal("#List_of_accounts_with_variance.IDM" ,"" ,"DBCount" ) > 0 Then

		oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
		oSheet.Range("A2").value = "測試資料期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date
		oSheet.Range("A3").value = "財務報表準備期間 - 開始日 : " & sLast_Accounting_Period_Date
		
		Set db = client.OpenDatabase("#List_of_accounts_with_variance.IDM")
		Set ThisTable = db.TableDef
		Set rs = db.RecordSet		
			
		p = 1
		iFieldCount = ThisTable.count
		For j = 1 To iFieldCount
			Set field = ThisTable.GetFieldAt (j) 
				rs.ToFirst
				For i = 1 To db.count
				Set rec = rs.ActiveRecord
					rs.Next
						If field.Name = "ACCOUNT_NUM_ALL" Then oSheet.Cells(i + 16, 2).Value = Chr(39) & rec.GetCharValueAt(j)
						If field.Name = "會計科目名稱_TB" Then oSheet.Cells(i + 16, 3).Value = Chr(39) & rec.GetCharValueAt(j)
						If field.Name = "DIFF" Then oSheet.Cells(i + 16, 4).Value = Chr(39) & rec.GetNumValueAt (j)
					
					sMsg = "處理進度 9/11 - 資料處理項目 [完整性測試之差異明細匯出]..." & Chr(10) & "Field " & field.name & " exprt record .." & i & "/" & db.count
					dlgText "Text1", sMsg
					'P = P + 1 ' Remark 20200218
				Next i
		Next j
			
		oSheet.Protect PW, DrawingObjects:=False, Contents:=False, Scenarios:=False
		oSheet.Cells.Locked = False   '解鎖
		oSheet.Range("B17:D" & 17+db.count ).Locked = True  '鎖定特定儲存格 
		oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True
					
	Else
		 sExcel.DisplayAlerts = False
		 oBook.Sheets("step1-3 完整性測試之差異說明").Delete
		 sExcel.DisplayAlerts = True
		 'oBook.Worksheets.Item("step1-3 完整性測試之差異說明")
	End If				
		
	If sVer = "TW" Then 
	
		Set oSheet = oBook.Worksheets.Item("ValidationReport")
		sTemp = GetTotal("#GL_#Doc_not_Balance.IDM" ,"" ,"DBCount" )
		oSheet.Range("D25").value = sTemp
		If sTemp > 0 And sTemp < 10000 Then    ' 需更改檔案頁籤名稱
			oSheet.Range("D25").Interior.Color = RGB(255, 255, 0)
	
			strr = Client.WorkingDirectory  & "Exports.ILB\#GL_#Doc_not_Balance.xlsx"
			Call Z_ExportDatabaseXLSX("#GL_#Doc_not_Balance.IDM", "#GL_#Doc_not_Balance.xlsx" , "V_Report 6")
			
			Set oSheet = oBook.Worksheets.Add
			oSheet.Name = "V_Report 6"
			Set oBook2=sExcel.Workbooks.Open(strr)
			Set oSheet2=oBook2.Worksheets.item("V_Report 6")		
			Set oRange=oSheet2.UsedRange
			oRange.Copy
			oSheet.Paste
			sExcel.CutCopyMode = False 
			oBook2.Save
			oBook2.Close (True)
			Kill strr 
	
			For i = 1 To  20
				oSheet.Columns(i).EntireColumn.AutoFit
			Next i
			
			oBook.Sheets("V_Report 6").Move After:=oBook.Sheets(oBook.Sheets.Count)	
		End If	
	
	
		Set oSheet = oBook.Worksheets.Item("自動化工具-檔案欄位資訊")
	
		oSheet.Range("A1" ).Value = sProgVer
		
		oSheet.Range("A2" ).Value = "TB檔案配對前後欄位對照表"
		
		oSheet.Range("E3").Value ="配對後欄位名稱"
		oSheet.Range("B3").Value ="欄位型態"
		oSheet.Range("C3").Value ="文字長度"
		oSheet.Range("D3").Value ="小數位數"
		oSheet.Range("A3").Value ="配對前欄位名稱"
		oSheet.Range("A3:E3").Font.Bold = True
		oSheet.Range("A3:E3").Font.Name = "Calibri"
		oSheet.Range("A3:E3").Font.Size = 12
		oSheet.Range("A3:E3").Interior.Color = RGB(240, 240, 0)
	
		'標註是否改過欄位
		Dim imark As Boolean
		imark = false
		On Error Resume Next
		Set value = ThisTable.GetField(field.name & "_TEMP")	
		If Err.Number <> 0 Then	
    			Err.Clear
    		Else
    			imark = true
		End If
		On Error GoTo 0	
		
		i = 3
			
		Set db = client.openDatabase("#TB#.IDM")
		Set thistable = db.tabledef
		iFieldCount = ThisTable.count
		For j = 1 To iFieldCount
			Set field = ThisTable.GetFieldAt (j)
			iFieldType = field.Type
			
			i = i + 1
			If iRight(field.name,3) = "_TB" Then oSheet.Range("E" & i ).Value = field.name	
			
			Select Case iFieldType
				Case WI_NUM_FIELD, WI_VIRT_NUM, WI_BOOL, WI_MULTISTATE  
					oSheet.Range("B" & i ).Value = "數字型態"
					oSheet.Range("D" & i ).Value = field.Decimals
					'iDecimals = field.Decimals
					'bImpliedDecimals = field.IsImpliedDecimal
					'sEqn = field.Equation
				Case WI_CHAR_FIELD,3
					If imark = false Then
						oSheet.Range("B" & i ).Value = "文字型態"
						oSheet.Range("C" & i ).Value = field.Length
					Else
						oSheet.Range("B" & i ).Value = "數字型態"
					End If 
					'iLen = field.Length
				Case WI_VIRT_CHAR
					If imark = false Then
						oSheet.Range("B" & i ).Value = "文字型態"
						oSheet.Range("C" & i ).Value = field.Length
					Else
						oSheet.Range("B" & i ).Value = "數字型態"
					End If 
					'iLen = field.Length
					'sEqn = field.Equation
				Case WI_DATE_FIELD,  WI_VIRT_DATE
					oSheet.Range("B" & i ).Value = "日期型態"
					'sEqn = field.Equation
				Case WI_TIME_FIELD, WI_VIRT_TIME
					oSheet.Range("B" & i ).Value = "時間型態"
			End Select
			If field.Description = "" Then 
				oSheet.Range("A" & i ).Value = field.name
			Else
				oSheet.Range("A" & i ).Value = field.Description
			End If 
				
		Next j 
	
	
		i = i + 2
		p = i
	
		oSheet.Range("A" & i  ).Value = "GL檔案配對前後欄位對照表"
		
		i = i + 1
		
		oSheet.Range("E"  & i ).Value ="配對後欄位名稱"
		oSheet.Range("B"  & i ).Value ="欄位型態"
		oSheet.Range("C"  & i ).Value ="文字長度"
		oSheet.Range("D"  & i ).Value ="小數位數"
		oSheet.Range("A"  & i ).Value ="配對前欄位名稱"
		oSheet.Range("A"  & i  & ":E"  & i ).Font.Bold = True
		oSheet.Range("A"  & i  & ":E"  & i ).Font.Name = "Calibri"
		oSheet.Range("A"  & i  & ":E"  & i  ).Font.Size = 12
		oSheet.Range("A"  & i  & ":E"  & i ).Interior.Color = RGB(230, 230, 0)	
		
	
		Set db = client.openDatabase("#GL#DESC.IDM")
		Set thistable = db.tabledef
		iFieldCount = ThisTable.count
		For j = 1 To iFieldCount
			Set field = ThisTable.GetFieldAt (j)
			iFieldType = field.Type
			
			'標註是否改過欄位
			imark = false
			On Error Resume Next
			Set value = ThisTable.GetField(field.name & "_TEMP")	
			If Err.Number <> 0 Then	
    				Err.Clear
    			Else
    				imark = true
			End If
			On Error GoTo 0
			
			i = i + 1
			
			If iRight(field.name,3) = "_JE" Or iRight(field.name,5) = "_JE_S"  Then oSheet.Range("E" & i ).Value = field.name	
			
			Select Case iFieldType
				Case WI_NUM_FIELD, WI_VIRT_NUM, WI_BOOL, WI_MULTISTATE  
					oSheet.Range("B" & i ).Value = "數字型態"
					oSheet.Range("D" & i ).Value = field.Decimals
					'iDecimals = field.Decimals
					'bImpliedDecimals = field.IsImpliedDecimal
					'sEqn = field.Equation
				Case WI_CHAR_FIELD,3
					If imark = false Then
						oSheet.Range("B" & i ).Value = "文字型態"
						oSheet.Range("C" & i ).Value = field.Length
					Else
						oSheet.Range("B" & i ).Value = "數字型態"
					End If 
					'iLen = field.Length
				Case WI_VIRT_CHAR
					If imark = false Then
						oSheet.Range("B" & i ).Value = "文字型態"
						oSheet.Range("C" & i ).Value = field.Length
					Else
						oSheet.Range("B" & i ).Value = "數字型態"
					End If
					'iLen = field.Length
					'sEqn = field.Equation
				Case WI_DATE_FIELD,  WI_VIRT_DATE
					oSheet.Range("B" & i ).Value = "日期型態"
					'sEqn = field.Equation
				Case WI_TIME_FIELD, WI_VIRT_TIME
					oSheet.Range("B" & i ).Value = "時間型態"
			End Select
			
			If field.Description = "" Then
				oSheet.Range("A" & i ).Value = field.name
			Else
				oSheet.Range("A" & i ).Value = field.Description
			End If
				
		Next j 
		
		For j = 1 To  5
			oSheet.Columns(j).EntireColumn.AutoFit
		Next j	
		
		oSheet.Range("A1" ,"E1" ).Merge
		oSheet.Range("A" & p,"E" & P).Merge
	End If		
	
	Set oSheet = oBook.Worksheets.Item("ValidationReport")
	oSheet.Activate
	
	PW  = sEngagement_Info & " - " & iSplit(sGLFilename,"","\",1,1)
	oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True
	
	oBook.Save
	oBook.Close (True)
	sExcel.Quit
	Set oRange = Nothing
	Set oSheet = Nothing
	Set oBook = Nothing
	Set sExcel=Nothing
End Function



Function GetNulls(DBname As String, fieldname1 As String, saveDB As String)
	Dim Equation As String
	Dim fieldcount As Integer
	Dim fld As fld 
	Dim table As tableDef
	
	Equation = "@IsBlank(" & fieldname1 & ")"
	Set db = Client.OpenDatabase(DBname)
	Set table =db.tableDef
	fieldcount = table.Count
	
	For i =1 To fieldcount      'to check if the field is a numeric field. If it is numeric then we have to convert it to string before performin IsBlank function
		Set fld =Table.GetFieldAt(i)
		If(( fld.IsNumeric) And (fld.Name = fieldName1)) Then
			Equation = "@IsBlank(@str(" &  fieldname1 & ",1,0))"
		End If
	Next i
	Set task = db.Extraction
	task.IncludeAllFields 
	task.AddExtraction SaveDB, "", Equation
	task.CreateVirtualDatabase = False
	task.PerformTask 1, db.Count
	Set task = Nothing
	Set table = Nothing
	Set db = Nothing
	Client.OpenDatabase (SaveDB)
End Function

Function Z_Field_Info(sTempFilename As String, sOldFieldName As String) 

        Dim db As database
        Dim table As table
        Dim task As task
        Dim field As field
        Dim newField As Object
        Dim iFieldType As Integer
        
        sType = ""
        sDecimals = 0
        sLen = 0

        Set db = client.openDatabase(sTempFilename)
        Set table = db.tabledef
        Set field = table.getfield(sOldFieldName)
        iFieldType = field.Type
                
        Select Case iFieldType
                Case WI_NUM_FIELD, WI_VIRT_NUM, WI_BOOL, WI_MULTISTATE  
                        sType = "WI_VIRT_NUM"
                        sDecimals = field.Decimals
                Case WI_CHAR_FIELD, 3
                        sType = "WI_VIRT_CHAR"
                        sLen = field.Length
                Case WI_VIRT_CHAR
                        sType = "WI_VIRT_CHAR"
                        sLen = field.Length
                Case WI_DATE_FIELD, WI_TIME_FIELD, WI_VIRT_DATE, WI_VIRT_TIME
                        sType = "WI_VIRT_DATE"
                        
        End Select
        
        Set db = Nothing
        Set table = Nothing 
        Set field = Nothing
                
End Function


'-----------完整性↑-------------
Function X_Create_Project_Info_Table

	Dim SQLrs1 As String 
	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	'SQLeqn = "SELECT count(*) as " & """" & "TableCount" & """" & " FROM INFORMATION_SCHEMA.TABLES  Where table_name = " & Chr(39) & "JE_PROJECT_INFO" & Chr(39)
	SQLeqn = "SELECT count(*) as " & """" & "TableCount" & """" & " FROM sqlite_master WHERE type=" & Chr(39) &  "table" & Chr(39) &  " AND name= " & Chr(39) &  "JE_PROJECT_INFO" & Chr(39)
	
		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)


			'  增加判斷TB or GL 是否完成
			If Val(SQLrs.Fields("TableCount")) <> 1 Then
				'建立案件資訊表
				SQLeqn = " CREATE TABLE [JE_PROJECT_INFO] (  [Engagement_Info] TEXT, [Period_Start_Date] TEXT, " &  _
							" [Period_End_Date] TEXT, [Last_Accounting_Period_Date] TEXT, [Industry] TEXT, [ERP] TEXT, [Population] INTEGER, [STEP_1] INTEGER, " &  _
							" [STEP_2] INTEGER, [STEP_3] INTEGER, [STEP_4] INTEGER, [STEP_5] INTEGER, [STEP_6] INTEGER, [GL_Finsh] INTEGER, [TB_Finsh] INTEGER, [TB_File] TEXT, [GL_File] TEXT)"
				'Set SQLrs1 = 
				SQLobjConn.execute(SQLeqn)

				SQLeqn = " Insert into [JE_PROJECT_INFO] VALUES ( " & Chr(39) & "No Defined" & Chr(39) & "," & Chr(39) & "No Defined" & Chr(39) & "," & Chr(39) & "No Defined" & Chr(39) & _ 
							"," & Chr(39) & "No Defined" & Chr(39) & ", " & Chr(39) & "No Defined" & Chr(39) & ", " & Chr(39) & "No Defined" & Chr(39) & ", 0, 1,0,0,0,0,0,0,0,"  & _
							 Chr(39) & "No Defined" & Chr(39) & ", " & Chr(39) & "No Defined" & Chr(39) & " )"
				'Set SQLrs1 =
				 SQLobjConn.execute(SQLeqn)
				
				'建立Routines結果表
				SQLeqn = " CREATE TABLE [JE_Routines] (  [R1] INTEGER, [R1_Status]  TEXT, [R1_Result] TEXT,  [R2] INTEGER, [R2_Status]  TEXT, [R2_Result] TEXT, " & _ 
						" [R3] INTEGER, [R3_Status]  TEXT, [R3_Result] TEXT, [R4] INTEGER, [R4_Status]  TEXT, [R4_Result] TEXT, " & _
						" [R5] INTEGER, [R5_Status]  TEXT, [R5_Result] TEXT, [R6] INTEGER, [R6_Status]  TEXT, [R6_Result] TEXT, " & _
						" [R7] INTEGER, [R7_Status]  TEXT, [R7_Result] TEXT, [R8] INTEGER, [R8_Status]  TEXT, [R8_Result] TEXT, " & _ 
						" [A2] INTEGER, [A2_Status]  TEXT, [A2_Result] TEXT, [A4] INTEGER, [A4_Status]  TEXT, [A4_Result] TEXT,  " & _
						" [A3] INTEGER, [A3_Status]  TEXT, [A3_Result] TEXT, [A2_Memo] TEXT, [A3_Memo] TEXT, [A4_Memo] TEXT )"
				'Set SQLrs1 = 
				SQLobjConn.execute(SQLeqn)

				SQLeqn = " Insert into [JE_Routines] VALUES ( 0," & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & "," & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39)  & " ) "

				'SQLeqn = " Insert into [JE_Routines] VALUES ( 0," & Chr(39) & "NA" & Chr(39) & ", " & " 0,0,0,0,0,0,0)"
				'Set SQLrs1 =
				 SQLobjConn.execute(SQLeqn)
				
				'建立Criteria結果表
				SQLeqn = " CREATE TABLE [JE_Criteria] (  [SEQ_Num] INTEGER, [W1] INTEGER, [W2] INTEGER, [W3] INTEGER, [W4] INTEGER, [W5] INTEGER, [W6] INTEGER, [W7] INTEGER, [W8] INTEGER, [W9] INTEGER, [W10] INTEGER )"
				'Set SQLrs1 = 
				SQLobjConn.execute(SQLeqn)

				SQLeqn = " Insert into [JE_Criteria] VALUES ( 1,0,0,0,0,0,0,0,0,0,0)"
				'Set SQLrs1 = 
				SQLobjConn.execute(SQLeqn)
				
				'建立Criteria Log表
				SQLeqn = " CREATE TABLE [JE_Criteria_Log] (  [SEQ_Num] TEXT,  [Criteria_Log] TEXT, [DocNum] INTEGER, [ItemNum] INTEGER, [SumCheck] INTEGER  )"
				'Set SQLrs1 = 
				SQLobjConn.execute(SQLeqn)			
				
				'建立各階段作業人員
				SQLeqn = " CREATE TABLE [JE_STEP_USER] ( [STEP_1] TEXT, [STEP_2] TEXT, [STEP_3] TEXT, [STEP_4] TEXT, [STEP_5] TEXT,  " & _ 
						" [STEP_1_Time] TEXT, [STEP_2_Time] TEXT, [STEP_3_Time] TEXT, [STEP_4_Time] TEXT, [STEP_5_Time] TEXT )"
				'Set SQLrs1 = 
				SQLobjConn.execute(SQLeqn)

				SQLeqn = " Insert into [JE_STEP_USER] VALUES ( " & Chr(39) & "NA" & Chr(39) & "," & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) &  _ 
						", " & Chr(39) & "NA" & Chr(39) & "," & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ")"
				'Set SQLrs1 =  
				SQLobjConn.execute(SQLeqn)
				
				'Set SQLrs1 = Nothing
				sEngagement_Info = "No Defined"

			Else
				Call X_Get_Project_Info
			End If
			
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing
End Function

Function X_Get_Project_Info

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Select * from [JE_PROJECT_INFO] " 

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
				sEngagement_Info = SQLrs.Fields("Engagement_Info")
				sPeriod_Start_Date = SQLrs.Fields("Period_Start_Date")
				sPeriod_End_Date = SQLrs.Fields("Period_End_Date")
				sLast_Accounting_Period_Date = SQLrs.Fields("Last_Accounting_Period_Date")
				sSTEP_1 = SQLrs.Fields("STEP_1")
				sSTEP_2 = SQLrs.Fields("STEP_2")
				sSTEP_3 = SQLrs.Fields("STEP_3")
				sSTEP_4 = SQLrs.Fields("STEP_4")
				sSTEP_5 = SQLrs.Fields("STEP_5")
				sSTEP_6 = SQLrs.Fields("STEP_6")
				sGL_Finsh = SQLrs.Fields("GL_Finsh")
				sTB_Finsh = SQLrs.Fields("TB_Finsh")
				sGL_FIle_Name = SQLrs.Fields("GL_File")
				sTB_FIle_Name = SQLrs.Fields("TB_File")
				sPopulation  = SQLrs.Fields("Population")
				sIndustry = SQLrs.Fields("Industry")
			
			SQLeqn = "Delete from  [Overview] where [Filename] like " & Chr(39) & "#%" & Chr(39)
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function


Function X_Update_Project_Info(s1 As String, s2 As String, s3 As String, s4 As String, s5 As String)

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Update [JE_PROJECT_INFO] Set Engagement_Info =  " & Chr(39) & s1 & Chr(39) & ", Period_Start_Date = " & Chr(39) &  s2 & Chr(39) & ", Period_End_Date = " & Chr(39) & s3 & Chr(39) & _
	                  ", Last_Accounting_Period_Date = " & Chr(39) & s4 & Chr(39) & ",  Industry = " & Chr(39) & s5 & Chr(39) & " where  Engagement_Info  = " & Chr(39) & sEngagement_Info  & Chr(39)

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
						
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function X_Update_Project_Info_FileName(s1 As String, s2 As String)

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Update [JE_PROJECT_INFO] Set " & S1 & " =  " & Chr(39) & s2 & Chr(39) 

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
						
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function


Function X_Update_Step_Info(s1 As String, s2 As Integer)

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Update [JE_PROJECT_INFO] Set " & s1 & " = " & s2  & " where  Engagement_Info  = " & Chr(39) & sEngagement_Info  & Chr(39)

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
								
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function X_Update_Step_User_Info(s1 As String, s2 As String, s3 As String, s4 As String)

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Update [JE_STEP_USER] Set " & S1 & " =  " & Chr(39) & s2 & Chr(39) & " , " & S3 & " =  " & Chr(39) & s4 & Chr(39)

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
						
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function X_Delete_Project_Info

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Delete from  [JE_PROJECT_INFO] where Engagement_Info  = " & Chr(39) & sEngagement_Info  & Chr(39)

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
				SQLeqn = " Insert into [JE_PROJECT_INFO] VALUES ( " & Chr(39) & "No Defined" & Chr(39) & "," & Chr(39) & "No Defined" & Chr(39) & "," & Chr(39) & "No Defined" & Chr(39) & _ 
							", " & Chr(39) & "No Defined" & Chr(39) &  ", " & Chr(39) & "No Defined" & Chr(39) & ", " & Chr(39) & "No Defined" & Chr(39) & ", 0, 1,0,0,0,0,0,0,0,"  & _
							 Chr(39) & "No Defined" & Chr(39) & ", " & Chr(39) & "No Defined" & Chr(39) & " )"
				Set SQLrs1 = SQLobjConn.execute(SQLeqn)
				
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function X_Update_Routines_Info(s1 As String, s2 As Integer)

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Update [JE_Routines] Set [" & s1 & "] = " & s2  

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
								
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function X_Update_Routines_Info_Str(s1 As String, s2 As String)

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Update [JE_Routines] Set [" & s1 & "] = " & Chr(39) & s2 & Chr(39)

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
								
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function X_Update_Criteria_Info(s1 As String, s2 As String)

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Update [JE_Criteria] Set [" & s1 & "] = " & s2  

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
								
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function X_Update_Criteria_Log(sWP As String, sCriteriaLog As String , sDocNum As String, sItemNum As String, sSumCheck As String)

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Insert into [JE_Criteria_Log] (  [SEQ_Num],  [Criteria_Log], [DocNum], [ItemNum], [SumCheck] ) Values (" & Chr(39) & sWP & Chr(39) &"," & Chr(39) & sCriteriaLog & Chr(39) & _ 
	                                            ", " & sDocNum  &  "," & sItemNum & "," & sSumCheck & ")"

	Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
								
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function


Function Introduction_Button_Control

	Call X_Get_Project_Info
	
	If sSTEP_3 = 1 Then sSTEP_2 = 1
			
	DlgEnable "BtnDataMap" ,  sSTEP_1
	DlgEnable "BtnLoadfile",  sSTEP_2
	DlgEnable "BtnRoutine",  0
	DlgEnable "BtnCriteria",  sSTEP_4
	DlgEnable "BtnExport",   sSTEP_5
	DlgEnable "BtnReRun", sSTEP_6
	DlgEnable "BtnCancel", 1
	
End Function

Function Introduction_Button_DisableALL
			
	DlgEnable "BtnDataMap" ,  0
	DlgEnable "BtnLoadfile",  0
	DlgEnable "BtnRoutine",  0
	DlgEnable "BtnCriteria",  0
	DlgEnable "BtnExport",  0 
	DlgEnable "BtnReRun", 0
	DlgEnable "BtnCancel", 0
	
End Function

Function X_Get_Routines_Info

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Select * from [JE_Routines] " 


		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
				sR1 = SQLrs.Fields("R1")
				sR1_Status = SQLrs.Fields("R1_Status")
				sR1_Result = SQLrs.Fields("R1_Result")
				sR2 = SQLrs.Fields("R2")
				sR2_Status = SQLrs.Fields("R2_Status")
				sR2_Result = SQLrs.Fields("R2_Result")
				sR3 = SQLrs.Fields("R3")
				sR3_Status = SQLrs.Fields("R3_Status")
				sR3_Result = SQLrs.Fields("R3_Result")
				sR4 = SQLrs.Fields("R4")
				sR4_Status = SQLrs.Fields("R4_Status")
				sR4_Result = SQLrs.Fields("R4_Result")
				sR5 = SQLrs.Fields("R5")
				sR5_Status = SQLrs.Fields("R5_Status")
				sR5_Result = SQLrs.Fields("R5_Result")
				sR6 = SQLrs.Fields("R6")
				sR6_Status = SQLrs.Fields("R6_Status")
				sR6_Result = SQLrs.Fields("R6_Result")
				sR7 = SQLrs.Fields("R7")
				sR7_Status = SQLrs.Fields("R7_Status")
				sR7_Result = SQLrs.Fields("R7_Result")
				sR8 = SQLrs.Fields("R8")
				sR8_Status = SQLrs.Fields("R8_Status")
				sR8_Result = SQLrs.Fields("R8_Result")
				'sA2 = SQLrs.Fields("A2")
				'sA2_Status = SQLrs.Fields("A2_Status")
				'sA2_Result = SQLrs.Fields("A2_Result")
				'sA3 = SQLrs.Fields("A3")
				'sA3_Status = SQLrs.Fields("A3_Status")
				'sA3_Result = SQLrs.Fields("A3_Result")
				'sA4 = SQLrs.Fields("A4")
				'sA4_Status = SQLrs.Fields("A4_Status")
				'sA4_Result = SQLrs.Fields("A4_Result")
			
			SQLeqn = "Delete from  [Overview] where [Filename] like " & Chr(39) & "#%" & Chr(39)
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing
		
End Function

Function Criteria_Dialog_Control

	Call X_Get_Routines_Info

		'DlgEnable "ChkBoxModfRout2", sA2
		'DlgEnable "ChkBoxModfRout3", sA3
		'DlgEnable "ChkBoxModfRout4", sA4
	
		DlgEnable "ChkBoxWeeekend_DocDate" ,  FindField("#GL#.IDM","DOC_WEEKEND_JE_T")
		DlgEnable "ChkBoxWeeekend_PostDate" ,   FindField("#GL#.IDM","POST_WEEKEND_JE_T")
		DlgEnable "ChkBoxHoliday_DocDate" ,   FindField("#GL#.IDM","DOC_HOLIDAY_JE_T")
		DlgEnable "ChkBoxHoliday_PostDate" ,  FindField("#GL#.IDM","POST_HOLIDAY_JE_T")
		If Z_File_Exist("#MakeUpDay#-Make-Up_Day.IDM") = True Then
			DlgEnable "ChkBoxMakeUpDay" ,   FindField("#GL#DESC.IDM","總帳日期_JE") 
			DlgEnable "ChkBoxMakeUpDay1" ,   FindField("#GL#DESC.IDM","傳票核准日_JE") 
		Else
			DlgEnable "ChkBoxMakeUpDay" ,0
			DlgEnable "ChkBoxMakeUpDay1" ,0
		End If
		If Z_File_Exist("#Null-GL_Description_Criteria.IDM") <> True Then Call getNulls("#GL#.IDM", "傳票摘要_JE", "#Null-GL_Description_Criteria.IDM") 
		DlgEnable "ChkBoxIsDescNull" , 1
			
End Function

Function X_Get_Criteria_Info

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Select * from [JE_Criteria] " 

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
				sSEQ_Num = SQLrs.Fields("SEQ_Num")
				sW1 = SQLrs.Fields("W1")
				sW2 = SQLrs.Fields("W2")
				sW3 = SQLrs.Fields("W3")
				sW4 = SQLrs.Fields("W4")
				sW5 = SQLrs.Fields("W5")
				sW6 = SQLrs.Fields("W6")
				sW7 = SQLrs.Fields("W7")
				sW8 = SQLrs.Fields("W8")
				sW9 = SQLrs.Fields("W9")
				sW10 = SQLrs.Fields("W10")
			
			SQLeqn = "Delete from  [Overview] where [Filename] like " & Chr(39) & "#%" & Chr(39)
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function Step1_Export_AccountMapFile

	Dim i As Double 
	
	strr= sTempExcelSource & "\AccountMapping.xlsx" 
	dstr = Client.WorkingDirectory  +"Exports.ILB\" & sEngagement_Info & "_" & Format(Now, "yyyymmdd") & Format(Now, "hhmmss") & "_AccountMapping.XLSX"
	
	S1Check = 0
			
	FileCopy strr, dstr 

	Set sExcel = CreateObject("Excel.Application")
	
	Set oBook = sExcel.Workbooks.open(dstr)
	Set oSheet = oBook.Worksheets.Item("AccountMapping")
	
		Set db = Client.OpenDatabase("#Completeness_Check.IDM")
		Set ThisTable = db.TableDef
		Set field = ThisTable.GetFieldAt (1) 
			count = db.count
			Set rs = db.RecordSet
				rs.ToFirst
				For i = 1 To count
					rs.Next
					Set rec = rs.ActiveRecord
		
						If field.IsCharacter Then 
							oSheet.Cells(i+3, 1).Value = Chr(39) & rec.GetCharValueAt (1)
						ElseIf  field.IsNumeric Then 
							oSheet.Cells(i+3, 1).Value = rec.GetNumValueAt(1) 
						End If

						If  iAllTrim(rec.GetCharValueAt (2)) <> ""  Then
							oSheet.Cells(i+3, 2).Value = rec.GetCharValueAt (2) 
						Else
							oSheet.Cells(i+3, 2).Value = "Not in TB"
						End If
						dlgText "Text1","Export to Excel : " &  i & " / " & count
				Next i
			Set rec = Nothing
		Set rs = Nothing
	Set db = Nothing
	
	Dim ProtectNo As String
	ProtectNo = "JETeamSti11Tried"
	oSheet.Protect ProtectNo, DrawingObjects:=False, Contents:=False, Scenarios:=False
	oSheet.Cells.Locked = False   '解鎖
	oSheet.Range("A4:B1048576" ).Locked = True  '鎖定特定儲存格 
	oSheet.Protect ProtectNo, DrawingObjects:=True, Contents:=True, Scenarios:=True
	osheet.EnableSelection = xlNoRestrictions '可以選取所有儲存格
	
	oBook.save
	oBook.Close (True)
	sExcel.Quit
	Set oSheet = Nothing
	Set oBook = Nothing
	Set sExcel=Nothing
	
End Function


Function Step1_Validation

		'sTemp = "@BetweenDate( Posting_Date_JE ,"& Chr(34) & iRemove( sPeriod_Start_Date , "/") & Chr(34) & "," & Chr(34) & iRemove( sPeriod_End_Date , "/") & Chr(34) & ")" 

		Call Sort_FieldName
			
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.Extraction
		For i = 1 To UBound(NewArray)
			If NewArray(i) <> "" Then task.AddFieldToInc NewArray(i)
		Next i
		dbName = "#GL#In_Period.IDM"
		task.AddExtraction dbName, "", ""
		task.CreateVirtualDatabase = False
		task.PerformTask 1, db.Count
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)
		
		Call Z_Rename_DB("#GL#In_Period.IDM","#GL#.IDM")
		
		'2019.05.20 Add For WP 
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.TopRecordsExtraction
		task.IncludeAllFields
		task.AddKey "會計科目編號_JE", "D"
		dbName = "#GL#DESC.IDM"
		task.OutputFileName = dbName
		task.NumberOfRecordsToExtract = 1
		task.CreateVirtualDatabase = False
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)		
				
		' Completeness/rollforward test
		' Summarization GL file
	 	Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.Summarization
		task.AddFieldToSummarize  "會計科目編號_JE"  	'  彙總的欄位 
		task.AddFieldToTotal "傳票金額_JE"  	' 彙總的數字欄位
		 dbName = "#GL_Account_Sum.IDM"
		task.OutputDBName = dbName
		task.CreatePercentField = FALSE
		task.StatisticsToInclude = SM_SUM
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase ( dbName )
	 						
		' JoinDatabase Sum_GL & TB 
		'  需增加入主key和次要key值的型態判斷
		Set db = Client.OpenDatabase( "#GL_Account_Sum.IDM" )
		Set task = db.JoinDatabase
		task.FileToJoin "#TB#.IDM"
		task.IncludeAllPFields     ' 主要檔案的欄位加入 (若有選擇會科名稱，則可以加入)
		task.AddSFieldToInc "會計科目編號_TB"	'  次要檔案的欄位加入，為使畫面較乾淨，只需加入必要欄位
		task.AddSFieldToInc "會計科目名稱_TB"
		'task.AddSFieldToInc "Opening_Balance_TB"
		'task.AddSFieldToInc "Ending_Balance_TB"
		task.AddSFieldToInc "試算表變動金額_TB"
		task.IncludeAllSFields	
		task.AddMatchKey "會計科目編號_JE", "會計科目編號_TB", "A"
		task.CreateVirtualDatabase = False
		dbName = "#Completeness_calculate.IDM"
		task.PerformTask dbName, "", WI_JOIN_ALL_REC
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)						
		
		' 增加計算欄位、會科編號判斷
		Call Z_Field_Info("#Completeness_calculate.IDM", "傳票金額_JE_SUM")
		Set db = Client.OpenDatabase("#Completeness_calculate.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "DIFF"
		field.Description = ""
		field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
		'field.Equation = " ENDING_BALANCE_TB - OPENING_BALANCE_TB - 傳票金額_JE_SUM "
		field.Equation = " 試算表變動金額_TB - 傳票金額_JE_SUM "
		field.Decimals = sDecimals
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing

		Call Z_Field_Info("#Completeness_calculate.IDM", "會計科目編號_TB")
		Set db = Client.OpenDatabase("#Completeness_calculate.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "ACCOUNT_NUM_ALL"
		field.Description = ""

		If sType = "WI_VIRT_NUM" Then 
			field.Type = WI_VIRT_NUM
			field.Equation = "@If( 會計科目編號_JE <> 0,  會計科目編號_JE , 會計科目編號_TB )"
			field.Decimals = sDecimals    
		ElseIf sType = "WI_VIRT_CHAR" Then
			field.Type = WI_VIRT_CHAR
			field.Equation = "@If( 會計科目編號_JE <> " & Chr(34) & Chr(34) & ",  會計科目編號_JE , 會計科目編號_TB )"
			field.Length = sLen
		End If
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing 


		'  將完整性檔案的必要欄位匯出
		Set db = Client.OpenDatabase("#Completeness_calculate.IDM")
		Set task = db.Extraction
		task.AddFieldToInc "ACCOUNT_NUM_ALL"
		task.AddFieldToInc "會計科目名稱_TB"
		'task.AddFieldToInc "Opening_Balance_TB"
		'task.AddFieldToInc "Ending_Balance_TB"
		task.AddFieldToInc "試算表變動金額_TB"
		task.AddFieldToInc "傳票金額_JE_SUM"
		task.AddFieldToInc "DIFF"
		dbName = "#Completeness_Check.IDM"
		task.AddExtraction dbName, "", ""
		task.CreateVirtualDatabase = False
		task.PerformTask 1, db.Count
		Set task = Nothing
		Set db = Nothing
		
		'傳票號借貸不平
		
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.Summarization
		task.AddFieldToSummarize "傳票號碼_JE"
		task.AddFieldToTotal "傳票金額_JE"
		dbName = "#GL#In_Period_Doc_Sum.IDM"
		task.OutputDBName = dbName
		task.CreatePercentField = FALSE
		task.StatisticsToInclude = SM_SUM
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)
		
		Set db = Client.OpenDatabase("#GL#In_Period_Doc_Sum.IDM")
		Set task = db.Extraction
		task.IncludeAllFields
		dbName = "#GL#In_Period_Doc_Sum_Diff.IDM"
		task.AddExtraction dbName, "", " 傳票金額_JE_SUM <> 0"
		task.CreateVirtualDatabase = False
		task.PerformTask 1, db.Count
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)	
			
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.JoinDatabase
		task.FileToJoin "#GL#In_Period_Doc_Sum_Diff.IDM"
		task.IncludeAllPFields
		task.IncludeAllSFields
		task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
		task.CreateVirtualDatabase = False
		dbName = "#GL_#Doc_not_Balance.IDM"
		task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)			
			
		If GetTotal("#GL_#Doc_not_Balance.IDM" ,"" ,"DBCount" ) <> 0 Then 
			
			If FindField("#GL_#Doc_not_Balance.IDM","DEBIT_傳票金額_JE_T") = 0 Then 
				Call Z_Field_Info("#GL_#Doc_not_Balance.IDM", "傳票金額_JE")
				Set db = Client.OpenDatabase("#GL_#Doc_not_Balance.IDM")
				Set task = db.TableManagement
				Set field = db.TableDef.NewField
				field.Name = "DEBIT_傳票金額_JE_T"
				field.Description = ""
				field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
				field.Equation = "@If( 傳票金額_JE >= 0,  傳票金額_JE , 0 )"
				field.Decimals = sDecimals    
				task.AppendField field
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Set field = Nothing         
			
				Set db = Client.OpenDatabase("#GL_#Doc_not_Balance.IDM")
				Set task = db.TableManagement
				Set field = db.TableDef.NewField
				field.Name = "CREDIT_傳票金額_JE_T"
				field.Description = ""
				field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
				field.Equation = "@If( 傳票金額_JE < 0,  傳票金額_JE , 0 )"
				field.Decimals = sDecimals    
				task.AppendField field
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Set field = Nothing
			
				Set db = Client.OpenDatabase("#GL_#Doc_not_Balance.IDM")
				Set task = db.Summarization
				task.AddFieldToSummarize "傳票號碼_JE"
				task.AddFieldToSummarize "總帳日期_JE"
				task.AddFieldToTotal "DEBIT_傳票金額_JE_T"
				task.AddFieldToTotal "CREDIT_傳票金額_JE_T"
				dbName = "#GL_#Doc_not_Balance_Sum.IDM"
				task.OutputDBName = dbName
				task.CreatePercentField = FALSE
				task.StatisticsToInclude = SM_SUM
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)		
					
			End If 
				
			Call Z_Delete_File("#GL#In_Period_Doc_Sum.IDM")
			Call Z_Delete_File("#GL#In_Period_Doc_Sum_Diff.IDM")
			
		End If 
				
End Function

Function Sort_FieldName
	Dim SourceFile As Datebase
	Dim thisTableFile As TableDef
	Dim field_count As Integer
	Dim i, j, k, L As Integer
	Dim ThisField, sfield As Field
	Dim iFieldType As String
	  
	Set SourceFile = client.OpenDataBase("#GL#.IDM")
	Set thisTableFile = SourceFile.tabledef	  '這是個可以針對所指定的資料表操作的物件
	field_count = thisTableFile.count

	ReDim OrangeArray(field_count +1)
	ReDim NewArray(field_count +20)
							   	
	For i = 1 To field_count

		Set ThisField = thisTableFile.GetFieldAt(i)
		OrangeArray(i) = ThisField.Name
						   		
	Next i
	Set ThisField = Nothing
	Set thisTableFile = Nothing 
	Set SourceFile = Nothing

	j = 13
	For i = 1 To field_count
		If OrangeArray(i) = "傳票號碼_JE" Then 
			NewArray(1) = "傳票號碼_JE"
		ElseIf OrangeArray(i) = "傳票文件項次_JE_S" Then 
			NewArray(2) = "傳票文件項次_JE_S"
		ElseIf OrangeArray(i) = "傳票核准日_JE" Then 
			NewArray(3) = "傳票核准日_JE"
		ElseIf OrangeArray(i) = "總帳日期_JE" Then 
			NewArray(4) = "總帳日期_JE"
		ElseIf OrangeArray(i) = "傳票建立人員_JE" Then 
			NewArray(5) = "傳票建立人員_JE"
		ElseIf OrangeArray(i) = "傳票核准人員_JE" Then 
			NewArray(6) = "傳票核准人員_JE"
		ElseIf OrangeArray(i) = "會計科目編號_JE" Then 
			NewArray(7) = "會計科目編號_JE"
		ElseIf OrangeArray(i) = "會計科目名稱_JE" Then 
			NewArray(8) = "會計科目名稱_JE"
		ElseIf OrangeArray(i) = "傳票金額_JE" Then 
			NewArray(9) = "傳票金額_JE"
		ElseIf OrangeArray(i) = "傳票摘要_JE" Then 
			NewArray(10) = "傳票摘要_JE"
		ElseIf OrangeArray(i) = "分錄來源模組_JE" Then 
			NewArray(11) = "分錄來源模組_JE"
		ElseIf OrangeArray(i) = "人工傳票否_JE_S" Then 
			NewArray(12) = "人工傳票否_JE_S"
		ElseIf OrangeArray(i) = "過帳碼_JE" Then 
			NewArray(13) = "過帳碼_JE"
		Else
			NewArray(J) = OrangeArray(i)
			J = J + 1
		End If 
	Next i 

End Function


Function Step1_Export_INFFile

	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.RandomSample
	task.IncludeAllFields
	'task.AddFieldToInc "傳票號碼_JE"
	'task.AddFieldToInc "DOCUMENT_DATE_JE"
	'task.AddFieldToInc "POSTING_DATE_JE"
	'task.AddFieldToInc "傳票摘要_JE"
	'task.AddFieldToInc "會計科目編號_JE"
	'task.AddFieldToInc "傳票金額_JE"
	dbName = "#INF Report#.IDM"
	task.CreateVirtualDatabase = False
	task.PerformTask dbName, "", 59, 1, db.Count, Int(Rnd()*500), False
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	Set db = Client.OpenDatabase("#INF Report#.IDM")
	Set task = db.Sort
	task.AddKey "傳票號碼_JE", "A"
	dbName = "#INF Report#Sort.IDM"
	task.PerformTask dbName
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)	
	
End Function


Function Step1_Export_INF_Report										
										
	Dim sTemp As String									
	Dim db  As database									
	Dim rs As recordset									
	Dim ThisTable As Object									
	Dim field As field									
	Dim rec As Object									
	Dim i As Long									
	Dim j As Integer									
	Dim k As Integer									
	Dim n As Integer 									
										
	Dim iFieldCount As Integer									
	Dim P As Integer									
	Dim CriteriaLog(10) As String									
										
	On Error GoTo ErrorHandler									
										
	S1Check = 0									
										
	strr= sTempExcelSource & "\INFReport.xlsx"   '									
	dstr = Client.WorkingDirectory  & "Exports.ILB\" & sEngagement_Info & "_" & Format(Now, "yyyymmdd") & Format(Now, "hhmmss") & "_INFReport.xlsx"									
										
	FileCopy strr, dstr									
										
	Set sExcel = CreateObject("Excel.Application")									
	Set oBook = sExcel.Workbooks.Open(dstr)									
	Set oSheet = oBook.Worksheets.Item("INF Testing 可靠性測試")									
										
	sMsg = "Processing - Export generation information to Excel..."									
	dlgText "CriteriaMsgText", sMsg 									
										
	oSheet.Range("A1").value = "公司名稱 [" & sEngagement_Info & "]"									
	oSheet.Range("A2").value = "財務報表期間 [" & sPeriod_Start_Date & " ~ " & sPeriod_End_Date & "]"									
	'n = 24									
	n = 52									
										
	Set fs = CreateObject("Scripting.FileSystemObject")									
										
	If  fs.FileExists(client.WorkingDirectory & "#INF Report#Sort.IDM" ) Then 									
										
		Set db = client.OpenDatabase("#INF Report#Sort.IDM")								
		Set ThisTable = db.TableDef								
		Set rs = db.RecordSet								
										
		P = 0								
		iFieldCount = ThisTable.count								
		For j = 1 To iFieldCount								
			Set field = ThisTable.GetFieldAt (j) 							
				If iRight(field.name,3) = "_JE" Or iRight(field.name,5) = "_JE_S" Or  field.name = JE_DC_name  Then						
					P = 0					
					If field.name = "傳票號碼_JE" Then P = 2					
					If field.name = "會計科目編號_JE" Then p = 3					
					If field.name = "會計科目名稱_JE" Then P = 4					
					If field.name = "傳票金額_JE" Then P = 6					
					If field.name = "總帳日期_JE" Then P = 7					
					If field.name = "傳票核准日_JE" Then p = 8					
					If field.name = "傳票摘要_JE" Then P = 11					
					If  field.name = "分錄來源模組_JE" Then  P = 10					
					'If field.name = "人工傳票否_JE_S" Then P = 10					
					If field.name = "傳票建立人員_JE" Then P = 9					
					If field.name = "傳票核准人員_JE" Then P = 12					
					If field.name = JE_DC_name Then P = 5					
										
					If p <> 0 Then 					
						rs.ToFirst				
						For k = 1 To db.count				
							Set rec = rs.ActiveRecord			
							rs.Next			
							If field.IsCharacter Then 			
								oSheet.Cells(k + n, p).Value = Chr(39) & rec.GetCharValueAt (j) 		
								oSheet.Cells(k + n, p).NumberFormatLocal = "@"		
								If field.name = "傳票核准人員_JE" And P = 12 Then  oSheet.Cells(51, p).Value = "傳票核准人員"		
								If field.name = JE_DC_name  And P = 5 Then oSheet.Cells(k + n, p).Value = Chr(39) & rec.GetCharValueAt (j) 		
							ElseIf field.IsNumeric Then 			
								If field.name = "傳票建立人員_JE" And  P = 9 Then oSheet.Cells(k + n, p).Value = rec.GetNumValueAt(j)		
								If field.name = "傳票核准人員_JE" And P = 12 Then  oSheet.Cells(51, p).Value = "傳票核准人員"		
								If field.name = JE_DC_name  And P = 5 Then oSheet.Cells(k + n, p).Value = rec.GetNumValueAt(j)		
								'If p = 5 Then		
								'	If  rec.GetNumValueAt(j) > 0 Then	
								'		oSheet.Cells(k + n, p).Value = rec.GetNumValueAt(j)
								'		oSheet.Cells(k + n, p+1).Value = 0
								'	Else	
								'		oSheet.Cells(k + n, p+1).Value = rec.GetNumValueAt(j) * -1
								'		oSheet.Cells(k + n, p).Value = 0
								'	End If	
								'End If		
								oSheet.Cells(k + n, p).Value = rec.GetNumValueAt(j)		
										
								'If sVer = "TW" And p = 10 Then 		
								'	If  rec.GetNumValueAt(j) = 0 Then	
								'		oSheet.Cells(k + n, p).Value = "Automated" 'rec.GetNumValueAt(j)
								'	Else	
								'		oSheet.Cells(k + n, p).Value = "Manual"
								'	End If 	
								'End If 		
							ElseIf field.IsDate Then 			
								oSheet.Cells(k + n, p).Value = rec.GetDateValueAt(j)		
								'oSheet.Cells(k + n, p).NumberFormatLocal = "YYYY/MM/DD"		
							ElseIf field.IsTime Then 			
								oSheet.Cells(k + n, p).Value = rec.GetTimeValueAt(j) 		
							End If			
										
							sMsg = "處理程序 12/12 - INF樣本匯出中，共 " & db.count & " 筆, 請稍後 !!! 匯出欄位 [" & field.name & "], 筆數進度 " & k & " / " & db.count 			
							dlgText "Text1", sMsg 			
						Next k				
					End If					
				End If						
		Next j								
	End If									
										
	'匯出60筆INF的所有欄位									
	Dim INFPath As String									
	INFPath = Client.WorkingDirectory  & "Exports.ILB\#INF Report#Sort.xlsx"									
	Call Z_ExportDatabaseXLSX("#INF Report#Sort.IDM", "#INF Report#Sort.xlsx" , "可靠性樣本_所有欄位")									
										
	Set AllRecords = oBook.Worksheets.Item("可靠性樣本_所有欄位")									
	Set oBook2=sExcel.Workbooks.Open(INFPath)									
	Set oSheet2=oBook2.Worksheets.item("可靠性樣本_所有欄位")									
	Set oRange=oSheet2.UsedRange									
	oRange.Copy Destination:=AllRecords.Range("A1")									
	'AllRecords.Paste
	sExcel.CutCopyMode = False									
	oBook2.Save									
	oBook2.Close (True)									
										
	Set oSheet2 = Nothing									
	Set oBook2 = Nothing									
	Kill INFPath 									
										
										
	fs = Nothing									
										
	Set oSheet = oBook.Worksheets.Item("INF Testing 可靠性測試")									
										
	oBook.Save									
	oBook.Close (True)									
	sExcel.Quit									
	Set oRange = Nothing									
	Set oSheet = Nothing									
	Set AllRecords = Nothing									
	Set oBook = Nothing									
	Set sExcel=Nothing									
										
	sMsg = "Processing Done..."									
	dlgText "CriteriaMsgText", sMsg 									
										
	Exit Function									
										
	ErrorHandler:									
		MsgBox "An error occurred: " & Err.Description								
		' Clean up resources								
		sExcel.Quit								
		If Not oBook2 Is Nothing Then oBook2.Close True								
		Kill INFPath								
		Set oSheet2 = Nothing								
		Set oBook2 = Nothing								
		Set oRange = Nothing								
		Set oSheet = Nothing								
		Set AllRecords = Nothing								
		Set oBook = Nothing								
		Set sExcel=Nothing								
										
End Function										



Function Step2_Upload_AccountMapping_File

	Dim obj As Object
	Dim S1, S2, S3 As String
	Dim Count, i , p As Integer 
	
	filename = ""
	
	ChoseMappingFile :

		sFilename = "Not_OK"

		Set obj = Client.CommonDialogs
			filename = obj.FileOpen("",Client.WorkingDirectory  & "Exports.ILB\AccountMapping.xlsx","XLSX Files (*.XLSX)|*.XLSX|All Files (*.*)|*.*||;")
		Set obj = Nothing	
		
		'Set fs = CreateObject("Scripting.FileSystemObject")
		
		'If (Not fs.FileExists(filename)) Then 
		If filename = "" Then 
			sMsg = "你所選擇的默認文件檔不存在，是否要再次重新選擇 ?"
			Result = MsgBox( sMsg , MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL,"步驟二 檔案不存在 警示訊息")
			If Result = IDYES Then
				GoTo ChoseMappingFile
			Else 
				Exit Function
			End If
		
	'	ElseIf filename = "" Then 
	'		sMsg = "你真的要取消上傳會計科目配對文件嗎?"
	'		Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL,"步驟二 警示訊息")
	'		If Result = IDYES Then
	'			Exit Function
	'		Else 
	'			GoTo ChoseMappingFile
	'		End If
				
		Else
			strr= filename
			dstr= Client.WorkingDirectory  +"Other.ILB\AccountMapping.XLSX"
			FileCopy strr, dstr 
			
			Dim pic As Shape
			
			Set sExcel = CreateObject("Excel.Application")
			Set oBook = sExcel.Workbooks.Open(dstr)
			
			'檢查AccountMapping Worksheet 是否存在
			count  = oBook.Sheets.Count
			p = 0 
			For i = 1 To count
				Set oSheet = oBook.Worksheets.Item(i)
				If oSheet.Name = "AccountMapping" Then P = 1
			Next i
			
			If P <> 1 Then
				
				oBook.save
				oBook.Close (True)
				sExcel.Quit
				Set oSheet = Nothing
				Set oBook = Nothing
				Set sExcel=Nothing

				sMsg = "你似乎選擇了錯誤的文件，是否要重新選擇?"
				Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟二 上傳檔案錯誤 警示訊息")
				If Result = IDYES Then
					GoTo ChoseMappingFile
				Else 
					Exit Function
				End If
			End If
				
			Set oSheet = oBook.Worksheets.Item("AccountMapping")
			
			ProtectNo = "JETeamSti11Tried"
			oSheet.Protect ProtectNo, DrawingObjects:=False, Contents:=False, Scenarios:=False
			oSheet.Cells.Locked = False   '解鎖'
			

			 '清除Excel 檔中的圖片
			 For Each pic In oSheet.Shapes
			 	pic.Delete		
			 Next

			oSheet.Range("A1:C2").Delete 
						
			S1 = oSheet.Range("A1").value
			S2 = oSheet.Range("B1").value
			S3 = oSheet.Range("C1").value
			
			oBook.save
			oBook.Close (True)
			sExcel.Quit
			Set oSheet = Nothing
			Set oBook = Nothing
			Set sExcel=Nothing
						
			'檢查匯入的欄位是否正確
			If S1 <> "GL_Number" Or S2 <> "GL_Name" Or S3 <> "Standardized Account Name*" Then
				sMsg = "你似乎選擇了錯誤的文件，是否要重新選擇?"
				Result = MsgBox(sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟二 上傳檔案錯誤 警示訊息")
				If Result = IDYES Then
					GoTo ChoseMappingFile
				Else 
					Exit Function
				End If
			End If
				
			'匯入 Accounting Mapping File 
			Set task = Client.GetImportTask("ImportExcel")
			dbName = dstr
			task.FileToImport = dbName
			task.SheetToImport = "AccountMapping"
			task.OutputFilePrefix = "#AccountMapping#"
			task.FirstRowIsFieldName = "TRUE"
			task.EmptyNumericFieldAsZero = "FALSE"
			task.PerformTask
			dbName = task.OutputFilePath("AccountMapping")
			Set task = Nothing
			Client.OpenDatabase(dbName)	
			
			sFilename = "OK"	
				
		End If
End Function

Function initialWeekend_Dlg(ControlID$, Action%, SuppValue%)

	Dim bExitFunction As Boolean
	Dim S As String
	Dim Mon As  String 
	Dim Tue As String 
	Dim Wed As String 
	Dim Thur As String
	Dim Fri As String 
	Dim Sat As String 
	Dim Sun As String 
	Dim i  As Integer
            
	Select Case Action%
                
               	Case 1
	               		 
			DlgValue "ChkBoxMon", 0
			DlgValue "ChkBoxTue", 0
			DlgValue "ChkBoxWed", 0
			DlgValue "ChkBoxThur", 0
			DlgValue "ChkBoxFri", 0
			DlgValue "ChkBoxSat",1
			DlgValue "ChkBoxSun",1
			Mon = "N"
			Tue = "N"
			Wed = "N" 
			Thur = "N"
			Fri = "N"
			Sat = "Y"
			Sun = "Y"
			
		Case 2
                		Select Case ControlID$
					Case "BtnSelect"
					
					If weekendselection.ChkBoxMon Then 
						Mon = "Y"
					Else 
						Mon = "N"
					End If
					If weekendselection.ChkBoxTue Then 
						Tue = "Y"
					Else 
						Tue = "N"
					End If
					If weekendselection.ChkBoxWed Then 
						Wed = "Y"
					Else 
						Wed = "N"
					End If
					If weekendselection.ChkBoxThur Then 
						Thur = "Y"
					Else 
						Thur = "N"
					End If
					If weekendselection.ChkBoxFri Then 
						Fri = "Y" 
					Else 
						Fri = "N"
					End If
					If weekendselection.ChkBoxSat Then 
						Sat = "Y"
					Else 
						Sat = "N"
					End If
					If weekendselection.ChkBoxSun Then 
						Sun = "Y"
					Else 
						Sun = "N"
					End If
					
					Call Z_Delete_File("#Weekend.IDM")
					Call X_Create_Weekend_Table
					Call X_Append_Weekend("Monday",Mon)
					Call X_Append_Weekend("Tuesday",Tue)
					Call X_Append_Weekend("Wednesday",Wed)
					Call X_Append_Weekend("Thursday",Thur)
					Call X_Append_Weekend("Friday",Fri)
					Call X_Append_Weekend("Saturday",Sat)
					Call X_Append_Weekend("Sunday",Sun)
					
					bExitFunction = True
					
					Case "BtnCancel"
						Call Z_Delete_File("#Weekend.IDM")
						
					bExitFunction = True
			End Select
					
	End Select

	If  bExitFunction Then
		initialWeekend_Dlg = 0
	Else 
		initialWeekend_Dlg = 1
	End If
                
End Function


Function X_Create_Weekend_Table	
	Set table = Client.NewTableDef 

	Set field1 = table.NewField
	Set field2 = table.NewField

	field1.Name ="DayOfWeek"
	field2.Name ="WorkDay"

	field1.Type =	WI_EDIT_CHAR
	field2.Type =	WI_EDIT_Char

	field1.Length = 10
	field2.Length = 1

	field1.Protected = FALSE 
	field2.Protected = FALSE 

	table.AppendField field1
	table.AppendField field2
	
	Set db = Client.NewDatabase ("#Weekend", "", table) 
	db.CommitDatabase 
	
	Set db = Nothing 
End Function

Function X_Append_Weekend(DOW As String, Works As String)

	Dim i As Integer, s As String, m As Long, n As Double
	Dim db As Variant, rs As Variant, rec As Variant
	
	Set db = Client.OpenDatabase("#Weekend.IDM")
	Set rs = db.RecordSet	
	Set rec = rs.NewRecord

	Set table = db.TableDef
	Set field1 = table.GetField("DayOfWeek")
	Set field2 = table.GetField("WorkDay")
	field1.Protected = False
	field2.Protected = False

	rec.SetCharValue "DayOfWeek" , DOW
	rec.SetCharValue "WorkDay" , Works
	rs.AppendRecord(rec)

	field1.Protected = True
	field2.Protected = True
				
	db.CommitDatabase	
	db.Close
	
	Set rs = Nothing
	Set rec = Nothing
	Set db = Nothing
	
End Function

Function Step2_Upload_Holiday_File

	Dim obj As Object
	Dim S1, S2, S3 As String
	Dim Count, i , p As Integer 
	
	filename1 = ""
	
	ChoseMappingFile :

		sFilename1 = "Not_OK"

		Set obj = Client.CommonDialogs
		filename1 = obj.FileOpen("",Client.WorkingDirectory  & "Exports.ILB\Holiday.xlsx","XLSX Files (*.XLSX)|*.XLSX|All Files (*.*)|*.*||;")
		Set obj = Nothing		
		
		If filename1 = "" Then 
			sMsg = "你真的想取消上傳假日設定文件檔嗎?"
			Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟二 是否確定取消作業 ")
				If Result = IDYES Then
					Exit Function
				Else 
					GoTo ChoseMappingFile
				End If
		Else
		
			strr= filename1
			dstr= Client.WorkingDirectory  +"Other.ILB\Holiday.XLSX"
			'Kill dstr
			FileCopy strr, dstr 
			
			Dim pic As Shape
			
			Set sExcel = CreateObject("Excel.Application")
			Set oBook = sExcel.Workbooks.Open(dstr)
			
			'檢查AccountMapping Worksheet 是否存在
			count  = oBook.Sheets.Count
			p = 0 
			For i = 1 To count
				Set oSheet = oBook.Worksheets.Item(i)
				If oSheet.Name = "Holiday" Then P = 1
			Next i
			
			If P <> 1 Then
				sMsg = "你似乎選擇了錯誤的文件，是否要重新選擇?"
				Result = MsgBox(sMsg , MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟二 上傳檔案錯誤 警示訊息")
				If Result = IDYES Then
					oBook.save
					oBook.Close (True)
					sExcel.Quit
					Set oSheet = Nothing
					Set oBook = Nothing
					Set sExcel=Nothing
					GoTo ChoseMappingFile
				Else 
					oBook.save
					oBook.Close (True)
					sExcel.Quit
					Set oSheet = Nothing
					Set oBook = Nothing
					Set sExcel=Nothing
					Exit Function
				End If
			End If
				
			Set oSheet = oBook.Worksheets.Item("Holiday")

			 '清除Excel 檔中的圖片
			 For Each pic In oSheet.Shapes
			 	pic.Delete
			 Next

			oSheet.Range("A1:C1").Delete 
						
			S1 = oSheet.Range("A1").value
			S2 = oSheet.Range("B1").value
			S3 = oSheet.Range("C1").value
			
			oBook.save
			oBook.Close (True)
			sExcel.Quit
			Set oSheet = Nothing
			Set oBook = Nothing
			Set sExcel=Nothing
			
			'檢查匯入的欄位是否正確
			If S1 <> "Date_of_Holiday" Or S2 <> "Holiday_Name" Or S3 <> "IS_Holiday" Then
				sMsg = "你似乎選擇了錯誤的文件，是否要重新選擇?"
				Result = MsgBox(sMsg , MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟二 上傳檔案錯誤 警示訊息")
				If Result = IDYES Then
					GoTo ChoseMappingFile
				Else 
					Exit Function
				End If
			End If

			Call Z_Delete_File("#Holiday#-Holiday.IDM")
							
			'匯入 Accounting Mapping File 
			Set task = Client.GetImportTask("ImportExcel")
			dbName = dstr
			task.FileToImport = dbName
			task.SheetToImport = "Holiday"
			task.OutputFilePrefix = "#Holiday#"
			task.FirstRowIsFieldName = "TRUE"
			task.EmptyNumericFieldAsZero = "FALSE"
			task.PerformTask
			dbName = task.OutputFilePath("Holiday")
			Set task = Nothing
			Client.OpenDatabase(dbName)	
			
			sFilename1 = "OK"	

			Call Z_Field_Info("#Holiday#-Holiday.IDM", "DATE_OF_HOLIDAY")
			If  sType <> "WI_VIRT_DATE" Then 
				sMsg = "匯入IDEA中的日期欄位系統辨識為文字型態，請檢查原始檔案以確認是否有輸入錯誤之日期或是欄位為空值"
				Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟二 檔案上傳錯誤 警示訊息")
				Call Z_Delete_File("#Holiday#-Holiday.IDM")	
			End If
			
			If Z_File_Exist("#Holiday#-Holiday.IDM") Then 
				If Z_DataField_Check("#Holiday#-Holiday.IDM", "DATE_OF_HOLIDAY") <> 0 Then
					sMsg = "匯入IDEA中的日期經檢查有錯誤，請檢查原始檔案以確認是否有輸入錯誤之日期"
					Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟二 檔案上傳錯誤 警示訊息")
					Call Z_Delete_File("#Holiday#-Holiday.IDM")			
				End If
			End If
										
		End If

End Function


Function Step2_Upload_MakeUpday_File

	Dim obj As Object
	Dim S1, S2, S3 As String
	Dim Count, i , p As Integer 
	
'	filename1 = ""
	
	ChoseMappingFile :

'		sFilename1 = "Not_OK"

		Set obj = Client.CommonDialogs
		filename1 = obj.FileOpen("",Client.WorkingDirectory  & "Exports.ILB\Make-Up_Day.xlsx","XLSX Files (*.XLSX)|*.XLSX|All Files (*.*)|*.*||;")
		Set obj = Nothing		
		
		If filename1 = "" Then 
			sMsg = "你真的想取消上傳補班日或結帳日設定文件檔嗎?"
			Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟二 是否取消檔案上傳作業")
				If Result = IDYES Then
					Exit Function
				Else 
					GoTo ChoseMappingFile
				End If
		Else
		
			strr= filename1
			dstr= Client.WorkingDirectory  +"Other.ILB\MakeUpDay.XLSX"
			'Kill dstr
			FileCopy strr, dstr 
			
			Dim pic As Shape
			
			Set sExcel = CreateObject("Excel.Application")
			Set oBook = sExcel.Workbooks.Open(dstr)
			
			'檢查AccountMapping Worksheet 是否存在
			count  = oBook.Sheets.Count
			p = 0 
			For i = 1 To count
				Set oSheet = oBook.Worksheets.Item(i)
				If oSheet.Name = "Make-Up_Day" Then P = 1
			Next i
			
			If P <> 1 Then
				sMsg = "你似乎選擇了錯誤的文件，是否要重新選擇?"
				Result = MsgBox(sMsg , MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟二 檔案上傳錯誤 警示訊息")
				If Result = IDYES Then
					oBook.save
					oBook.Close (True)
					sExcel.Quit
					Set oSheet = Nothing
					Set oBook = Nothing
					Set sExcel=Nothing
					GoTo ChoseMappingFile
				Else 
					oBook.save
					oBook.Close (True)
					sExcel.Quit
					Set oSheet = Nothing
					Set oBook = Nothing
					Set sExcel=Nothing
					Exit Function
				End If
			End If
				
			Set oSheet = oBook.Worksheets.Item("Make-Up_Day")

			 '清除Excel 檔中的圖片
			 For Each pic In oSheet.Shapes
			 	pic.Delete
			 Next

			oSheet.Range("A1:B1").Delete 
						
			S1 = oSheet.Range("A1").value
			S2 = oSheet.Range("B1").value
			'S3 = oSheet.Range("C1").value
			
			oBook.save
			oBook.Close (True)
			sExcel.Quit
			Set oSheet = Nothing
			Set oBook = Nothing
			Set sExcel=Nothing
			
			'檢查匯入的欄位是否正確
			If S1 <> "Date_of_MakeUpDay" Or S2 <> "MakeUpDay_Desc" Then
				sMsg = "你似乎選擇了錯誤的文件，是否要重新選擇?"
				Result = MsgBox(sMsg , MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "步驟二 檔案上傳錯誤 警示訊息")
				If Result = IDYES Then
					GoTo ChoseMappingFile 
				Else 
					Exit Function
				End If
			End If

			Call Z_Delete_File("#MakeUpDay#-Make-Up_Day.IDM")
							
			'匯入 Accounting Mapping File 
			Set task = Client.GetImportTask("ImportExcel")
			dbName = dstr
			task.FileToImport = dbName
			task.SheetToImport = "Make-Up_Day"
			task.OutputFilePrefix = "#MakeUpDay#"
			task.FirstRowIsFieldName = "TRUE"
			task.EmptyNumericFieldAsZero = "FALSE"
			task.PerformTask
			dbName = task.OutputFilePath("Make-Up_Day")
			Set task = Nothing
			Client.OpenDatabase(dbName)	
			
'			sFilename1 = "OK"	
			
			Call Z_Field_Info("#MakeUpDay#-Make-Up_Day.IDM", "Date_of_MakeUpDay")
			If  sType <> "WI_VIRT_DATE" Then 
				sMsg = "匯入IDEA中的日期欄位系統辨識為文字型態，請檢查原始檔案以確認是否有輸入錯誤之日期或是欄位為空值"
				Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟二 檔案上傳錯誤 警示訊息")
				Call Z_Delete_File("#MakeUpDay#-Make-Up_Day.IDM")	
			End If
			
			If Z_File_Exist("#MakeUpDay#-Make-Up_Day.IDM") Then 
				If Z_DataField_Check("#MakeUpDay#-Make-Up_Day.IDM", "Date_of_MakeUpDay") <> 0 Then
					sMsg = "匯入IDEA中的日期經檢查有錯誤，請檢查原始檔案以確認是否有輸入錯誤之日期"
					Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟二 檔案上傳錯誤 警示訊息")
					Call Z_Delete_File("#MakeUpDay#-Make-Up_Day.IDM")			
				End If
			End If
			
		End If

End Function


Function Step2_Check_Required

	Set db = Client.OpenDatabase("#AccountMapping.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "STANDARDIZED_ACCOUNT_NAME"
	dbName = "#AccountMapping_Sum.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)

	P=0
	q = 0
	Set db = Client.OpenDatabase("#AccountMapping_Sum.IDM")
	Set ThisTable = db.TableDef
	Set field = ThisTable.GetFieldAt (1) 
		count = db.count
		Set rs = db.RecordSet
			rs.ToFirst
			For i = 1 To count
				rs.Next
				Set rec = rs.ActiveRecord
					
				If rec.GetCharValueAt (1) = "Revenue" Then    p = p + 1
				If rec.GetCharValueAt (1) = "Cash" Or rec.GetCharValueAt (1) = "Receipt in advance"  Or rec.GetCharValueAt (1) = "Receivables"  Then    q = q + 1
				Next i
			Set rec = Nothing
			Set rs = Nothing
		Set db = Nothing
	Client.OpenDatabase (dbName)
	'Derek 20180820 Modify
	
	If GetTotal("#AccountMapping.IDM" ,"" ,"DBCount" ) <> 0 Then 
		Call Z_Field_Info("#AccountMapping#-AccountMapping.IDM", "STANDARDIZED_ACCOUNT_NAME")

			Set db = Client.OpenDatabase("#AccountMapping#-AccountMapping.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "TEMP"
			field.Description = ""
			field.Type = WI_CHAR_FIELD
			field.Equation = "@If(STANDARDIZED_ACCOUNT_NAME<> """", STANDARDIZED_ACCOUNT_NAME,""Others"")"
			If sLen <= 6 Then 
				field.Length = 6
			Else
				field.Length = sLen
			End If 
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
			
		Call X_RemoveField("#AccountMapping#-AccountMapping.IDM", "STANDARDIZED_ACCOUNT_NAME")
		If FindField("#AccountMapping#-AccountMapping.IDM", "COL4") <> 0 Then Call X_RemoveField("#AccountMapping#-AccountMapping.IDM", "COL4")
		Call Z_renameFields("#AccountMapping#-AccountMapping.IDM", "TEMP", "STANDARDIZED_ACCOUNT_NAME")
		
		Call Z_Rename_DB("#AccountMapping#-AccountMapping.IDM", "#AccountMapping.IDM")
			
	End If
	
	If GetTotal("#AccountMapping_Sum.IDM" ,"" ,"DBCount" ) <> 0 Then 

		Set db = Client.OpenDatabase("#AccountMapping_Sum.IDM")
		Set task = db.Extraction
		task.IncludeAllFields
		dbName = "#AccountMapping_R.IDM"
		task.AddExtraction dbName, "", " STANDARDIZED_ACCOUNT_NAME  ==  ""Revenue"" "
		task.CreateVirtualDatabase = False
		task.PerformTask 1, db.Count
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)
		
		Set db = Client.OpenDatabase("#AccountMapping_Sum.IDM")
		Set task = db.Extraction
		task.IncludeAllFields
		dbName = "#AccountMapping_C.IDM"
		task.AddExtraction dbName, "", "(STANDARDIZED_ACCOUNT_NAME  == " & Chr(34) & "Receivables"  & Chr(34) &  " .OR.  STANDARDIZED_ACCOUNT_NAME  == "  & Chr(34) &  "Cash"  & Chr(34) &  " .OR.   STANDARDIZED_ACCOUNT_NAME  == "  & Chr(34) &  "Receipt in advance"  & Chr(34) &  ")" 
		task.CreateVirtualDatabase = False
		task.PerformTask 1, db.Count
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)			
	
	End If 
	
	If p = 1 And q = 0 Then 
		amountTotal = 0
	Else
		amountTotal = 1
	End If 
End Function


Function Z_Modidy_Field_Num_to_Char(sTempFilename As String, sOldFieldName As String, sNewFieldName As String)  As Boolean
	
	amountTotal =  iLen(iInt(GetTotal(sTempFilename, sOldFieldName, "MaxValue")))
 	
	Set db = Client.OpenDatabase(sTempFilename)
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	Set oldfield = db.TableDef.GetField(sOldFieldName)
	field.Name = sOldFieldName
	'field.Description = sOldFieldName & "- Num to Chr" & amountTotal
	field.Description = oldfield.Description
	field.Type = WI_CHAR_FIELD
	field.Equation = ""
	field.Length = amountTotal 
	task.ReplaceField sOldFieldName, field
	task.PerformTask
	Set oldfield = Nothing
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing
	
	Set db = Client.OpenDatabase(sTempFilename)
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	Set oldfield = db.TableDef.GetField(sOldFieldName)
	field.Name = sNewFieldName
	'field.Description = sOldFieldName
	field.Description = oldfield.Description
	field.Type = WI_CHAR_FIELD
	field.Equation = sOldFieldName
	field.Length = amountTotal
	task.AppendField field
	task.PerformTask
	Set oldfield = Nothing
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing	

End Function


Function Step1_Append_IsManual
	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "人工傳票否_JE_S"
	field.Description = ""
	field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
	field.Equation = "0"
	field.Decimals = 0
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing
End Function


Function Step1_Check_User_Defind_Manual(sTempFilename As String, sOldFieldName As String)  As Boolean
	
	On Error GoTo ErrorHandler
	Set db = Client.OpenDatabase(sTempFilename)
	Set task = db.Summarization
	task.AddFieldToSummarize sOldFieldName
	task.AddFieldToTotal sOldFieldName
	dbName = "#GL_Temp_Maunal_Check.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.StatisticsToInclude = SM_SUM
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)	

	P=0
	Q = 0 
	Set db = Client.OpenDatabase("#GL_Temp_Maunal_Check.IDM")
	Set ThisTable = db.TableDef
	Set field = ThisTable.GetFieldAt (1) 
		count = db.count
		Set rs = db.RecordSet
			rs.ToFirst
			For i = 1 To count
				rs.Next
				Set rec = rs.ActiveRecord
					
				If rec.GetNumValueAt (1) = 1 Or rec.GetNumValueAt (1) = 0 Then
					P = P + 1
				Else
					Q = Q +1
				End If 

				Next i

	If (P=2 Or p = 1 ) And Q = 0 Then 
		Step1_Check_User_Defind_Manual = true
		GoTo ReleaseVariables
	Else 
		GoTo ErrorHandler		
	End If 
	
ErrorHandler:	
	 Step1_Check_User_Defind_Manual = false
	
ReleaseVariables:
	Set rec = Nothing
	Set rs = Nothing
	Set db = Nothing			
	
End Function

Function Z_WorkbookOpen(sWorkBookName As String) As Boolean
    Dim oExcel As Object
    Dim wb As Object
    Dim isOpen As Boolean
    Dim excelWasNotRunning As Boolean
    
    On Error Resume Next
    Set oExcel = GetObject("","Excel.Application")
    If Err.Number <> 0 Then
        Set oExcel = CreateObject("Excel.Application")
        excelWasNotRunning = True
    End If
    On Error GoTo 0

    isOpen = False
    For Each wb In oExcel.Workbooks
        If LCase(wb.Name) = LCase(sWorkBookName) Then
            isOpen = True
            Exit For
        End If
    Next wb
    
    Z_WorkbookOpen = isOpen
    
    ' 只有在Excel應用程式是由此代碼創建時才關閉它
    If excelWasNotRunning Then
        oExcel.Quit
    End If

    Set wb = Nothing
    Set oExcel = Nothing
End Function



Function StepN_Excel_File_Check(sStepTemp As String)

	S1Check = "0"
	Set fs = CreateObject("Scripting.FileSystemObject")

	If sStepTemp = "STEP1" Then 	

		strr= sTempExcelSource & "\ValidationReport.xlsx"   ' "Exports.ILB\ValidationReport.xlsx"

		If Not fs.FileExists(strr) Then 
			S1Check = 1
			sMsg = "完整性驗證報告範本檔(Validation Template Report)不存在，請聯繫系統管理員!"
			Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一 範本檔不存在 警示訊息")
		End If 

		strr= sTempExcelSource & "\AccountMapping.xlsx"   ' "Exports.ILB\ValidationReport.xlsx"

		If Not fs.FileExists(strr) Then 
			S1Check = 1
			sMsg = "會計科目類別設定範本檔(Account Mapping template file)不存在，請聯繫系統管理員!"
			Result = MsgBox( sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一 範本檔不存在 警示訊息")
		End If 

		strr= sTempExcelSource & "\INFReport.xlsx"   ' "Exports.ILB\ValidationReport.xlsx"

		If Not fs.FileExists(strr) Then 
			S1Check = 1
			sMsg = "INF測試範本檔(INF testing template file)不存在，請聯繫系統管理員!"
			Result = MsgBox( sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟一 範本檔不存在 警示訊息")
		End If 

		'If Z_WorkbookOpen("ValidationReport.xlsx") = True Then 
		'	S1Check = "1"
		'	sMsg = "完整性驗證報告文件(Validation Report)似乎仍然開啟，請關閉開啟之檔案後再試一次"
		'	Result = MsgBox( sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一 檔案開啟 警示訊息")
		'End If 
					
		'If Z_WorkbookOpen("INFReport.xlsx") = True Then 
		'	S1Check = "1"
		'	sMsg = "INF Excel文件(INF testing Report)似乎仍然開啟，請關閉開啟之檔案後再試一次"
		'	Result = MsgBox(sMsg , MB_OK Or MB_ICONEXCLAMATION , "步驟一 檔案開啟 警示訊息")
		'End If 

		'If Z_WorkbookOpen("AccountMapping.xlsx") = True Then 
		'	S1Check = "1"
			sMsg =  "會計科目類別設定檔案(Account Mapping file)似乎仍然開啟，請關閉開啟之檔案後再試一次"
		'	Result = MsgBox( sMsg ,MB_OK Or MB_ICONEXCLAMATION , "步驟一 檔案開啟 警示訊息")
		'End If
			
	End If 
	
	If sStepTemp = "STEP2" Then 
		'If Z_WorkbookOpen("Holiday.xlsx") = True Then 
		'	S1Check = "1"
		'	sMsg = "假日設定檔案似乎仍然開啟，請關閉開啟之檔案後再試一次"
		'	Result = MsgBox( sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟二 檔案開啟 警示訊息")
		'End If
		
		'If Z_WorkbookOpen("Make-Up_Day.xlsx") = True Then 
		'	S1Check = "1"
		'	sMsg = "補班日/結帳日設定檔案似乎仍然開啟，請關閉開啟之檔案後再試一次"
		'	Result = MsgBox( sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟二 檔案開啟 警示訊息")
		'End If
	End If

	If sStepTemp = "STEP3" Then 
		
		strr= sTempExcelSource & "\PrescreeningReport.xlsx"   ' "Exports.ILB\ValidationReport.xlsx"
		If Not fs.FileExists(strr) Then 
			S1Check = "1"
			sMsg = "預篩選報告範本檔(Pre-screening template report)不存在，請聯繫系統管理員 !"
			Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟三 範本檔不存在 警示訊息")
		End If 
		'If Z_WorkbookOpen("PrescreeningReport.xlsx") = True Then 
		'	S1Check = "1"
		'	sMsg = "預篩選文件(Pre-screening report)似乎仍然開啟，請關閉開啟檔案後再試一次"
		'	Result = MsgBox(sMsg ,MB_OK Or MB_ICONEXCLAMATION , "步驟三 檔案開啟 警示訊息")
		'End If
	End If

	If sStepTemp = "STEP4" Then 
		
		strr= sTempExcelSource & "\CriteriaSelectionReport.xlsx"  
		If Not fs.FileExists(strr) Then 
			S1Check = "1"
			sMsg = "篩選後報告範本檔(criteria selection template report)不存在，請聯繫系統管理員！"
			Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟四 範本檔不存在 警示訊息")
		End If 
		'If Z_WorkbookOpen("CriteriaSelectionReport.xlsx") = True Then 
		'	S1Check = "1"
		'	sMsg = "篩選後報告文件(criteria selection report)似乎仍然開啟，請關閉後再試一次"
		'	Result = MsgBox(sMsg ,MB_OK Or MB_ICONEXCLAMATION , "步驟四 檔案開啟 警示訊息")
		'End If
	End If
			
	If sStepTemp = "STEP5" Then 
		
		strr= sTempExcelSource & "\WorkingPaper.xlsx"  
		If Not fs.FileExists(strr) Then 
			S1Check = "1"
			sMsg = "工作底稿報告範本檔(Working paper template report)不存在，請聯繫系統管理員！"
			Result = MsgBox(sMsg,MB_OK Or MB_ICONEXCLAMATION , "步驟五 範本檔不存在 警示訊息")
		End If 
		'If Z_WorkbookOpen("WorkingPaper.xlsx") = True Then 
		'	S1Check = "1"
		'	sMsg = "JE工作底稿報告(JE Working Paper)似乎仍然開啟，請關閉開啟檔案後再試一次"
		'	Result = MsgBox( sMsg ,MB_OK Or MB_ICONEXCLAMATION , "步驟五 檔案開啟 警示訊息")
		'End If
	End If
			
	Set fs = Nothing		
			
End Function

Function FindField(sFileName As String, sFindField As String )

	Dim db As Datebase
	Dim table As tableDef
	Dim field As field
	Dim i As Integer

	Set db = Client.OpenDatabase(sFileName)
	Set table = db.TableDef
	For i = 1 To table.Count
		Set field = table.GetFieldAt(i)
		If Field.Name = sFindField  Then 
			FindField = 1
			Exit Function
		End If
	Next i

	Set field = Nothing
	Set table = Nothing
	Set db = Nothing

	FindField = 0
	
End Function




Function Step3_Routines

	Dim WeekField As String
	Dim HolidayField As String
		
	WeekField = "0"
	HolidayField = "0"
	
	'DlgEnable "BtnOK" ,  0
	'DlgEnable "BtnCancel" ,  0
	
	sFilename = ""
	sFilename = ""
	
	S1Check =  0
	sStep4_Rec_info = ""
	
	Call Step3_Routines_Info_Reset
	
	'sTemp = iRemove(iAllTrim( iUpper(iReplace(Routines.TextBoxRout4,",","|")))," ")
	'If iRight(sTemp,1) = "|" Then 
	'	sTemp = iLeft(sTemp, Len(sTemp) -1)
	'End If

	'sTemp = iRemove(sTemp, "|")
	
	'For j = 1 To Len(sTemp)
	'	sChar = iAllTrim(Mid(sTemp, j, 1))
	'	If iFindOneOf(sChar, "0123456789") = 0 Then
	'		DlgEnable "BtnOK" ,  1
	'		DlgEnable "BtnCancel" ,  1
	'		sMsg = "額外測試項目-尾數測試作業僅接受輸入數字與逗號(,)"
	'		Result = MsgBox( sMsg ,MB_OK Or MB_ICONEXCLAMATION , "步驟三 額外測試項目輸入錯誤 警示訊息")			
	'		S1Check = 1
	'		Exit Function
	'	End If
	'Next j
	
	'sMsg = "處理程序 1/15 - 前處理作業，檢查並新增欄位"
	'dlgText "RoutineMsgText", sMsg 
		
'	If sPopulation = 0 Then 
'		sTemp = " Posting_Date_JE  >= "& Chr(34) & iRemove(sLast_Accounting_Period_Date, "/") & Chr(34)
'		Call Z_DirectExtractionTable("#GL#In_Period.IDM", "#Temp1.IDM", sTemp)
'		Call Z_Delete_file("#GL#In_Period.IDM")
'		Call Z_Rename_DB("#Temp1.IDM","#GL#In_Period.IDM")
'		Call Z_Delete_file("#Temp1.IDM")
'	End If 

	If Z_File_Exist("#Null-GL_Description_Criteria.IDM") <> True Then Call getNulls("#GL#.IDM", "傳票摘要_JE", "#Null-GL_Description_Criteria.IDM")	
	
	'新增欄位考量
        '借方金額 For R5, R6
	If FindField("#GL#.IDM","DEBIT_傳票金額_JE_T") = 0 Then 
		Call Z_Field_Info("#GL#.IDM", "傳票金額_JE")
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "DEBIT_傳票金額_JE_T"
		field.Description = ""
		field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
		field.Equation = "@If( 傳票金額_JE >= 0,  傳票金額_JE , 0 )"
		field.Decimals = sDecimals    
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing         

		'sMsg = "Processing - Check and Appending Field [CREDIT_傳票金額_JE_T]"
		'dlgText "Text1", sMsg 
        
		'貸方金額 For R5, R6
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "CREDIT_傳票金額_JE_T"
		field.Description = ""
		field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
		field.Equation = "@If( 傳票金額_JE < 0,  傳票金額_JE , 0 )"
		field.Decimals = sDecimals    
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
		
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "DEBIT_CREDIT_JE_T"
		field.Description = ""
		field.Type = WI_CHAR_FIELD
		field.Equation = "@If( 傳票金額_JE >=0,""DEBIT"",""CREDIT"")"
		field.Length = 6
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
		
	End If 
	
	'sMsg = "處理程序 2/15 - 產生測試作業用暫存檔"
	'dlgText "Text1", sMsg 

	Call Z_Delete_File("#GL#Sum_By_Doc.IDM")
	Call Z_Delete_File("#GL#Sum_By_Doc_Line.IDM")
	Call Z_Delete_File("#Temp1.IDM")
	
	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "傳票號碼_JE"
	If FindField("#GL#.IDM","傳票核准日_JE") <> 0 Then  
		task.AddFieldToSummarize "傳票核准日_JE"
	End If 
	If FindField("#GL#.IDM","總帳日期_JE") <> 0 Then  
		task.AddFieldToSummarize "總帳日期_JE"
	End If
	task.AddFieldToTotal "傳票金額_JE"
	dbName = "#GL#Sum_By_Doc.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "傳票號碼_JE"
	task.AddFieldToSummarize "傳票文件項次_JE_S"
	dbName = "#GL#Sum_By_Doc_Line.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)	
	
	'sMsg = "處理程序 3/15 - 處理假日與周末日資料"
	'dlgText "Text1", sMsg 
		
	' DocDate find weekday For R7
	'1 Sun. , 2 Mon. , 3 Tues. , 4 Wed. , 5 Thurs. , 6 Fri. , 7 Sat 
	If FindField("#GL#Sum_By_Doc.IDM","傳票核准日_JE") <> 0 Then  
		Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "DOCDATE_WEEK"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation = " @CompIf(@Dow( 傳票核准日_JE) = 1,"  &  Chr(34)  & "Sunday" & Chr(34) & ", @Dow( 傳票核准日_JE) = 2 ,"  & Chr(34) & _
						"Monday" & Chr(34) & ", @Dow( 傳票核准日_JE) = 3 , " & Chr(34) & "Tuesday" & Chr(34) & ", @Dow( 傳票核准日_JE) =4 , " & _
						Chr(34) & "Wednesday" & Chr(34) & ", @Dow( 傳票核准日_JE) =5 , " & Chr(34) & "Thursday" & Chr(34) & ", @Dow( 傳票核准日_JE) = 6 , " & _
						Chr(34) & "Friday" &  Chr(34) & ", @Dow( 傳票核准日_JE) =7 , " & Chr(34) & "Saturday" & Chr(34) & " )"
		field.Length = 10
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
	End If 

	' PostDate find weekday For R7
	If FindField("#GL#Sum_By_Doc.IDM","總帳日期_JE") <> 0 Then  
		Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "POSTDATE_WEEK"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation = " @CompIf(@Dow( 總帳日期_JE) = 1,"  &  Chr(34)  & "Sunday" & Chr(34) & ", @Dow( 總帳日期_JE) = 2 ,"  & Chr(34) & _
						"Monday" & Chr(34) & ", @Dow( 總帳日期_JE) = 3 , " & Chr(34) & "Tuesday" & Chr(34) & ", @Dow(總帳日期_JE) =4 , " & _
						Chr(34) & "Wednesday" & Chr(34) & ", @Dow( 總帳日期_JE) =5 , " & Chr(34) & "Thursday" & Chr(34) & ", @Dow( 總帳日期_JE) = 6 , " & _
						Chr(34) & "Friday" &  Chr(34) & ", @Dow( 總帳日期_JE) =7 , " & Chr(34) & "Saturday" & Chr(34) & " )"
		field.Length = 10
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing        
	End If	
			
	'**********   Routine R1  *************
	
	' 若有Tag人工判斷條件，需加入
	'sMsg = "處理程序 4/15 - 處理預篩選程序【#1. 於期末財務報表準備日後核准之分錄】"
	'dlgText "RoutineMsgText", sMsg 
	
	If FindField("#GL#DESC.IDM","傳票核准日_JE") <> 0 Then 
		sTemp = " 傳票核准日_JE  >= "& Chr(34) & iRemove(sLast_Accounting_Period_Date, "/") & Chr(34)
		Call Z_DirectExtractionTable("#GL#.IDM", "#PreScr-R1.IDM", sTemp)
		amountTotal = GetTotal("#PreScr-R1.IDM" ,"" ,"DBCount" )
		If amountTotal = 0 Then 
			Call X_Update_Routines_Info("R1",0)
			Call X_Update_Routines_Info_Str("R1_Status","N/A")
			Call X_Update_Routines_Info_Str("R1_Result","沒有符合預篩選條件之明細項")
			
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "PRESCR_R1"
			field.Description = ""
			field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
			field.Equation =   Chr(34) &  "" &  Chr(34) 
			field.Length = 1
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
			
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "R1_Tag"
			field.Description = ""
			field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
			field.Equation =   Chr(34) &  "" &  Chr(34) 
			field.Length = 1
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
			
		Else
			Call X_Update_Routines_Info("R1",1)
			Call X_Update_Routines_Info_Str("R1_Status","V")
			
			Set db = Client.OpenDatabase("#PreScr-R1.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "PRESCR_R1"
			field.Description = ""
			field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
			field.Equation =   Chr(34) &  "Y" &  Chr(34) 
			field.Length = 1
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
	
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#PreScr-R1.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "PRESCR_R1"
			task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp1.IDM"
			task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
	
			Client.CloseDatabase ("#Temp1.IDM")
	
			Call Z_Delete_File("#GL#Sum_By_Doc.IDM")
	
			Set ProjectManagement = client.ProjectManagement
				ProjectManagement.RenameDatabase "#Temp1.IDM", "#GL#Sum_By_Doc.IDM"
			Set ProjectManagement = Nothing						       		
	
			
			'20181018 Add
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#PreScr-R1.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "PRESCR_R1"
			task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
			task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp1.IDM"
			task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
				
			Call Z_Delete_File("#GL#Sum_By_Doc_Line.IDM")
			Call Z_Rename_DB("#Temp1.IDM","#GL#Sum_By_Doc_Line.IDM")
			Result = Z_renameFields("#GL#Sum_By_Doc_Line.IDM","PRESCR_R1", "R1_Tag")
		
			Call Z_Rename_DB("#PreScr-R1.IDM","#PreScr-R1-All.IDM")
			
			Call Z_Delete_File("#PreScr-R1.IDM")
			
			Call X_Update_Routines_Info_Str("R1_Result","符合預篩選條件之明細項請詳 [R1] 工作表")
			
		End If
	Else
			Call X_Update_Routines_Info("R1",0)
			Call X_Update_Routines_Info_Str("R1_Status","N/A")
			Call X_Update_Routines_Info_Str("R1_Result","沒有設定傳票核准日欄位")	
	End If
	
	Call Z_Delete_File("#PreScr-R1.IDM")
	Call Z_Delete_File("#Temp1.IDM")     		

	'**********   Routine R2  *************
	
	'sMsg = "處理程序 5/15 - 處理預篩選程序【#2. 分錄摘要出現特定描述】"
	'dlgText "RoutineMsgText", sMsg 
	
	sTemp = "@RegExpr(  @AllTrim( @Upper(傳票摘要_JE)) , " & Chr(34) & "ADJ|REV|RECLASS|SUSPENSE|ERROR|WRONG|調整|迴轉|沖銷|重分類|避險|重編|錯誤|計畫外|預算外|调整|回转|冲销|重分类|避险|重编|错误|计画外|预算外" & Chr(34) &") "
	
	Call Z_DirectExtractionTable("#GL#.IDM", "#PreScr-R2.IDM", sTemp)
	amountTotal = GetTotal("#PreScr-R2.IDM" ,"" ,"DBCount" )
	If amountTotal = 0 Then
		Call X_Update_Routines_Info("R2",0) 
		Call X_Update_Routines_Info_Str("R2_Status","N/A")
		Call X_Update_Routines_Info_Str("R2_Result","沒有符合預篩選條件之明細項")
		
		Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "PRESCR_R2"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation =   Chr(34) &  "" &  Chr(34) 
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
		
		Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "R2_Tag"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation =   Chr(34) &  "" &  Chr(34) 
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
		
	Else
		Call X_Update_Routines_Info("R2",1)
		Call X_Update_Routines_Info_Str("R2_Status","V")
		Set db = Client.OpenDatabase("#PreScr-R2.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "PRESCR_R2"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation =   Chr(34) &  "Y" &  Chr(34) 
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing

		Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
		Set task = db.JoinDatabase
		task.FileToJoin "#PreScr-R2.IDM"
		task.IncludeAllPFields
		task.AddSFieldToInc "PRESCR_R2"
		task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
		task.CreateVirtualDatabase = False
		dbName = "#Temp1.IDM"
		task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)

		Client.CloseDatabase ("#Temp1.IDM")
			
		Call Z_Delete_File("#GL#Sum_By_Doc.IDM")

		Set ProjectManagement = client.ProjectManagement
			ProjectManagement.RenameDatabase "#Temp1.IDM", "#GL#Sum_By_Doc.IDM"
		Set ProjectManagement = Nothing
		

		'20181018 Add
		If Z_File_Exist("#GL#Sum_By_Doc_Line.IDM") Then
		
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#PreScr-R2.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "PRESCR_R2"
			task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
			task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp1.IDM"
			task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
			
			Call Z_Delete_File("#GL#Sum_By_Doc_Line.IDM")
			Call Z_Rename_DB("#Temp1.IDM","#GL#Sum_By_Doc_Line.IDM")
			Result = Z_renameFields("#GL#Sum_By_Doc_Line.IDM","PRESCR_R2", "R2_Tag")
	
		End If 			

		If sVer = "TW" Then Call Z_Rename_DB("#PreScr-R2.IDM","#PreScr-R2-All.IDM")

		Call Z_Delete_File("#PreScr-R2.IDM")
		
		Call X_Update_Routines_Info_Str("R2_Result","符合預篩選條件之明細項請詳 [R2] 工作表")
				
	End If 
	Call Z_Delete_File("#PreScr-R2.IDM")

	       		
	'**********   Routine R3  *************
	
	If GetTotal("#AccountMapping.IDM" ,"" ,"DBCount" ) = 0 Then 
		GoTo RoutineR4
	Else
		If GetTotal("#AccountMapping_R.IDM" ,"" ,"DBCount" ) * GetTotal("#AccountMapping_C.IDM" ,"" ,"DBCount" ) = 0 Then 
			GoTo RoutineR4
		End If 
	End If 
	
	'sMsg = "處理程序 6/15 - 處理預篩選程序【#3. 未預期出現之特定借貸組合】"
	'dlgText "RoutineMsgText", sMsg 

	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.JoinDatabase
	task.FileToJoin "#AccountMapping.IDM"
	task.AddPFieldToInc "傳票號碼_JE"
	task.AddPFieldToInc "傳票文件項次_JE_S"
	task.AddPFieldToInc "傳票金額_JE"
	task.AddSFieldToInc "STANDARDIZED_ACCOUNT_NAME"
	task.AddMatchKey "會計科目編號_JE", "GL_NUMBER", "A"
	task.CreateVirtualDatabase = False
	dbName = "#GL#Account_Mapping.IDM"
	task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "傳票號碼_JE"
	task.AddFieldToSummarize  "傳票文件項次_JE_S"
	task.Criteria = " STANDARDIZED_ACCOUNT_NAME  == ""Revenue"" .AND.  傳票金額_JE <0"
	dbName = "#GL#Account_Mapping_Credit.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	'Derek 20180820 Modify
	Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "傳票號碼_JE"
	task.AddFieldToSummarize "傳票文件項次_JE_S"
	task.Criteria = " (STANDARDIZED_ACCOUNT_NAME  == " & Chr(34) & "Receivables"  & Chr(34) &  " .OR.  STANDARDIZED_ACCOUNT_NAME  == "  & Chr(34) &  "Cash"  & Chr(34) &  " .OR.   STANDARDIZED_ACCOUNT_NAME  == "  & Chr(34) &  "Receipt in advance"  & Chr(34) &  ") .AND.  傳票金額_JE >0"
	dbName = "#Temp1.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)

	
	Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
	Set task = db.JoinDatabase
	task.FileToJoin "#Temp1.IDM"
	task.IncludeAllPFields
	task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
	task.Criteria = " 傳票金額_JE >0"
	task.CreateVirtualDatabase = False
	dbName = "#GL#Account_Mapping_Debit.IDM"
	task.PerformTask dbName, "", WI_JOIN_NOC_SEC_MATCH
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	Call Z_Delete_File("#Temp1.IDM")
			
	If GetTotal("#GL#Account_Mapping_Debit.IDM" ,"" ,"DBCount" ) * GetTotal("#GL#Account_Mapping_Credit.IDM" ,"" ,"DBCount" ) = 0 Then
		Call X_Update_Routines_Info("R3",0) 
		Call X_Update_Routines_Info_Str("R3_Status","N/A")
		Call X_Update_Routines_Info_Str("R3_Result","沒有符合預篩選條件之明細項")
		
		Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "PRESCR_R3"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation =   Chr(34) &  "" &  Chr(34) 
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
		
		'20181018 Add
		If Z_File_Exist("#GL#Sum_By_Doc_Line.IDM") Then
		
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "R3_Tag"
			field.Description = ""
			field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
			field.Equation =   Chr(34) &  "" &  Chr(34) 
			field.Length = 1
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
		
		End If 
		
	Else 
		Set db = Client.OpenDatabase("#GL#Account_Mapping_Debit.IDM")
		Set task = db.JoinDatabase
		task.FileToJoin "#GL#Account_Mapping_Credit.IDM"
		task.IncludeAllPFields
		task.IncludeAllSFields
		task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
		task.CreateVirtualDatabase = False
		dbName = "#GL#Account_Mapping_ALL.IDM"
		task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)
	
			If GetTotal("#GL#Account_Mapping_ALL.IDM" ,"" ,"DBCount" ) = 0 Then 
				Call X_Update_Routines_Info("R3",0) 
				Call X_Update_Routines_Info_Str("R3_Status","N/A")
				Call X_Update_Routines_Info_Str("R3_Result","沒有符合預篩選條件之明細項")
				
				Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
				Set task = db.TableManagement
				Set field = db.TableDef.NewField
				field.Name = "PRESCR_R3"
				field.Description = ""
				field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
				field.Equation =   Chr(34) &  "" &  Chr(34) 
				field.Length = 1
				task.AppendField field
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Set field = Nothing

				'20181018 Add
				If Z_File_Exist("#GL#Sum_By_Doc_Line.IDM") Then
				
					Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
					Set task = db.TableManagement
					Set field = db.TableDef.NewField
					field.Name = "R3_Tag"
					field.Description = ""
					field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
					field.Equation =   Chr(34) &  "" &  Chr(34) 
					field.Length = 1
					task.AppendField field
					task.PerformTask
					Set task = Nothing
					Set db = Nothing
					Set field = Nothing
				
				End If 
								
			Else
				Call X_Update_Routines_Info("R3",1)
				Call X_Update_Routines_Info_Str("R3_Status","V") 
				Set db = Client.OpenDatabase("#GL#Account_Mapping_ALL.IDM")
				Set task = db.TableManagement
				Set field = db.TableDef.NewField
				field.Name = "PRESCR_R3"
				field.Description = ""
				field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
				field.Equation = """Y"""
				field.Length = 1
				task.AppendField field
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Set field = Nothing

				Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
				Set task = db.JoinDatabase
				task.FileToJoin "#GL#Account_Mapping_ALL.IDM"
				task.IncludeAllPFields
				task.AddSFieldToInc "PRESCR_R3"
				task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
				task.CreateVirtualDatabase = False
				dbName = "#Temp1.IDM"
				task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)			

				Client.CloseDatabase ("#Temp1.IDM")
			
				Call Z_Delete_File("#GL#Sum_By_Doc.IDM")

				Set ProjectManagement = client.ProjectManagement
					ProjectManagement.RenameDatabase "#Temp1.IDM", "#GL#Sum_By_Doc.IDM"
				Set ProjectManagement = Nothing	
			
				
				'20181018 Add 
				If Z_File_Exist("#GL#Sum_By_Doc_Line.IDM") Then
					Set db = Client.OpenDatabase("#GL#.IDM")
					Set task = db.JoinDatabase
					task.FileToJoin "#GL#Account_Mapping_ALL.IDM"
					task.IncludeAllPFields
					task.AddSFieldToInc "PRESCR_R3"
					task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
					'task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
					task.CreateVirtualDatabase = False
					dbName = "#PreScr-R3-All.IDM"
					task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)		
				
					Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
					Set task = db.JoinDatabase
					task.FileToJoin "#GL#Account_Mapping_ALL.IDM"
					task.IncludeAllPFields
					task.AddSFieldToInc "PRESCR_R3"
					task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
					'task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
					task.CreateVirtualDatabase = False
					dbName = "#Temp1.IDM"
					task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)
					
					Call Z_Delete_File("#GL#Sum_By_Doc_Line.IDM")
					Call Z_Rename_DB("#Temp1.IDM","#GL#Sum_By_Doc_Line.IDM")
					Result = Z_renameFields("#GL#Sum_By_Doc_Line.IDM","PRESCR_R3", "R3_Tag")
			
				End If 				

				Call X_Update_Routines_Info_Str("R3_Result","符合預篩選條件之明細項請詳 [R3] 工作表")
		End If    		
	End If
	Call Z_Delete_File("#GL#Account_Mapping_ALL.IDM")
	Call Z_Delete_File("#GL#Account_Mapping_Debit.IDM")
	Call Z_Delete_File("#GL#Account_Mapping_Credit.IDM")
	Call Z_Delete_File("#Temp1.IDM")
        
	RoutineR4 :
	
	If GetTotal("#AccountMapping.IDM" ,"" ,"DBCount" ) = 0 Then
		Call X_Update_Routines_Info("R3",0) 
		Call X_Update_Routines_Info_Str("R3_Status","N/A")
		Call X_Update_Routines_Info_Str("R3_Result","因未設定可進行非預期科目配對傳票篩選所需之科目配對檔，故此預篩選程序並未執行")
		
		Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "PRESCR_R3"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation =   Chr(34) &  "" &  Chr(34) 
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
		
		'20181018 Add
		If Z_File_Exist("#GL#Sum_By_Doc_Line.IDM") Then
		
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "R3_Tag"
			field.Description = ""
			field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
			field.Equation =   Chr(34) &  "" &  Chr(34) 
			field.Length = 1
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
		
		End If 		
	End If
		
	'**********   Routine R4  *************
	
	'sMsg = "處理程序 7/15 - 處理預篩選程序【#4. 分錄金額中有連續0的尾數】"
	'dlgText "RoutineMsgText", sMsg 

	amountTotal = iLen(iInt(GetTotal( "#GL#.IDM", "DEBIT_傳票金額_JE_T" , "AverageValue" )))
	Roundedamout = 1
	For i = 2 To amountTotal 
		Roundedamout = Roundedamout * 10
	Next 
        i = Len(iStr(Roundedamout,1,0))-1
	'sTemp = " 傳票金額_JE  % "  &  Roundedamout & "  = 0"
	sTemp = iRight(iStr(Roundedamout,1,0), Len(iStr(Roundedamout,1,0))-1)
	sTemp = " @Right(@Str(@int(傳票金額_JE),1,0)," & i & ") =  "  &  Chr(34) & sTemp & Chr(34)
	Call Z_DirectExtractionTable("#GL#.IDM", "#PreScr-R4.IDM", sTemp)
	If GetTotal("#PreScr-R4.IDM" ,"" ,"DBCount" ) = 0 Then
		Call X_Update_Routines_Info("R4",0)
		Call X_Update_Routines_Info_Str("R4_Status","N/A")
		Call X_Update_Routines_Info_Str("R4_Result","沒有符合預篩選條件之明細項")
		
		Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "PRESCR_R4"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation =   Chr(34) &  "" &  Chr(34) 
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing

		'20181018 Add
		If Z_File_Exist("#GL#Sum_By_Doc_Line.IDM") Then
		
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "R4_Tag"
			field.Description = ""
			field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
			field.Equation =   Chr(34) &  "" &  Chr(34) 
			field.Length = 1
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
		
		End If 
				
	Else
		Call X_Update_Routines_Info("R4",1)
		Call X_Update_Routines_Info_Str("R4_Status","V")
		Set db = Client.OpenDatabase("#PreScr-R4.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "PRESCR_R4"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation =   Chr(34) &  "Y" &  Chr(34) 
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing

		Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
		Set task = db.JoinDatabase
		task.FileToJoin "#PreScr-R4.IDM"
		task.IncludeAllPFields
		task.AddSFieldToInc "PRESCR_R4"
		task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
		task.CreateVirtualDatabase = False
		dbName = "#Temp1.IDM"
		task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)

		Client.CloseDatabase ("#Temp1.IDM")
			
		Call Z_Delete_File("#GL#Sum_By_Doc.IDM")

		Set ProjectManagement = client.ProjectManagement
			ProjectManagement.RenameDatabase "#Temp1.IDM", "#GL#Sum_By_Doc.IDM"
		Set ProjectManagement = Nothing						       		
	
		'20181018 Add
		If Z_File_Exist("#GL#Sum_By_Doc_Line.IDM") Then
		
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc_Line.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#PreScr-R4.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "PRESCR_R4"
			task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
			task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp1.IDM"
			task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
			
			Call Z_Delete_File("#GL#Sum_By_Doc_Line.IDM")
			Call Z_Rename_DB("#Temp1.IDM","#GL#Sum_By_Doc_Line.IDM")
			Result = Z_renameFields("#GL#Sum_By_Doc_Line.IDM","PRESCR_R4", "R4_Tag")
	
		End If 			

		If sVer = "TW" Then Call Z_Rename_DB("#PreScr-R4.IDM","#PreScr-R4-All.IDM")		

		Call Z_Delete_File("#PreScr-R4.IDM")
		
		Call X_Update_Routines_Info_Str("R4_Result","符合預篩選條件之明細項請詳 [R4] 工作表")
	End If 
	Call Z_Delete_File("#PreScr-R4.IDM")

	       		
	'**********   Routine R5  *************
	
	'sMsg = "處理程序 8/15 - 處理預篩選程序【#5. 依分錄編製者彙總分錄】"
	'dlgText "RoutineMsgText", sMsg 

	Dim checkField As Integer 
	ckeckField = FindField("#GL#.IDM" , "傳票建立人員_JE" )
	If ckeckField = 0 Then 
		Call X_Update_Routines_Info("R5",0)
		Call X_Update_Routines_Info_Str("R5_Status","N/A")
		Call X_Update_Routines_Info_Str("R5_Result","因於資料欄未配對時未配對【編制人員】欄位，故此預篩選程序未執行")
		GoTo RoutineSix
	End If
	
	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "傳票建立人員_JE"
	If FindField("#GL#.IDM" , "人工傳票否_JE_S" ) <> 0 Then task.AddFieldToSummarize "人工傳票否_JE_S" '2019/05/17 Add
	task.AddFieldToTotal "DEBIT_傳票金額_JE_T"
	task.AddFieldToTotal "CREDIT_傳票金額_JE_T"
	'task.Criteria = sCriteria
	dbName = "#PreScr-R5-Sum.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.StatisticsToInclude = SM_SUM
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)

	Call X_Update_Routines_Info("R5",1)
	Call X_Update_Routines_Info_Str("R5_Status","V")
	Call X_Update_Routines_Info_Str("R5_Result","符合預篩選條件之明細項請詳 [R5] 工作表")

	'**********   Routine R6  *************
	RoutineSix:	
	
	'sMsg = "處理程序 9/15 - 處理預篩選程序【#6. 較少使用之科目】"
	'dlgText "RoutineMsgText", sMsg 
			
	'Routine 6  
	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "會計科目編號_JE"
	task.AddFieldToSummarize "會計科目名稱_JE"
	task.AddFieldToTotal "DEBIT_傳票金額_JE_T"
	task.AddFieldToTotal "CREDIT_傳票金額_JE_T"
	'task.Criteria = sCriteria
	dbName = "#PreScr-R6-Sum.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.StatisticsToInclude = SM_SUM
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	'Derek 20180820 Modify 
	
	Set db = Client.OpenDatabase("#PreScr-R6-Sum.IDM")
	Set task = db.Sort
	task.AddKey "NO_OF_RECS", "A"
	task.AddKey "會計科目編號_JE", "A"
	dbName = "#PreScr-R6-Sum_Sort.IDM"
	task.PerformTask dbName
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	Call Z_Delete_File("#PreScr-R6-Sum.IDM")
	Call Z_Rename_DB("#PreScr-R6-Sum_Sort.IDM","#PreScr-R6-Sum.IDM")
	
	Call X_Update_Routines_Info("R6",1)
	Call X_Update_Routines_Info_Str("R6_Status","V")
	Call X_Update_Routines_Info_Str("R6_Result","符合預篩選條件之明細項請詳 [R6] 工作表")
		       		
	'**********   Routine R7  *************
	
	'sMsg = "處理程序 10/15 - 處理預篩選程序【總帳日/核准日在假日】" 
	'dlgText "RoutineMsgText", sMsg 

	Dim dstr As String
	dstr = client.WorkingDirectory  & "#Weekend.IDM"
	Set fs = CreateObject("Scripting.FileSystemObject")
	If Not fs.FileExists(dstr)  Then 
		Call X_Update_Routines_Info("R7",0)
	Else
		WeekField = "1"
		Call X_Update_Routines_Info("R7",1)
		
		'20181018 Modify
		If FindField("#GL#Sum_By_Doc.IDM","DOCDATE_WEEK") <> 0 Then
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#Weekend.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "WORKDAY"
			task.AddMatchKey "DOCDATE_WEEK", "DAYOFWEEK", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp1.IDM"
			task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
		
			Result = Z_renameFields("#Temp1.IDM", "WORKDAY", "DOC_WEEKEND_JE_T")		
	
			Client.CloseDatabase ("#Temp1.IDM")
				
			Call Z_Delete_File("#GL#Sum_By_Doc.IDM")
	
			Set ProjectManagement = client.ProjectManagement
				ProjectManagement.RenameDatabase "#Temp1.IDM", "#GL#Sum_By_Doc.IDM"
			Set ProjectManagement = Nothing				
		End If 		
			
		'20181018 Modify
		If FindField("#GL#Sum_By_Doc.IDM","POSTDATE_WEEK") <> 0 Then
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#Weekend.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "WORKDAY"
			task.AddMatchKey "POSTDATE_WEEK", "DAYOFWEEK", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp1.IDM"
			task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
		
			Result = Z_renameFields("#Temp1.IDM", "WORKDAY", "POST_WEEKEND_JE_T")		
	
			Client.CloseDatabase ("#Temp1.IDM")
				
			Call Z_Delete_File("#GL#Sum_By_Doc.IDM")
	
			Set ProjectManagement = client.ProjectManagement
				ProjectManagement.RenameDatabase "#Temp1.IDM", "#GL#Sum_By_Doc.IDM"
			Set ProjectManagement = Nothing						
		End If 
	End If 


	'**********   Routine R8  *************
	
	'sMsg = "處理程序 11/15 - 處理預篩選程序【總帳日/核准日在假期】" 
	'dlgText "RoutineMsgText", sMsg 

	dstr = client.WorkingDirectory  & "#Holiday#-Holiday.IDM"
	Set fs = CreateObject("Scripting.FileSystemObject")
	If Not fs.FileExists(dstr)  Then 
		Call X_Update_Routines_Info("R8",0) 
	Else
		HolidayField = "1"
		Call X_Update_Routines_Info("R8",1)

		'20181018 Modify
		If FindField("#GL#Sum_By_Doc.IDM","傳票核准日_JE") <> 0 Then
					
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#Holiday#-Holiday.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "HOLIDAY_NAME"
			task.AddSFieldToInc "IS_HOLIDAY"
			task.AddMatchKey "傳票核准日_JE", "DATE_OF_HOLIDAY", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp1.IDM"
			task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
		
			Result = Z_renameFields("#Temp1.IDM", "HOLIDAY_NAME", "DOC_HOLIDAY_NAME_JE_T")		
			Result = Z_renameFields("#Temp1.IDM", "IS_HOLIDAY", "DOC_HOLIDAY_JE_T")		
	
			Client.CloseDatabase ("#Temp1.IDM")
				
			Call Z_Delete_File("#GL#Sum_By_Doc.IDM")
	
			Set ProjectManagement = client.ProjectManagement
				ProjectManagement.RenameDatabase "#Temp1.IDM", "#GL#Sum_By_Doc.IDM"
			Set ProjectManagement = Nothing						
		End If 

		'20181018 Modify
		If FindField("#GL#Sum_By_Doc.IDM","總帳日期_JE") <> 0 Then
			Set db = Client.OpenDatabase("#GL#Sum_By_Doc.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#Holiday#-Holiday.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "HOLIDAY_NAME"
			task.AddSFieldToInc "IS_HOLIDAY"
			task.AddMatchKey "總帳日期_JE", "DATE_OF_HOLIDAY", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp1.IDM"
			task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
		
			Result = Z_renameFields("#Temp1.IDM", "HOLIDAY_NAME", "POST_HOLIDAY_NAME_JE_T")		
			Result = Z_renameFields("#Temp1.IDM", "IS_HOLIDAY", "POST_HOLIDAY_JE_T")		
	
			Client.CloseDatabase ("#Temp1.IDM")
				
			Call Z_Delete_File("#GL#Sum_By_Doc.IDM")
	
			Set ProjectManagement = client.ProjectManagement
				ProjectManagement.RenameDatabase "#Temp1.IDM", "#GL#Sum_By_Doc.IDM"
			Set ProjectManagement = Nothing						
		End If 
	End If 
       		

	'**********   Routine A2  *************

	RoutineA2 :	

	'**********   Routine A3  *************

	 RoutineA3 :
	 
	'**********   Routine A4  *************			
       	
       	RoutineA4 :
       	       		


	'**********   Routine Final  *************
	'sMsg = "處理程序 12/15 - 篩選結果彙整處理作業" 
	'dlgText "RoutineMsgText", sMsg 
       		
	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.JoinDatabase
	task.FileToJoin "#GL#Sum_By_Doc.IDM"
	task.IncludeAllPFields
	If FindField("#GL#Sum_By_Doc.IDM","PRESCR_R1") <> 0 Then task.AddSFieldToInc "PRESCR_R1"
	'task.AddSFieldToInc "PRESCR_R1"
	task.AddSFieldToInc "PRESCR_R2"
	If FindField("#GL#Sum_By_Doc.IDM","PRESCR_R3") <> 0 Then task.AddSFieldToInc "PRESCR_R3"
	task.AddSFieldToInc "PRESCR_R4"
	'task.AddSFieldToInc "PRESCR_A2"
	'task.AddSFieldToInc "PRESCR_A3"
	'task.AddSFieldToInc "PRESCR_A4"
	'20181018 modify
	If FindField("#GL#Sum_By_Doc.IDM","DOC_HOLIDAY_JE_T") <> 0 Then task.AddSFieldToInc "DOC_HOLIDAY_JE_T"
	If FindField("#GL#Sum_By_Doc.IDM","DOC_HOLIDAY_NAME_JE_T") <> 0 Then task.AddSFieldToInc "DOC_HOLIDAY_NAME_JE_T"
	If FindField("#GL#Sum_By_Doc.IDM","POST_HOLIDAY_JE_T") <> 0 Then task.AddSFieldToInc "POST_HOLIDAY_JE_T"
	If FindField("#GL#Sum_By_Doc.IDM","POST_HOLIDAY_NAME_JE_T") <> 0 Then task.AddSFieldToInc "POST_HOLIDAY_NAME_JE_T"
	If FindField("#GL#Sum_By_Doc.IDM","DOC_WEEKEND_JE_T") <> 0 Then task.AddSFieldToInc "DOC_WEEKEND_JE_T"
	If FindField("#GL#Sum_By_Doc.IDM","POST_WEEKEND_JE_T") <> 0 Then task.AddSFieldToInc "POST_WEEKEND_JE_T"
	task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
	'Derek 2019.01.24 Add
	If FindField("#GL#.IDM","總帳日期_JE") <> 0 Then task.AddMatchKey "總帳日期_JE", "總帳日期_JE", "A"
	If FindField("#GL#.IDM","傳票核准日_JE") <> 0 Then task.AddMatchKey "傳票核准日_JE", "傳票核准日_JE", "A"
	task.CreateVirtualDatabase = False
	dbName = "#GL#Critial.IDM"
	task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)    

	
	'Call Z_Delete_File("#GL#Sum_By_Doc.IDM")

	If sVer = "TW" Then 
		
		Set db = Client.OpenDatabase("#GL#Critial.IDM")
		Set task = db.JoinDatabase
		task.FileToJoin "#GL#Sum_By_Doc_Line.IDM"
		task.IncludeAllPFields
		If FindField("#GL#Sum_By_Doc_Line.IDM","R1_TAG") <> 0 Then task.AddSFieldToInc "R1_TAG"
		'task.AddSFieldToInc "R1_TAG"
		task.AddSFieldToInc "R2_TAG"
		If FindField("#GL#Sum_By_Doc_Line.IDM","R3_TAG") <> 0 Then task.AddSFieldToInc "R3_TAG"
		task.AddSFieldToInc "R4_TAG"
		'task.AddSFieldToInc "A2_TAG"
		'task.AddSFieldToInc "A3_TAG"
		'task.AddSFieldToInc "A4_TAG"
		task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
		task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
		task.CreateVirtualDatabase = False
		dbName = "#Temp1.IDM"
		task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)

		Call Z_Delete_File("#GL#Critial.IDM")
		Call Z_Rename_DB("#Temp1.IDM","#GL#Critial.IDM")
		Call Z_Delete_File("#GL#Sum_By_Doc_Line.IDM")
		
	End If	
	
	Call Z_Rename_DB("#GL#Critial.IDM","#GL#.IDM")

	Call X_Update_Step_Info("STEP_4",1)
	Call X_Update_Step_Info("STEP_3",0)
	Call X_Update_Step_User_Info("STEP_3", UserFullName, "STEP_3_Time", DateValue(Now) & " " & TimeValue(Now))
	
	'sMsg = "處理程序 13/15 - 產生Pre-screening_Report檔案，並開始處理將預篩選結果寫入Pre-screening_Report檔案中" 
	'dlgText "RoutineMsgText", sMsg 
	
	Call Step3_Export_Excel
					      		       		       		
	Shell "cmd.exe /c rd /s /q %systemdrive%\$Recycle.bin"

End Function


'Derek 20180820 Modify
Function Step3_Export_Excel

	Dim sTemp As String
	Dim db  As database
	Dim rs As recordset
	Dim ThisTable As Object
	Dim field As field
	Dim rec As Object
	Dim i As Long
	Dim j As Integer
	Dim Z As Integer
	Dim n As Integer 
	Dim iFieldCount As Integer
	Dim P As Integer
	Dim CriteriaNo(7) As String
	Dim CriteriaWP(7) As String
	Dim CriteriaKeyWord(7) As String 
	
	S1Check = 0
	
	CriteriaNo(1) = "#PreScr-R1-All.IDM"
	CriteriaWP(1) = "R1"
	CriteriaKeyWord(1) = "總帳日期_JE"
	CriteriaNo(2) = "#PreScr-R2-All.IDM"
	CriteriaWP(2) = "R2"
	CriteriaKeyWord(2) = "傳票摘要_JE" 
	CriteriaNo(3) = "#PreScr-R3-All.IDM" 
	CriteriaWP(3) = "R3"
	CriteriaKeyWord(3) = "會計科目編號_JE"
	CriteriaNo(4) =  "#PreScr-R4-All.IDM"
	CriteriaWP(4) = "R4"
	CriteriaKeyWord(4) = "傳票金額_JE"
	CriteriaNo(5) = "#PreScr-R5-Sum.IDM"
	CriteriaWP(5) = "R5"
	CriteriaKeyWord(5) = ""
	CriteriaNo(6) =  "#PreScr-R6-Sum.IDM" 
	CriteriaWP(6) = "R6"
	CriteriaKeyWord(6) = ""
	'CriteriaNo(7) = "#PreScr-A2-All.IDM"
	'CriteriaWP(7) = "A2"
	'CriteriaKeyWord(7) = "傳票摘要_JE" 	
	'CriteriaNo(8) = "#PreScr-A3-All.IDM"
	'CriteriaWP(8) = "A3"
	'CriteriaKeyWord(8) = "" 	
	'CriteriaNo(9) = "#PreScr-A4-All.IDM"
	'CriteriaWP(9) = "A4"
	'CriteriaKeyWord(9) = "傳票金額_JE"
	CriteriaNo(7) = "#Null-GL_Description_Criteria.IDM"
	CriteriaWP(7) = "R7"
	CriteriaKeyWord(7) = "傳票摘要_JE"

	n = 7
		
	For i = 1 To n
		If z_File_Exist(CriteriaNo(i)) Then
		Set SourceFile = client.OpenDataBase(CriteriaNo(i))
		Set thisTableFile = SourceFile.tabledef	  
		field_count = thisTableFile.count
	
		ReDim OrangeArray(field_count +1)
								   	
		For j = 1 To field_count
	
			Set ThisField = thisTableFile.GetFieldAt(j)
			OrangeArray(j) = ThisField.Name
							   		
		Next j
		
		Set ThisField = Nothing
		Set thisTableFile = Nothing 
		Set SourceFile = Nothing
		
			If Not (i = 5 Or i = 6 ) Then 
				Set db = Client.OpenDatabase(CriteriaNo(i))
				Set task = db.Extraction
				For j = 1 To UBound(OrangeArray)
					If iRight(OrangeArray(j),3) = "_JE" Or iRight(OrangeArray(j),5) = "_JE_S"  Then task.AddFieldToInc OrangeArray(j)
				Next j
				dbName = CriteriaWP(i) & ".IDM"  
				task.AddExtraction dbName, "", sTemp
				task.CreateVirtualDatabase = False
				task.PerformTask 1, db.Count
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)
				
				Call Z_Delete_File(CriteriaNo(i))
				Call Z_Rename_DB(CriteriaWP(i) & ".IDM"  , CriteriaNo(i))
			End If
	
		End If
					
	Next i
	
	strr= sTempExcelSource & "\PrescreeningReport.xlsx"   '
	dstr = Client.WorkingDirectory  & "Exports.ILB\" & sEngagement_Info & "_" & Format(Now, "yyyymmdd") & Format(Now, "hhmmss") & "_PrescreeningReport.xlsx"
	
	FileCopy strr, dstr
	
	Set sExcel = CreateObject("Excel.Application")
	Set oBook = sExcel.Workbooks.Open(dstr)
	Set oSheet = oBook.Worksheets.Item("Pre-screening_Report")
	
	'sMsg = "處理程序 14/15 - 將測試結果總表寫入Pre-screening_Report中，並匯出明細檔" 
	'dlgText "RoutineMsgText", sMsg 
	
	oSheet.Range("E1").value = "Client:" & sEngagement_Info
	oSheet.Range("E2").value = "Year End: " & sPeriod_End_Date
	oSheet.Range("E4").value = "Prepared by: " & UserFullName
	oSheet.Range("E5").value = "Prepared date: " & Format(DateValue(Now),"YYYY/MM/DD")

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Select * from [JE_Routines] " 

	Set SQLobjConn = CreateObject("ADODB.Connection")
	SQLobjConn.open SQLconnStr
		Set SQLrs = SQLobjConn.execute(SQLeqn)
			
			oSheet.Range("A8").value = SQLrs.Fields("R1_Status")
			oSheet.Range("A9").value = SQLrs.Fields("R2_Status")
			oSheet.Range("A10").value = SQLrs.Fields("R3_Status")
			oSheet.Range("A11").value = SQLrs.Fields("R4_Status")
			oSheet.Range("A12").value = SQLrs.Fields("R5_Status")
			oSheet.Range("A13").value = SQLrs.Fields("R6_Status")
			'oSheet.Range("A14").value = SQLrs.Fields("A2_Status")
			'oSheet.Range("A15").value = SQLrs.Fields("A3_Status")
			'oSheet.Range("A16").value = SQLrs.Fields("A4_Status")
			

			If GetTotal("#Null-GL_Description_Criteria.IDM"  ,"" ,"DBCount" ) <> 0 Then 
				oSheet.Range("A14").value = "V"
				oSheet.Range("D14").value = "符合新增預篩選條件之明細項請詳 [R7] 工作表"
			Else
				oSheet.Range("A14").value = "N/A"
				oSheet.Range("C14").value = "沒有符合預篩選條件之明細項"
				If sL <> "CHT" Then oSheet.Range("D14").value = "Please refer to sheet [R7] for details"
			End If
				
			oSheet.Range("C8").value = "將篩選期末關帳開始日 " & sLast_Accounting_Period_Date & " 之後核准並入到查核年度總帳之分錄" 
			
			amountTotal = iLen(iInt(GetTotal( "#GL#.IDM", "DEBIT_傳票金額_JE_T" , "AverageValue" )))
			Roundedamout = 1
			For i = 2 To amountTotal 
				Roundedamout = Roundedamout * 10
			Next 
			i = Len(iStr(Roundedamout,1,0))-1	
			oSheet.Range("C11").value = "設定連續0的位數為：" & i
				
			'oSheet.Range("C14").value = SQLrs.Fields("A2_Memo")
			'oSheet.Range("C15").value = SQLrs.Fields("A3_Memo")
			'oSheet.Range("C16").value = SQLrs.Fields("A4_Memo")
				
			oSheet.Range("D8").value = SQLrs.Fields("R1_Result")
			oSheet.Range("D9").value = SQLrs.Fields("R2_Result")
			oSheet.Range("D10").value = SQLrs.Fields("R3_Result")
			oSheet.Range("D11").value = SQLrs.Fields("R4_Result")
			oSheet.Range("D12").value = SQLrs.Fields("R5_Result")
			oSheet.Range("D13").value = SQLrs.Fields("R6_Result")
			'oSheet.Range("D14").value = SQLrs.Fields("A2_Result")
			'oSheet.Range("D15").value = SQLrs.Fields("A3_Result")
			'oSheet.Range("D16").value = SQLrs.Fields("A4_Result")
			
		SQLeqn = "Delete from  [Overview] where [Filename] like " & Chr(39) & "#%" & Chr(39)
		Set SQLrs = SQLobjConn.execute(SQLeqn)
		
		Set SQLrs = Nothing
	Set SQLobjConn =  Nothing
	
	Set fs = CreateObject("Scripting.FileSystemObject")

	For Z = 1 To n
		If Z = 5 Or Z = 6 Then 
			oSheet.Range("E" & Z+7).value = "N/A"
			oSheet.Range("F" & Z+7).value = "N/A"
		Else
			If  fs.FileExists(client.WorkingDirectory & CriteriaNo(Z) ) Then
				amountTotal =  GetTotal(CriteriaNo(Z),"" ,"DBCount" )
				amountTotal1= Z_Sum_Doc(CriteriaNo(Z),"傳票號碼_JE","Temp_sum.IDM")
				'amountTotal1 =  X_Char_Category(CriteriaNo(z) ,"傳票號碼_JE" )
				If amountTotal1 >2500 Then 
					oSheet.Range("E" & Z+7).value = "Over 2,500"
				ElseIf amountTotal1 = 0  Then
					oSheet.Range("E" & Z+7).value = "0"
				Else
					oSheet.Range("E" & Z+7).value = amountTotal1
				End If
				oSheet.Range("F" & Z+7).value = amountTotal
				If amountTotal >= 10000 Then oSheet.Range("D" & Z+7).value = "明細筆數超過10,000筆，明細資料不匯出"
			Else
				oSheet.Range("E" & Z+7).value = "N/A"
				oSheet.Range("F" & Z+7).value = "N/A"
			End If
			Call z_delete_file("temp_sum.IDM")
			Call z_delete_file("temp_sum1.IDM")

		End If
	Next Z

	oSheet.Columns(3).WrapText = True
		
	For Z = 1 To n	
		If  fs.FileExists(client.WorkingDirectory & CriteriaNo(z) ) Then
		
			amountTotal =  GetTotal(CriteriaNo(z),"" ,"DBCount" )
			If amountTotal > 0 And amountTotal < 10000 Then 
		
				'sMsg = "處理程序 15/15 - 將預篩選匯出之檔案複製至Pre-screening_Report，目前處理檔案為 "  & CriteriaWP(Z)
				'dlgText "RoutineMsgText", sMsg 
			 
				strr = Client.WorkingDirectory  & "Exports.ILB\" & iLeft(CriteriaNo(Z), iLen(CriteriaNo(Z))-4) & ".xlsx"
	
				Call Z_ExportDatabaseXLSX(CriteriaNo(Z), iLeft(CriteriaNo(Z), iLen(CriteriaNo(Z))-4)  & ".xlsx" , CriteriaWP(Z))
				
				Set oSheet = oBook.Worksheets.Add
				oSheet.Name = CriteriaWP(Z)
				Set oBook2=sExcel.Workbooks.Open(strr)
				Set oSheet2=oBook2.Worksheets.item(CriteriaWP(Z))
				Set oRange=oSheet2.UsedRange
				oRange.Copy
				oSheet.Paste
				sExcel.CutCopyMode = False 
				oBook2.Save
				oBook2.Close (True)
				Kill strr 
				
				For i = 1 To  20
					oSheet.Columns(i).EntireColumn.AutoFit
				Next i
				
				oBook.Sheets(CriteriaWP(Z)).Move After:=oBook.Sheets(oBook.Sheets.Count)
				
			End If
		End If 
	Next Z	
	
	fs = Nothing
	
	Set oSheet = oBook.Worksheets.Item("Pre-screening_Report")
	oSheet.Activate
	
	oBook.Save
	oBook.Close (True)
	sExcel.Quit
	Set oRange = Nothing
	Set oSheet = Nothing
	Set oBook = Nothing
	Set sExcel=Nothing

	s1Check = 0

	'sMsg = "恭喜您，步驟三作業順利完成"
	'dlgText "RoutineMsgText", sMsg 
	
	'Run_E_Time = DateValue(Now) & " " & TimeValue(Now)
	'If Introduction.CheckBox1 = 1  Then Call X_SendMail_Step("步驟三")

	'sMsg = "這個步驟已產生PrescreeningReport(JE預先篩選報告)到下面路徑： " & Client.WorkingDirectory  & "Exports.ILB\PrescreeningReport.xlsx " & Chr(13) & Chr(13) & _
	'	"請將該JE預先篩選結果與您的案件經理進行討論，讓案件經理一起參與決定後續之JE高風險範圍條件。" & Chr(13) & Chr(13) & _
	'	"亦可將該案件之IDEA Project資料夾轉給案件經理，改由案件經理來執行步驟4 [條件篩選]。"
	
	'Result = MsgBox(sMsg ,MB_OK Or MB_ICONINORMATION , "恭喜您已完成步驟三作業!!!")
	
	If Dir("C:\Audit_Tool\JE\Pre_screening.pbix") <> "" And Dir("C:\Audit_Tool\JE\CallPowerBI.iem") <> "" Then 
		sMsg = "你是否需要產生PowerBI的檔案?"
		
		Result = MsgBox( sMsg, MB_YESNO Or MB_ICONQUESTION Or MB_DEFBUTTON1 Or MB_APPLMODAL, "Generate Power BI Files!")
		If Result = IDYES Then
			Client.RunIDEAScriptEX "C:\Audit_Tool\JE\CallPowerBI.iem", "","","",""
		End If
	End If
			
	Set runScript =  CreateObject("WScript.Shell")
	runScript.Run "explorer.exe /e," & client.WorkingDirectory & "Exports.ILB"
	
End Function 

Function Z_ExportDatabaseXLSX(sSourceDB As String, sExcelName As String, sSheetName As String)
	Set db = Client.OpenDatabase(sSourceDB)
	Set task = db.ExportDatabase
	task.IncludeAllFields
	eqn = ""
	task.PerformTask Client.WorkingDirectory +"Exports.ILB\"+ sExcelName , sSheetName , "XLSX", 1, db.Count, eqn
	Set db = Nothing
	Set task = Nothing
End Function

Function Step3_Routines_Info_Reset

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Delete from  [JE_Routines] "

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
				SQLeqn = " Insert into [JE_Routines] VALUES ( 0," & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						"  0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ","  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", "  & _
						" 0 , " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & "," & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39) & ", " & Chr(39) & "NA" & Chr(39)  & " ) "
				Set SQLrs1 = SQLobjConn.execute(SQLeqn)
				
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function


Function GetTagFieldName '#GL#.IDM for Criteria DropBox

	Dim SourceFile As Datebase
	Dim thisTableFile As TableDef
	Dim field_count As Integer
	Dim i, j, k, L As Integer
	Dim ThisField, sfield As Field
	Dim iFieldType As String

	Set SourceFile = client.OpenDataBase("#GL#.IDM")
	Set thisTableFile = SourceFile.tabledef	  '這是個可以針對所指定的資料表操作的物件
	field_count = thisTableFile.count

	ReDim FieldArray_num(field_count)
	ReDim FieldArray_mix(field_count)
	ReDim FieldArray_date(field_count)
	'ReDim FieldArray_Char(field_count)
	
	i = 1
	j = 1
	k = 1
	L = 1
	n = 1

	FieldArray_num(0) = "Select..."	' 數字型態
	FieldArray_mix(0) = "Select..."	' 所有欄位
	FieldArray_date(0) = "Select..."	'日期型態
	'FieldArray_Char(0) = "Select..."	'文字型態
	

	For i = 1 To field_count
		Set ThisField = thisTableFile.GetFieldAt(i)

		'If Right(ThisField.Name,3) = "_JE" Then						   		
	 			Set sfield = thisTableFile.getfield(ThisField.Name)
			   	iFieldType = sfield.Type
			   		If ThisField.IsNumeric = True Then
			   			FieldArray_num(j) = ThisField.Name
			   			j = j + 1
			   		ElseIf  iFieldType = WI_DATE_FIELD Or iFieldType = WI_VIRT_DATE Then
			   			FieldArray_date(l) = ThisField.Name
			   			l = l + 1
			   		End If
			   		
					If iFieldType = WI_CHAR_FIELD Or iFieldType = WI_VIRT_CHAR Or  iFieldType = WI_DATE_FIELD Or iFieldType = WI_VIRT_DATE Then
   						FieldArray_mix(k) = ThisField.Name
   						k = k + 1
	   				End If 
				   	'If iFieldType = WI_CHAR_FIELD Or iFieldType = WI_VIRT_CHAR  Then
			   		'	FieldArray_Char(n) = ThisField.Name
			   		'	n = n + 1
				   	'End If
		'End If
	Next i

	Set sfield = Nothing
	Set ThisField = Nothing
	Set thisTableFile = Nothing 
	Set SourceFile = Nothing

End Function



Function CriteriaDlg_Arry
	' 日期
	DlgListboxArray "DropListSelDate1", FieldArray_date()
	DlgListboxArray "DropListSelDate2", FieldArray_date()
	
	' 文字
	DlgListboxArray "DropListSelChar1", FieldArray_Mix()
	DlgListboxArray "DropListSelChar2", FieldArray_Mix()
	DlgListboxArray "DropListSelChar3", FieldArray_Mix()
	DlgListboxArray "DropListSelChar4", FieldArray_Mix()
	DlgListboxArray "DropListSelChar5", FieldArray_Mix()

	' 數字
	DlgListboxArray "DropListSelNum1", FieldArray_Num()
	DlgListboxArray "DropListSelChar6", FieldArray_Num()
	
End Function 


Function AddAccPairing_DC
	
	Set db = Client.OpenDatabase("#AccountMapping_Sum.IDM")
	Set rs = db.RecordSet
	field_count = rs.count
	
	ReDim FieldArray_AddAccPairing(field_count+1)
	FieldArray_AddAccPairing(0) = "Select..."
	
	For Count = 1 To field_count
		rs.GetAt(Count)
		FieldArray_AddAccPairing(Count) = rs.ActiveRecord.GetCharValue("STANDARDIZED_ACCOUNT_NAME") 
	Next 
	
	If field_count > 0 Then FieldArray_AddAccPairing(field_count+1) = "Others"

	Set db = Nothing
	Set rs = Nothing
	Set rec = Nothing

	'DlgListboxArray "DropListAdd1De", FieldArray_AddAccPairing()
	'DlgListboxArray "DropListAdd1Cr", FieldArray_AddAccPairing()
	
	
	'DlgListboxArray "DropListBoxDebit1", FieldArray_AddAccPairing()
	'DlgListboxArray "DropListBoxDebit2", FieldArray_AddAccPairing()
	'DlgListboxArray "DropListBoxDebit3", FieldArray_AddAccPairing()
	'DlgListboxArray "DropListBoxCredit1", FieldArray_AddAccPairing()
	'DlgListboxArray "DropListBoxCredit2", FieldArray_AddAccPairing()
	'DlgListboxArray "DropListBoxCredit3", FieldArray_AddAccPairing()
			
End Function

'   待修改
Function Step4 (Test As String, sPairType As String)	
	DebitTemp_Match = AccPairingtoString_Match(DebitAccounts)
	CreditTemp_Match = AccPairingtoString_Match(CreditAccounts)	 
	Select Case Test
		Case "Test 1"
			'20200212 Add
			If Not Z_File_Exist("#GL#Sum_By_Doc_Line.IDM") Then
				Set db = Client.OpenDatabase("#GL#.IDM")
				Set task = db.JoinDatabase
				task.FileToJoin "#AccountMapping.IDM"
				task.AddPFieldToInc "傳票號碼_JE"
				task.AddPFieldToInc "傳票文件項次_JE_S"
				task.AddPFieldToInc "傳票金額_JE"
				task.AddSFieldToInc "STANDARDIZED_ACCOUNT_NAME"
				task.AddMatchKey "會計科目編號_JE", "GL_NUMBER", "A"
				task.CreateVirtualDatabase = False
				dbName = "#GL#Account_Mapping.IDM"
				task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)	
			End If 
					
			If sPairType = "A" Then
				Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
				Set task = db.Summarization
				task.AddFieldToSummarize "傳票號碼_JE"
				task.AddFieldToSummarize "傳票文件項次_JE_S"
				'task.Criteria = "STANDARDIZED_ACCOUNT_NAME  = " & Chr(34) & FieldArray_AddAccPairing(Criteria.DropListAdd1De)  & Chr(34) & " .AND.  傳票金額_JE >=0 "
				task.Criteria = DebitTemp_Match  & " .AND.  傳票金額_JE >=0 "
				dbName = "#GL#Account_Mapping_Debit.IDM"
				task.OutputDBName = dbName
				task.CreatePercentField = FALSE
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)
									
				Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
				Set task = db.Summarization
				task.AddFieldToSummarize "傳票號碼_JE"
				task.AddFieldToSummarize "傳票文件項次_JE_S"
				'task.Criteria = "STANDARDIZED_ACCOUNT_NAME  = " & Chr(34) &  FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) & Chr(34) & " .AND.  傳票金額_JE <0 "
				task.Criteria = CreditTemp_Match  & " .AND.  傳票金額_JE <0 "
				dbName = "#GL#Account_Mapping_Credit.IDM"
				task.OutputDBName = dbName
				task.CreatePercentField = FALSE
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)
			End If 

			If sPairType = "B" Then
				Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
				Set task = db.Summarization
				task.AddFieldToSummarize "傳票號碼_JE"
				task.AddFieldToSummarize "傳票文件項次_JE_S"
				'task.Criteria = "STANDARDIZED_ACCOUNT_NAME  = " & Chr(34) & FieldArray_AddAccPairing(Criteria.DropListAdd1De)  & Chr(34) & " .AND.  傳票金額_JE >=0 "
				task.Criteria = DebitTemp_Match  & " .AND.  傳票金額_JE >=0 "
				dbName = "#GL#Account_Mapping_Debit.IDM"
				task.OutputDBName = dbName
				task.CreatePercentField = FALSE
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)
										
				Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
				Set task = db.Summarization
				task.AddFieldToSummarize "傳票號碼_JE"
				task.AddFieldToSummarize "傳票文件項次_JE_S"
				'task.Criteria = "STANDARDIZED_ACCOUNT_NAME  = " & Chr(34) &  FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) & Chr(34) & " .AND.  傳票金額_JE <0 "
				task.Criteria = CreditTemp_Match  & " .AND.  傳票金額_JE <0 "
				dbName = "#GL#Account_Mapping_Temp.IDM"
				task.OutputDBName = dbName
				task.CreatePercentField = FALSE
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)
			
				'2019.02.19 Modify
				If GetTotal("#GL#Account_Mapping_Temp.IDM" ,"" ,"DBCount" ) <> 0 Then
					Set db = Client.OpenDatabase("#GL#Account_Mapping_Temp.IDM")
					Set task = db.Summarization
					task.AddFieldToSummarize "傳票號碼_JE"
					dbName = "#GL#Account_Mapping_Sum.IDM"
					task.OutputDBName = dbName
					task.CreatePercentField = FALSE
					task.PerformTask
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)
				
					Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
					Set task = db.JoinDatabase
					task.FileToJoin "#GL#Account_Mapping_Sum.IDM"
					task.IncludeAllPFields
					task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
					task.Criteria = " 傳票金額_JE < 0"
					task.CreateVirtualDatabase = False
					dbName = "#GL#Account_Mapping_Credit.IDM"
					task.PerformTask dbName, "", WI_JOIN_NOC_SEC_MATCH
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)
				Else
					Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
					Set task = db.Summarization
					task.AddFieldToSummarize "傳票號碼_JE"
					dbName = "#GL#Account_Mapping_Credit.IDM"
					task.OutputDBName = dbName
					task.CreatePercentField = FALSE
					task.PerformTask
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)
				End If
				
				Call Z_Delete_File("#GL#Account_Mapping_Temp.IDM")
				Call Z_Delete_File("#GL#Account_Mapping_Sum.IDM")
					
			End If 

			If sPairType = "C" Then
				Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
				Set task = db.Summarization
				task.AddFieldToSummarize "傳票號碼_JE"
				task.AddFieldToSummarize "傳票文件項次_JE_S"
				'task.Criteria = "STANDARDIZED_ACCOUNT_NAME  = " & Chr(34) & FieldArray_AddAccPairing(Criteria.DropListAdd1De)  & Chr(34) & " .AND.  傳票金額_JE >=0 "
				task.Criteria = DebitTemp_Match  & " .AND.  傳票金額_JE >=0 "
				dbName = "#GL#Account_Mapping_Temp.IDM"
				task.OutputDBName = dbName
				task.CreatePercentField = FALSE
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)
										
				Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
				Set task = db.Summarization
				task.AddFieldToSummarize "傳票號碼_JE"
				task.AddFieldToSummarize "傳票文件項次_JE_S"
				'task.Criteria = "STANDARDIZED_ACCOUNT_NAME  = " & Chr(34) &  FieldArray_AddAccPairing(Criteria.DropListAdd1Cr) & Chr(34) & " .AND.  傳票金額_JE <0 "
				task.Criteria = CreditTemp_Match  & " .AND.  傳票金額_JE <0 "
				dbName = "#GL#Account_Mapping_Credit.IDM"
				task.OutputDBName = dbName
				task.CreatePercentField = FALSE
				task.PerformTask
				Set task = Nothing
				Set db = Nothing
				Client.OpenDatabase (dbName)
			
				'2019.02.19 Modify
				If GetTotal("#GL#Account_Mapping_Temp.IDM" ,"" ,"DBCount" ) <> 0 Then
					Set db = Client.OpenDatabase("#GL#Account_Mapping_Temp.IDM")
					Set task = db.Summarization
					task.AddFieldToSummarize "傳票號碼_JE"
					dbName = "#GL#Account_Mapping_Sum.IDM"
					task.OutputDBName = dbName
					task.CreatePercentField = FALSE
					task.PerformTask
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)
				
					Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
					Set task = db.JoinDatabase
					task.FileToJoin "#GL#Account_Mapping_Sum.IDM"
					task.IncludeAllPFields
					task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
					task.Criteria = " 傳票金額_JE >= 0"
					task.CreateVirtualDatabase = False
					dbName = "#GL#Account_Mapping_Debit.IDM"
					task.PerformTask dbName, "", WI_JOIN_NOC_SEC_MATCH
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)
				
				Else
					Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
					Set task = db.Summarization
					task.AddFieldToSummarize "傳票號碼_JE"
					dbName = "#GL#Account_Mapping_Debit.IDM"
					task.OutputDBName = dbName
					task.CreatePercentField = FALSE
					task.PerformTask
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)
				End If				
				
				Call Z_Delete_File("#GL#Account_Mapping_Temp.IDM")
				Call Z_Delete_File("#GL#Account_Mapping_Sum.IDM")
					
			End If 
						
			
		Case "Test 2"
			Set db = Client.OpenDatabase("#GL#Account_Mapping_Debit.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#GL#Account_Mapping_Credit.IDM"
			task.IncludeAllPFields
			task.IncludeAllSFields
			task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
			task.CreateVirtualDatabase = False
			dbName = "#GL#Account_Mapping_ALL.IDM"
			task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
			
			Set db = Client.OpenDatabase("#GL#Account_Mapping_Debit.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#GL#Account_Mapping_ALL.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "傳票號碼_JE"
			task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp3.IDM"
			task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
		
			Set db = Client.OpenDatabase("#GL#Account_Mapping_Credit.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#GL#Account_Mapping_ALL.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "傳票號碼_JE"
			task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp4.IDM"
			task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)
		
			Set db = Client.OpenDatabase("#Temp4.IDM")
			Set task = db.AppendDatabase
			task.AddDatabase "#Temp3.IDM"
			dbName = "#Temp5.IDM"
			task.PerformTask dbName, ""
			Set task = Nothing
			Set db = Nothing
			Client.OpenDatabase (dbName)		
			
			Call Z_Rename_DB("#Temp5.IDM","#GL#Account_Mapping_ALL.IDM")
			Call Z_Delete_File("#Temp3.IDM")
			Call Z_Delete_File("#Temp4.IDM")
				
		Case "Test 3"

		Case "Test 4"
			Client.CloseDatabase ("#Temp1.IDM")
			Client.CloseDatabase ("#GL#In_Period.IDM")
			
			Set db = Client.OpenDatabase("#GL#In_Period.IDM")
			Set task = db.JoinDatabase
			task.FileToJoin "#Temp1.IDM"
			task.IncludeAllPFields
			task.AddSFieldToInc "傳票號碼_JE"
			task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
			task.CreateVirtualDatabase = False
			dbName = "#Temp.IDM"
			task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
			Set task = Nothing
			Set db = Nothing
			
	End Select
								
End Function								


Function Step4_Check_Select

	If  Criteria.ChkBoxRout1  = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf Criteria.ChkBoxRout2 = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf  Criteria.ChkBoxRout3  = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf  Criteria.ChkBoxRout4 = 1 Then
		sRoutine = 1
		Exit Function
	'ElseIf  Criteria.ChkBoxModfRout2 =  1 Then
	'	sRoutine = 1
	'	Exit Function
	ElseIf  Criteria.ChkBoxModfRout4 = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf Criteria.ChkBoxWeeekend_DocDate  = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf  Criteria.ChkBoxWeeekend_PostDate = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf  Criteria.ChkBoxHoliday_DocDate = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf Criteria.ChkBoxHoliday_PostDate = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf  Criteria.ChkBoxIsManual = 1 Then
		sRoutine = 1
		Exit Function
	ElseIf  Criteria.ChkBoxSelDate1 = 1 Then
		Exit Function
	ElseIf Criteria.ChkBoxSelDate2 =  1 Then
		Exit Function
	'ElseIf Criteria.ChkBoxModfRout3 = 1 Then
	'	Exit Function
	ElseIf Criteria.ChkBoxModfRoutA3 = 1 Then
		Exit Function
	ElseIf Criteria.ChkBoxSelNum1 =  1 Then
		Exit Function
	ElseIf Criteria.ChkBoxNumAcc =1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxSelChar1 = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxIsDescNull = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxSelChar2 = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxSelChar3 = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxSelChar4 = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxSelChar5 = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxSelChar6 = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxDebit = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxCredit = 1 Then
		Exit Function
	ElseIf  Criteria.ChkBoxMakeUpDay = 1 Then
		Exit Function
	Else
		sMsg = "沒有任何項目被勾選，請至少選擇一項"
		Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟四 篩選條件挑選錯誤 警示訊息")
		Step4_Check_Select = 1
	End If

End Function

Function Step4_Check_Select_TW

	i = 0 
	sMsg = ""

	If Criteria.ChkBoxRout1 = 1 Then i = i + 1
	If Criteria.ChkBoxRout2 = 1 Then i = i + 1
	If Criteria.ChkBoxRout3 = 1 Then i = i + 1
	If Criteria.ChkBoxRout4 = 1 Then i = i + 1
	'If Criteria.ChkBoxModfRout2 = 1 Then i = i + 1
	'If Criteria.ChkBoxModfRout3 = 1 Then i = i + 1
	If Criteria.ChkBoxModfRout4 = 1 Then i = i + 1
	If Criteria.ChkBoxIsDescNull = 1 Then i = i + 1
	If Criteria.ChkBoxSelChar1  = 1 Then i = i + 1
	If Criteria.ChkBoxSelChar2  = 1 Then i = i + 1
	If Criteria.ChkBoxIsManual = 1 Then i = i + 1
	If Criteria.ChkBoxWeeekend_DocDate = 1 Then i = i + 1
	If Criteria.ChkBoxHoliday_DocDate = 1 Then i = i + 1
	If Criteria.ChkBoxWeeekend_PostDate = 1 Then i = i + 1
	If Criteria.ChkBoxHoliday_PostDate = 1 Then i = i + 1
	If Criteria.ChkBoxSelDate1  = 1 Then i = i + 1
	If Criteria.ChkBoxSelDate2  = 1 Then i = i + 1
	If Criteria.ChkBoxModfRoutA3  = 1 Then i = i + 1
	If Criteria.ChkBoxNumAcc  = 1 Then i = i + 1
	If Criteria.ChkBoxDebit  = 1 Then i = i + 1
	If Criteria.ChkBoxCredit  = 1 Then i = i + 1
	If Criteria.ChkBoxMakeUpDay  = 1 Then i = i + 1
	
	If Criteria.ChkBoxRout2 = 1 And Criteria.ChkBoxIsDescNull = 1 Then sMsg = sMsg + "** 【分錄摘要出現預設之特定描述】 與 【摘要為空白】 不可同時勾選"  & Chr(10)
	'If Criteria.ChkBoxRout3 = 1 And Criteria.ChkBoxModfRoutA3 = 1 Then sMsg = sMsg + "** 【#3. 未預期出現之特定借貸組合】 與 【符合特定科目類別】 不可同時勾選"  & Chr(10)
	If Criteria.ChkBoxRout3 = 1 And Criteria.ChkBoxNumAcc = 1 Then sMsg = sMsg + "** 【未預期出現之特定借貸組合】 與 【數字篩選條件下符合特定科目類別】 不可同時勾選"  & Chr(10)
	If Criteria.ChkBoxModfRoutA3 = 1 And Criteria.ChkBoxNumAcc = 1 Then sMsg = sMsg + "** 【符合特定科目類別】 與 【數字篩選條件下符合特定科目類別】 不可同時勾選"  & Chr(10)
	'If Criteria.ChkBoxRout2 = 1 And Criteria.ChkBoxModfRout2 = 1 Then sMsg = sMsg + "** 【#2. 分錄摘要出現預設之特定描述】 與 【# 自行設定之特定描述摘要】 不可同時勾選"  & Chr(10)
	If Criteria.ChkBoxRout4 = 1 And Criteria.ChkBoxModfRout4 = 1 Then sMsg = sMsg + "** 【分錄金額中有連續0的尾數】 與 【# 自行設定之額外的特定尾數】 不可同時勾選"  & Chr(10)
	If Criteria.ChkBoxDebit = 1 And Criteria.ChkBoxCredit = 1 Then sMsg = sMsg + "** 【僅考量借方傳票】 與 【僅考量貸方傳票】 不可同時勾選"  & Chr(10)

	If (Criteria.ChkBoxWeeekend_DocDate = 1 And Criteria.ChkBoxHoliday_DocDate = 1) Or  (Criteria.ChkBoxWeeekend_DocDate = 1 And Criteria.ChkBoxHoliday_PostDate = 1)  Or _
		(Criteria.ChkBoxWeeekend_PostDate = 1 And Criteria.ChkBoxHoliday_DocDate = 1) Or  (Criteria.ChkBoxWeeekend_PostDate = 1 And Criteria.ChkBoxHoliday_PostDate = 1) Then _
		sMsg = sMsg + "** 【日期在非工作日之周末】 與 【日期在國定假日】 不可同時勾選"  & Chr(10)
	
	If i > 1 Then 
		'If Criteria.ChkBoxRout1 = 1 And Z_File_Exist("#PreScr-R1-All.IDM") <> True Then sMsg = sMsg + "** 預篩選【財務報表核准入後核准之傳票分錄】篩選結果為空值，不可設為進階篩選的組合條件之一"  & Chr(10)
		'If Criteria.ChkBoxRout2 = 1 And Z_File_Exist("#PreScr-R2-All.IDM") <> True Then sMsg = sMsg + "** 預篩選【分錄摘要出現預設之特定描述】篩選結果為空值，不可設為進階篩選的組合條件之一"  & Chr(10)
		'If Criteria.ChkBoxRout3 = 1 And Z_File_Exist("#PreScr-R3-All.IDM") <> True Then sMsg = sMsg + "** 預篩選【未預期出現之特定借貸組合】篩選結果為空值，不可設為進階篩選的組合條件之一"  & Chr(10)
		'If Criteria.ChkBoxRout4 = 1 And Z_File_Exist("#PreScr-R4-All.IDM") <> True Then sMsg = sMsg + "** 預篩選【分錄金額中有連續0的尾數】篩選結果為空值，不可設為進階篩選的組合條件之一"  & Chr(10)
		'If Criteria.ChkBoxModfRout2 = 1 And Z_File_Exist("#PreScr-A2-All.IDM") <> True Then sMsg = sMsg + "** 預篩選【# 自行設定之特定描述摘要】篩選結果為空值，不可設為進階篩選的組合條件之一"  & Chr(10)
		'If Criteria.ChkBoxModfRout2 = 1 And Criteria.ChkBoxIsDescNull = 1 Then sMsg = sMsg + "** 預篩選【# 自行設定之特定描述摘要】與【摘要欄位為空白】不可同時設為進階篩選的組合條件"  & Chr(10)
		'If Criteria.ChkBoxModfRout3 = 1 And Z_File_Exist("#PreScr-A3-All.IDM") <> True Then sMsg = sMsg + "** 預篩選【# 自行設定之額外的科目借貸組合】篩選結果為空值，不可設為進階篩選的組合條件之一"  & Chr(10)
		'If Criteria.ChkBoxModfRout4 = 1 And Z_File_Exist("#PreScr-A4-All.IDM") <> True Then sMsg = sMsg + "** 預篩選【自行設定之額外的特定尾數】篩選結果為空值，不可設為進階篩選的組合條件之一"  & Chr(10)
		'If Criteria.ChkBoxIsDescNull = 1 And GetTotal("#Null-GL_Description_Criteria.IDM" ,"" ,"DBCount" ) = 0  Then sMsg = sMsg + "** 預篩選【摘要欄位為空白】篩選結果為空值，不可設為進階篩選的組合條件之一"  & Chr(10)
		'If FieldArray_Mix(Criteria.DropListSelChar1) = "傳票摘要_JE" And Criteria.ChkBoxRout2 = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【分錄摘要出現預設之特定描述】同時設為進階篩選的組合條件"  & Chr(10)
		'If FieldArray_Mix(Criteria.DropListSelChar2) = "傳票摘要_JE" And Criteria.ChkBoxRout2 = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【分錄摘要出現預設之特定描述】同時設為進階篩選的組合條件"  & Chr(10)
		'If FieldArray_Mix(Criteria.DropListSelChar3) = "傳票摘要_JE" And Criteria.ChkBoxRout2 = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【分錄摘要出現預設之特定描述】同時設為進階篩選的組合條件"  & Chr(10)
		'If FieldArray_Mix(Criteria.DropListSelChar4) = "傳票摘要_JE" And Criteria.ChkBoxRout2 = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【分錄摘要出現預設之特定描述】同時設為進階篩選的組合條件"  & Chr(10)
		'If FieldArray_Mix(Criteria.DropListSelChar5) = "傳票摘要_JE" And Criteria.ChkBoxRout2 = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【分錄摘要出現預設之特定描述】同時設為進階篩選的組合條件"  & Chr(10)
		'If FieldArray_Num(Criteria.DropListSelChar6) = "傳票摘要_JE" And Criteria.ChkBoxRout2 = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【分錄摘要出現預設之特定描述】同時設為進階篩選的組合條件"  & Chr(10)		
		'If FieldArray_Char(Criteria.DropListSelChar1) = "傳票摘要_JE" And Criteria.ChkBoxModfRout2 = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【# 自行設定之特定描述摘要】同時設為進階篩選的組合條件"  & Chr(10)
		'If FieldArray_Char(Criteria.DropListSelChar2) = "傳票摘要_JE" And Criteria.ChkBoxModfRout2 = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【# 自行設定之特定描述摘要】同時設為進階篩選的組合條件"  & Chr(10)
		If FieldArray_Mix(Criteria.DropListSelChar1) = "傳票摘要_JE" And Criteria.ChkBoxIsDescNull = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【摘要欄位為空白】同時設為進階篩選的組合條件"  & Chr(10)
		If FieldArray_Mix(Criteria.DropListSelChar2) = "傳票摘要_JE" And Criteria.ChkBoxIsDescNull = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【摘要欄位為空白】同時設為進階篩選的組合條件"  & Chr(10)
		If FieldArray_Mix(Criteria.DropListSelChar3) = "傳票摘要_JE" And Criteria.ChkBoxIsDescNull = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【摘要欄位為空白】同時設為進階篩選的組合條件"  & Chr(10)
		If FieldArray_Mix(Criteria.DropListSelChar4) = "傳票摘要_JE" And Criteria.ChkBoxIsDescNull = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【摘要欄位為空白】同時設為進階篩選的組合條件"  & Chr(10)
		If FieldArray_Mix(Criteria.DropListSelChar5) = "傳票摘要_JE" And Criteria.ChkBoxIsDescNull = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【摘要欄位為空白】同時設為進階篩選的組合條件"  & Chr(10)
		If FieldArray_Num(Criteria.DropListSelChar6) = "傳票摘要_JE" And Criteria.ChkBoxIsDescNull = 1 Then sMsg = sMsg + "** 自訂篩選欄位設定摘要【傳票摘要_JE】者 ，不可與預篩選【摘要欄位為空白】同時設為進階篩選的組合條件"  & Chr(10)
	End If	
	
	If sMsg <> "" Then 
		Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION , "步驟四 篩選條件錯誤 警示訊息")
		Step4_Check_Select_tw = 1
	End If

End Function

Function Step4_Reset_CheckBox

	Criteria.ChkBoxRout1 = 0
	Criteria.ChkBoxRout2 = 0
	Criteria.ChkBoxRout3 = 0
	Criteria.ChkBoxRout4 = 0
	'Criteria.ChkBoxModfRout2 = 0 
	'Criteria.ChkBoxModfRout3 = 0
	Criteria.ChkBoxModfRout4 = 0
	Criteria.ChkBoxModfRoutA3 = 0
	Criteria.ChkBoxWeeekend_DocDate = 0
	Criteria.ChkBoxWeeekend_PostDate = 0
	Criteria.ChkBoxHoliday_DocDate = 0
	Criteria.ChkBoxHoliday_PostDate = 0
	Criteria.ChkBoxIsManual = 0
	Criteria.ChkBoxSelDate1 = 0
	Criteria.ChkBoxSelDate2 = 0
	Criteria.ChkBoxSelNum1 = 0
	Criteria.ChkBoxSelChar1 = 0
	Criteria.ChkBoxSelChar2 = 0
	Criteria.ChkBoxSelChar3 = 0
	Criteria.ChkBoxSelChar4 = 0
	Criteria.ChkBoxSelChar5 = 0
	Criteria.ChkBoxSelChar6 = 0
	Criteria.ChkBoxIsDescNull = 0
	Criteria.ChkBoxNumAcc = 0
	'Criteria.TextRout3Add1 = 0
	Criteria.ChkBoxDebit = 0
	Criteria.ChkBoxCredit = 0
	Criteria.ChkBoxMakeUpDay = 0
	Criteria.ChkBoxMakeUpDay1 = 0
	
End Function


Function GetSelectChar(SelChar As String, sFieldName As String )

	Dim sTemp_a As String
	Dim count As Integer
	Dim SelectChoose As String
	
	If SelChar ="JETEAM" Then
		GetSelectChar = "@isblank(" & sFieldName & ")=1"
	
	Else	
		sTemp = iRemove(iAllTrim( iUpper(iReplace(SelChar,",","|")))," ")
		GetSelectChar = "@RegExpr( @AllTrim( @Upper(" & sFieldName & " )) , " & Chr(34) & sTemp & Chr(34) &")"
	End If

End Function

Function GetSelectNum(SelChar As String, sFieldName As String )

	If SelChar ="JETEAM" Then
		GetSelectNum =  sFieldName & "=0"
	
	Else		
		sTemp = iRemove(iAllTrim( iUpper(iReplace(SelChar,",","|")))," ")
		GetSelectNum = "@RegExpr( @AllTrim( @Upper(@str(@int(" & sFieldName & " ),1,0))) , " & Chr(34) & sTemp & Chr(34) &")"
	End If 

End Function

Function Step4_JoinDatabase(sPDB As String, sSDB As String, sNewDB As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.JoinDatabase
	task.FileToJoin sSDB
	task.IncludeAllPFields
	task.AddSFieldToInc "傳票號碼_JE"
	task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
	task.CreateVirtualDatabase = False
	dbName = sNewDB
	task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	'20181022 Add
	If sVer = "TW" And GetTotal(sNewDB ,"" ,"DBCount" ) <> 0 Then 
	'MsgBox sSDB
		Set db = Client.OpenDatabase(sSDB)
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "K_TEMP"
		field.Description = ""
		field.Type = WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation =  Chr(34) & "Y" & Chr(34)  'Chr(34) & sLogMemo & Chr(34)
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing			

		Set db = Client.OpenDatabase(sNewDB)
		Set task = db.JoinDatabase
		task.FileToJoin sSDB
		task.IncludeAllPFields
		task.AddSFieldToInc "K_TEMP"
		task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
		task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
		'If sSDB <> "#GL#Account_Mapping_ALL.IDM" Then  task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
		task.CreateVirtualDatabase = False
		dbName = "#TempS.IDM"
		task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)
		
		If FindField("#TempS.IDM","Criteria_Tag") = 0 Then
			Result = Z_renameFields("#TempS.IDM","K_TEMP", "Criteria_Tag")
		Else
		
			Set db = Client.OpenDatabase("#TempS.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "K_TEMP1"
			field.Description = ""
			field.Type = WI_CHAR_FIELD
			'field.Equation = "Criteria_MEMO + @If(Criteria_MEMO <> """", "" 和 "", """")  +  K_TEMP "
			field.Equation = "Criteria_Tag + K_TEMP "
			field.Length = 20
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
			
			Call X_RemoveField("#TempS.IDM", "Criteria_Tag")
			Call X_RemoveField("#TempS.IDM", "K_TEMP")
			Result = Z_renameFields("#TempS.IDM","K_TEMP1", "Criteria_Tag")
		End If
		
		Call Z_Rename_DB("#TempS.IDM",sNewDB)
							
	End If
	
End Function


Function  Step4_Summarization(sPDB As String, sField As String, sNewDB As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.Summarization
	task.AddFieldToSummarize sField
	dbName = sNewDB
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
End Function

Function  Step4_Summarization_Line(sPDB As String, sField1 As String, sField2 As String, sField3 As String , sNewDB As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.Summarization
	task.AddFieldToSummarize sField1
	task.AddFieldToSummarize sField2
	task.AddFieldToSummarize sField3
	dbName = sNewDB
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
End Function

Function Z_Rename_DB(sSourceDB As String, sNewDB As String)

	Client.CloseDatabase (sNewDB)
	Call Z_Delete_File(sNewDB)
	Client.CloseDatabase (sSourceDB)

	Set ProjectManagement = client.ProjectManagement
	ProjectManagement.RenameDatabase sSourceDB, sNewDB
	Set ProjectManagement = Nothing	

End Function 

Function Z_File_Exist(sIDMFile As String) As Boolean

	Set fs = CreateObject("Scripting.FileSystemObject")
	
	If  fs.FileExists(client.WorkingDirectory & sIDMFile ) Then 
		Z_File_Exist = true
	Else
		Z_File_Exist = false
	End If
	
	fs = Nothing

End Function


Function X_Char_Category(sSource As String, sField As String)

	Set db = Client.OpenDatabase(sSource)
	Set stats = db.FieldStats(sField)

		X_Char_Category = stats.NumCategories()

	Set db = Nothing
	Set stats = Nothing

End Function 


Function CriteriaLogSum_Dlg(ControlID$, Action%, SuppValue%)
Dim bExitFunction As Boolean

	Select Case Action%
		Case 1   
			DlgText "GroupBox1","篩選條件 一"
			DlgText "GroupBox2","篩選條件 二"
			DlgText "GroupBox3","篩選條件 三"
			DlgText "GroupBox4","篩選條件 四"
			DlgText "GroupBox5","篩選條件 五"
			DlgText "GroupBox6","篩選條件 六"
			DlgText "GroupBox7","篩選條件 七"
			DlgText "GroupBox8","篩選條件 八"
			DlgText "GroupBox9","篩選條件 九"
			DlgText "GroupBox10","篩選條件 十"
			
			connStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
				eqn = "SELECT * FROM [JE_Criteria_Log] order by  [SEQ_Num]  ASC"

				Set objConn = CreateObject("ADODB.Connection")
					objConn.open connStr
					Set rs = objConn.execute(eqn)
						Do While Not rs.EOF
					
						For i = 1 To 10 
							If rs.Fields("SEQ_Num")  = "W" & i Then 
								dlgText "W" & i & "_Log", rs.Fields("Criteria_Log")
								If sVer = "TW" And sL = "CHT"  Then 
									If Val(rs.Fields("DocNum")) = -1 Then 
										dlgText "CD" & i , " *** 總共有 2500+ 張傳票(明細項共有 " & Val(rs.Fields("ItemNum"))  & " 筆)，其中共有" & Val(rs.Fields("SumCheck")) & " 筆明細符合此篩選條件! ***"
									ElseIf Val(rs.Fields("DocNum")) = -2 Then
										dlgText "CD" & i , " *** 總共有 0 張傳票(明細項共有 " & Val(rs.Fields("ItemNum"))  & " 筆)，其中共有" & Val(rs.Fields("SumCheck")) & " 筆明細符合此篩選條件! ***"
									Else
										dlgText "CD" & i , " *** 總共有 " & Val(rs.Fields("DocNum")) & " 張傳票(明細項共有 " & Val(rs.Fields("ItemNum"))  & " 筆)，其中共有" & Val(rs.Fields("SumCheck")) & " 筆明細符合此篩選條件! ***"
									End If
								Else
									dlgText "CD" & i , " *** Total have " & Val(rs.Fields("DocNum")) & " documents with " & Val(rs.Fields("ItemNum"))  & " lines! ***"
								End If								
							End If 
						Next i 
						
						 rs.MoveNext
					Loop

				Set rs = Nothing

			Set objConn =  Nothing		
		Case 2   
		
	End Select 
	
	If  bExitFunction Then
		CriteriaLogSum_Dlg = 0
	Else 
		CriteriaLogSum_Dlg = 1
	End If	
		
End Function 


Function Step4_Export_Excel

	Dim sTemp As String
	Dim db  As database
	Dim rs As recordset
	Dim ThisTable As Object
	Dim field As field
	Dim rec As Object
	Dim i As Long
	Dim j As Integer
	Dim k As Integer
	Dim iFieldCount As Integer
	Dim P As Integer
	Dim CriteriaLog(10) As String
	
	S1Check = 0

	strr= sTempExcelSource & "\CriteriaSelectionReport.xlsx"   '
	dstr = Client.WorkingDirectory  & "Exports.ILB\" & sEngagement_Info & "_" & Format(Now, "yyyymmdd") & Format(Now, "hhmmss") & "_CriteriaSelectionReport.xlsx"
	
	FileCopy strr, dstr
	
	Set sExcel = CreateObject("Excel.Application")
	Set oBook = sExcel.Workbooks.Open(dstr)
	Set oSheet = oBook.Worksheets.Item("Summary Inforamtion")
	
	'sMsg = "Processing - Export generation information to Excel..."
	'dlgText "CriteriaMsgText", sMsg 

	oSheet.Range("C1").value = "Client:" & sEngagement_Info
	oSheet.Range("C2").value = "Year End: " & sPeriod_End_Date
	oSheet.Range("D1").value = "Prepared by: " & UserFullName
	oSheet.Range("D2").value = "Prepared date: " & Format(DateValue(Now),"YYYY/MM/DD")

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "SELECT * FROM [JE_Criteria_Log] order by  [SEQ_Num] ASC"

	Set SQLobjConn = CreateObject("ADODB.Connection")
	SQLobjConn.open SQLconnStr
		Set SQLrs = SQLobjConn.execute(SQLeqn)

			Do While Not SQLrs.EOF
			
			oSheet.Cells.EntireRow.AutoFit
			'oSheet.Cells.EntireColumn.AutoFit
				
				For i = 1 To 10
					If SQLrs.Fields("SEQ_Num")  = "W" & i Then 
						oSheet.Range("A" & i + 4).value = "Criteria Selection " & i 
						oSheet.Range("B" & i + 4).value = SQLrs.Fields("Criteria_Log")
						If Val(SQLrs.Fields("DocNum")) = -1 Then 
							oSheet.Range("C" & i + 4).value = "Over 2,500"
						Else
							oSheet.Range("C" & i + 4).value = Val(SQLrs.Fields("DocNum"))
						End If
						'oSheet.Range("D" & i + 4).value = SQLrs.Fields("ItemNum")
						oSheet.Range("D" & i + 4).value = SQLrs.Fields("SumCheck")
						CriteriaLog(i) = SQLrs.Fields("Criteria_Log")
					End If 
				Next i 
						
			 SQLrs.MoveNext
			Loop
		Set SQLrs = Nothing
	Set SQLobjConn =  Nothing		
		
	Set fs = CreateObject("Scripting.FileSystemObject")

	For i = 1 To 10
		If  fs.FileExists(client.WorkingDirectory & "#CriteriaSelect" & i & ".IDM"  ) Then
		
			'2019/03/06 Adj
			If  GetTotal("#CriteriaSelect" & i & ".IDM" ,"" ,"DBCount" ) <=  1000000 Then
		
			'sMsg = "Processing - Export " & "#CriteriaSelect" & i & ".IDM" 
			'dlgText "CriteriaMsgText", sMsg 
		 
			strr = Client.WorkingDirectory  & "Exports.ILB\" & "#CriteriaSelect" & i & ".xlsx"

			Call Z_ExportDatabaseXLSX("#CriteriaSelect" & i & ".IDM", "#CriteriaSelect" & i  & ".xlsx" , "#Criteria Select " & i)
			
			Set oSheet = oBook.Worksheets.Add
			oSheet.Name = "#Criteria Select " & i
			Set oBook2=sExcel.Workbooks.Open(strr)
			Set oSheet2=oBook2.Worksheets.item("#Criteria Select " & i)
			Set oRange=oSheet2.UsedRange
			oRange.Copy
			oSheet.Paste
			sExcel.CutCopyMode = False 
			oBook2.Save
			oBook2.Close (True)
			Kill strr 
			
			For j = 1 To  20
				oSheet.Columns(j).EntireColumn.AutoFit
			Next j
			
			
			oBook.Sheets("#Criteria Select " & i).Move After:=oBook.Sheets(oBook.Sheets.Count)
			
			End If
			
		End If 
	Next i
	
	fs = Nothing
	
	Set oSheet = oBook.Worksheets.Item("Summary Inforamtion")
	oSheet.Activate
		
	oBook.Save
	oBook.Close (True)
	sExcel.Quit
	Set oRange = Nothing
	Set oSheet = Nothing
	Set oBook = Nothing
	Set sExcel = Nothing

	Call X_Update_Step_User_Info("STEP_4", UserFullName, "STEP_4_Time", DateValue(Now) & " " & TimeValue(Now))
	
	'sMsg = "Processing Done..."
	'dlgText "CriteriaMsgText", sMsg 

	sMsg = "在此步驟中，已產生篩選後報告且儲存錄路徑 " & Client.WorkingDirectory  & "Exports.ILB\CriteriaSelectionReport.xlsx "
	
	Result = MsgBox(sMsg ,MB_OK Or MB_ICONINFORMATION , "步驟四 進階篩選測試果輸出訊息")
			
	Set runScript =  CreateObject("WScript.Shell")
	runScript.Run "explorer.exe /e," & client.WorkingDirectory & "Exports.ILB"	
	
End Function


Function X_Step4_Drop_Log_Table

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	
		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr

			SQLeqn = "Delete from [JE_Criteria_Log] "
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
			SQLeqn = "Delete from [JE_Criteria] "
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
			SQLeqn = " Insert into [JE_Criteria] VALUES ( 1,0,0,0,0,0,0,0,0,0,0)"
			Set SQLrs = SQLobjConn.execute(SQLeqn)
	
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function

Function Step4_Button_Disable

	DlgEnable "BtnRun", 0
	DlgEnable "QuickSummary", 0
	DlgEnable "BtnExpSumReport", 0
	DlgEnable "BtnSelSpeDate1", 0
	DlgEnable "BtnSelSpeDate2", 0
	DlgEnable "BtnSelSpeChar1", 0
	DlgEnable "BtnSelSpeChar2", 0
	DlgEnable "BtnSelSpeChar3", 0
	DlgEnable "BtnSelSpeChar4", 0
	DlgEnable "BtnSelSpeChar5", 0
	DlgEnable "BtnSelSpeChar6", 0
	DlgEnable "BtnExit", 0
	DlgEnable "BtnFromDate1", 0
	DlgEnable "BtnToDate1", 0
	DlgEnable "BtnFromDate2", 0
	DlgEnable "BtnToDate2", 0
	DlgEnable "BtnAccPairingDr",0
	DlgEnable "BtnAccPairingCr",0
	
End Function

Function Step4_Button_Enable

	If sSEQ_Num < 11 Then DlgEnable "BtnRun" , 1   

	DlgEnable "BtnSelSpeDate1", 1
	DlgEnable "BtnSelSpeDate2", 1
	DlgEnable "BtnSelSpeChar1", 1
	DlgEnable "BtnSelSpeChar2", 1
	DlgEnable "BtnSelSpeChar3", 1
	DlgEnable "BtnSelSpeChar4", 1
	DlgEnable "BtnSelSpeChar5", 1
	DlgEnable "BtnSelSpeChar6", 1
	'DlgEnable "BtnRun", 1
	DlgEnable "QuickSummary", 1
	DlgEnable "BtnExpSumReport", 1
	DlgEnable "BtnExit", 1
	DlgEnable "BtnFromDate1", 1
	DlgEnable "BtnToDate1", 1
	DlgEnable "BtnFromDate2", 1
	DlgEnable "BtnToDate2", 1
	DlgEnable "BtnAccPairingDr",1
	DlgEnable "BtnAccPairingCr",1

End Function



Function Step5_Export_Excel_TW

	Dim sTemp As String
	Dim db  As database
	Dim rs As recordset
	Dim ThisTable As Object
	Dim field As field
	Dim rec As Object
	Dim i As Long
	Dim j As Integer
	Dim Z As Integer
	Dim iFieldCount As Integer
	Dim p As Long


	sMsg = "處理進度 1/13 - 開始彙整挑選要產出至W/P的篩選項目"
	dlgText "WPMsgText", sMsg 

	Call Step5_ToWP_Sum
	Call Step5_ToWP_ReDo
	
	sMsg = "處理進度 2/13 - 複製W/P範本檔"
	dlgText "WPMsgText", sMsg 
	
	PW = "SSZZXXXASDShiosa8asfjas;f99992"
	
	strr= sTempExcelSource & "\WorkingPaper.xlsx"   '
	dstr = Client.WorkingDirectory  & "Exports.ILB\" & sEngagement_Info & "_" & Format(Now, "yyyymmdd") & Format(Now, "hhmmss") & "_WorkingPaper.xlsx"
	
	FileCopy strr, dstr
		
	Set sExcel = CreateObject("Excel.Application")
	Set oBook = sExcel.Workbooks.Open(dstr)
	
	Set oSheet = oBook.Worksheets.Item("step1 完整性測試")
	
	sMsg = "處理進度 3/13 - 資料處理項目 [step1 完整性測試] ..."
	dlgText "WPMsgText", sMsg 

	oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
	oSheet.Range("A2").value = "測試資料期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date
	oSheet.Range("A3").value = "財務報表準備期間 - 開始日 : " & sLast_Accounting_Period_Date
	
	If GetTotal("#List_of_accounts_with_variance.IDM" ,"" ,"DBCount" ) > 0 Then 
		oSheet.Range("B15").value = "基於上述程序，查核團隊對於JE測試母體之完整性，尚需於Step1-3說明以取得足夠的查核證據。" 
		oSheet.Range("B17").value = "#針對試算表科目金額本期異動與會計分錄(JE)進行推滾比對之清單列示如下：(有部分科目之差異數不為0，請於step1-3說明其理由，以確認JE母體的完整性)" 
		oSheet.Range("B15:B17").Font.Bold = True
		oSheet.Range("B15:B17").Font.Name = "Calibri"
		oSheet.Range("B15:B17").Font.Size = 12
		oSheet.Range("B15:B17").Font.ColorIndex = 3
	End If
	
	Set db = client.OpenDatabase("#Completeness_Check.IDM")
	Set ThisTable = db.TableDef
	Set rs = db.RecordSet		

	p = 1
	iFieldCount = ThisTable.count
	For j = 1 To iFieldCount
		Set field = ThisTable.GetFieldAt (j) 
				rs.ToFirst
				For i = 1 To db.count
				Set rec = rs.ActiveRecord
					rs.Next
		
					If field.IsCharacter Then 
						oSheet.Cells(i + 19, j + 1).Value = Chr(39) & rec.GetCharValueAt (j)
					ElseIf field.IsNumeric Then
						oSheet.Cells(i + 19, j + 1).Value = rec.GetNumValueAt(j)
					ElseIf field.IsDate Then 
						oSheet.Cells(i + 19, j + 1).Value = rec.GetDateValueAt(j) 
					ElseIf field.IsTime Then 
						oSheet.Cells(i + 19, j + 1).Value = rec.GetTimeValueAt(j) 
					End If
					sMsg = "處理進度 3/1 - 資料處理項目 [step1 完整性測試]..." & Chr(10) & "欄位 " & field.name & " 資料匯出中 .." & i & "/" & db.count
					dlgText "WPMsgText", sMsg
					'P = P + 1
				Next i
	Next j

	oSheet.Protect PW, DrawingObjects:=False, Contents:=False, Scenarios:=False
	oSheet.Cells.Locked = False   '解鎖
	'oSheet.Range("B19:F" & 19+db.count ).Locked = True  '鎖定特定儲存格 
	oSheet.Range("B15:F" & 19+db.count ).Locked = True  '鎖定特定儲存格 
	oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True
	
	
	
	Set oSheet = oBook.Worksheets.Item("step1-1 借貸不平測試")
	
	sMsg = "處理進度 4/13 - 資料處理項目 [step1-1 借貸不平測試] ..."
	dlgText "WPMsgText", sMsg 

	oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
	oSheet.Range("A2").value = "測試資料期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date
	oSheet.Range("A3").value = "財務報表準備期間 - 開始日 : " & sLast_Accounting_Period_Date
	
	If Z_File_Exist("#GL_#Doc_not_Balance_Sum.IDM") = True Then 
	
		If FindField("#GL_#Doc_not_Balance_Sum.IDM","NO_OF_RECS1") <> 0 Then Call X_RemoveField("#GL_#Doc_not_Balance_Sum.IDM","NO_OF_RECS1")
		
		If GetTotal("#GL_#Doc_not_Balance_Sum.IDM" ,"" ,"DBCount" ) <> 0 Then 
			oSheet.Range("B12").value = "基於上述程序，查核團隊發現有部分傳票借貸不平，但已取得足夠的查核證據，確認其理由尚屬合理。"
			oSheet.Range("B14").value = "出現借貸不平之個別傳票說明："
			
	
			oSheet.Range("B16").value = "傳票號碼"
			oSheet.Range("C16").value = "總帳日期"
			oSheet.Range("D16").value = "借方金額"
			oSheet.Range("E16").value = "貸方金額"
			oSheet.Range("F16").value = "借貸不平的理由"
			
			oSheet.Range("B12:F16").Font.Bold = True
			oSheet.Range("B12:F16").Font.Name = "Calibri"
			oSheet.Range("B12:F16").Font.Size = 12
			oSheet.Range("B12:B16").Font.ColorIndex = 3
			oSheet.Range("B16:F16").Font.ColorIndex = 2
			oSheet.Range("B16:F16").Interior.Color = RGB(0, 102, 255)
			
	
			Set db = client.OpenDatabase("#GL_#Doc_not_Balance_Sum.IDM")
			Set ThisTable = db.TableDef
			Set rs = db.RecordSet		
		
			p = 1
			iFieldCount = ThisTable.count
			For j = 1 To iFieldCount
				Set field = ThisTable.GetFieldAt (j) 
						rs.ToFirst
						For i = 1 To db.count
						Set rec = rs.ActiveRecord
							rs.Next
				
							If field.IsCharacter Then 
								oSheet.Cells(i + 16, j + 1).Value = Chr(39) & rec.GetCharValueAt (j)
							ElseIf field.IsNumeric Then
								oSheet.Cells(i + 16, j + 1).Value = rec.GetNumValueAt(j)
							ElseIf field.IsDate Then 
								oSheet.Cells(i + 16, j + 1).Value = rec.GetDateValueAt(j) 
							ElseIf field.IsTime Then 
								oSheet.Cells(i + 16, j + 1).Value = rec.GetTimeValueAt(j) 
							End If
							sMsg = "處理進度 4/13 - 資料處理項目 [step1-1 借貸不平測試]..." & Chr(10) & "欄位 " & field.name & " 資料匯出中" & i & "/" & db.count
							dlgText "WPMsgText", sMsg
							P = P + 1
						Next i
			Next j
		
			oSheet.Protect PW, DrawingObjects:=False, Contents:=False, Scenarios:=False
			oSheet.Cells.Locked = False   '解鎖
			'oSheet.Range("B16:E" & 16+db.count ).Locked = True  '鎖定特定儲存格 
			oSheet.Range("B12:E" & 16+db.count ).Locked = True  '鎖定特定儲存格 
			oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True
				
		Else
			oSheet.Range("B12").value = "基於上述程序，查核團隊已取得足夠的查核證據，確認無借貸不平之情形。"
		End If 	
	Else
		oSheet.Range("B12").value = "基於上述程序，查核團隊已取得足夠的查核證據，確認無借貸不平之情形。"	
	End If

	Set oSheet = oBook.Worksheets.Item("step1-2 分錄編製人員說明")
	
	sMsg = "處理進度 5/13 - 資料處理項目 [step1-2 分錄編製人員說明] ..."
	dlgText "WPMsgText", sMsg 

	oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
	oSheet.Range("A2").value = "測試資料期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date
	oSheet.Range("A3").value = "財務報表準備期間 - 開始日 : " & sLast_Accounting_Period_Date
	
	If Z_File_Exist("#PreScr-R5-Sum.IDM") = True Then 

		If GetTotal("#PreScr-R5-Sum.IDM" ,"" ,"DBCount" ) <> 0 Then 
	
			Set db = client.OpenDatabase("#PreScr-R5-Sum.IDM")
			Set ThisTable = db.TableDef
			Set rs = db.RecordSet		
		
			p = 1
			iFieldCount = ThisTable.count
			For j = 1 To iFieldCount
				Set field = ThisTable.GetFieldAt (j) 
						rs.ToFirst
						For i = 1 To db.count
						Set rec = rs.ActiveRecord
							rs.Next
								'Derek Modify 2019.01.28
								If field.Name = "傳票建立人員_JE" Then 
									If field.IsCharacter Then oSheet.Cells(i + 11, 2).Value = Chr(39) & rec.GetCharValueAt(j)
									If field.IsNumeric Then oSheet.Cells(i + 11, 2).Value = Chr(39) & rec.GetNumValueAt(j)
								End If
								If field.Name = "人工傳票否_JE_S" Then 
									If rec.GetNumValueAt(j) = 0 Then  
										oSheet.Cells(i + 11, 3).Value = "自動拋轉分錄"
									Else
										oSheet.Cells(i + 11, 3).Value = "人工建立分錄"
									End If
								End If
								If field.Name = "NO_OF_RECS" Then oSheet.Cells(i + 11, 4).Value = Chr(39) & rec.GetNumValueAt (j)
								If field.Name = "DEBIT_傳票金額_JE_T_SUM" Then oSheet.Cells(i + 11, 5).Value = Chr(39) & rec.GetNumValueAt(j)

							sMsg = "處理進度 5/13 - 資料處理項目 [step1-2 分錄編製人員說明]..." & Chr(10) & "欄位 " & field.name & " 資料匯出中 .." & i & "/" & db.count
							dlgText "WPMsgText", sMsg
							P = P + 1
						Next i
			Next j
		
			oSheet.Protect PW, DrawingObjects:=False, Contents:=False, Scenarios:=False
			oSheet.Cells.Locked = False   '解鎖
			oSheet.Range("B12:E" & 12+db.count ).Locked = True  '鎖定特定儲存格 
			oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True
				
		End If 	
		
	Else
	
		oSheet.Range("B13").value = "請注意，該測試作業於總帳資料欄位配對作業程序並挑選編制人員欄位。" 
		oSheet.Range("B13").Font.Bold = True
		oSheet.Range("B13").Font.Name = "Calibri"
		oSheet.Range("B13").Font.Size = 12
		oSheet.Range("B13").Font.ColorIndex = 3

	End If 
		
		
	sMsg = "處理進度 6/13 - 資料處理項目 [step2 可靠性測試] ..."
	dlgText "WPMsgText", sMsg 
	
	Set oSheet = oBook.Worksheets.Item("step2 可靠性測試")

	oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
	oSheet.Range("A2").value = "財務報表期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date



	Set oSheet = oBook.Worksheets.Item("step1-3 完整性測試之差異說明")
	
	sMsg = "處理進度 7/13 - 資料處理項目 [step1-3 完整性測試之差異說明] ..."
	dlgText "WPMsgText", sMsg 
	
	'2019.05.22 Add
	If GetTotal("#List_of_accounts_with_variance.IDM" ,"" ,"DBCount" ) > 0 Then

		oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
		oSheet.Range("A2").value = "測試資料期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date
		oSheet.Range("A3").value = "財務報表準備期間 - 開始日 : " & sLast_Accounting_Period_Date
		
		Set db = client.OpenDatabase("#List_of_accounts_with_variance.IDM")
		Set ThisTable = db.TableDef
		Set rs = db.RecordSet		
			
		p = 1
		iFieldCount = ThisTable.count
		For j = 1 To iFieldCount
			Set field = ThisTable.GetFieldAt (j) 
				rs.ToFirst
				For i = 1 To db.count
				Set rec = rs.ActiveRecord
					rs.Next
						If field.Name = "ACCOUNT_NUM_ALL" Then oSheet.Cells(i + 16, 2).Value = Chr(39) & rec.GetCharValueAt(j)
						If field.Name = "會計科目名稱_TB" Then oSheet.Cells(i + 16, 3).Value = Chr(39) & rec.GetCharValueAt(j)
						If field.Name = "DIFF" Then oSheet.Cells(i + 16, 4).Value = Chr(39) & rec.GetNumValueAt (j)
					
					sMsg = "處理進度 7/13 - 資料處理項目 [step1-3 完整性測試之差異說明]..." & Chr(10) & "Field " & field.name & " exprt record .." & i & "/" & db.count
					dlgText "WPMsgText", sMsg
					'P = P + 1 renark 20200218
				Next i
		Next j
			
		oSheet.Protect PW, DrawingObjects:=False, Contents:=False, Scenarios:=False
		oSheet.Cells.Locked = False   '解鎖
		oSheet.Range("B17:D" & 17+db.count ).Locked = True  '鎖定特定儲存格 
		oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True
					
	Else
		 sExcel.DisplayAlerts = False
		 oBook.Sheets("step1-3 完整性測試之差異說明").Delete
		 sExcel.DisplayAlerts = True
		 'oBook.Worksheets.Item("step1-3 完整性測試之差異說明")
	End If


	sMsg = "處理進度 8/13 - 資料處理項目 [step3 高風險條件彙總] ..."
	dlgText "WPMsgText", sMsg 
	
	Set oSheet = oBook.Worksheets.Item("step3 高風險條件彙總")

	oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
	oSheet.Range("A2").value = "財務報表期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date


	'If sPopulation = 0 Then 
	'	oSheet.Range("B7").value = "期末財務報表準備期間之會計分錄"
	'	oSheet.Range("B9").value = "查核團隊考量舞弊風險相關之分錄，只存在於期末的財務報表準備期間。" & Chr(10) & _
	'				"在作業2.14.2中，記錄與舞弊風險因子相關之管理階層誘因係以收入/利潤目標來衡量績效的。關於收入/利潤時點的不實表達，例如，" & _
	'				" 偽造的開帳單並代管(bill and hold)的交易或者未記錄的費用，一般只會影響期末，而不會考量此風險會存在於整個財務報導期間。" & _
	'				"查核團隊推斷收入/利潤時點不實表達風險的分錄，存在於期末。因此，決定不篩選整個財務報導期間的會計分錄。"
	'Else
		oSheet.Range("B7").value = "期末財務報表準備期間與整個財務報導期間之會計分錄"
		oSheet.Range("B9").value = "查核團隊考量舞弊風險相關之分錄，除了會於期末財務報表準備期間發生外，某些與舞弊風險因子相關之分錄類型(例如：收入)"  & _
					"亦會在整個財務報導期間中被記錄。這些都將會對財務報表的正確性造成影響。因此，除測試期末財務報表準備期間之分錄外，" & _
					" 亦將對整個財務報表期間之會計分錄母體，篩選與舞弊風險類型相關之分錄。"		
	'End If

	P = 19
	sLogMemo = "" 
	For i = 1 To 10 
		If WPselect(i) = 1 Then 
			SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
			SQLeqn = "Select * from [JE_Criteria_Log] where SEQ_NUM = " & Chr(39) & "W" & i & Chr(39) 

			Set SQLobjConn = CreateObject("ADODB.Connection")
			SQLobjConn.open SQLconnStr
				Set SQLrs = SQLobjConn.execute(SQLeqn)
					oSheet.Range("C" & p).value = SQLrs.Fields("Criteria_Log")
					'oSheet.Range("E" & p).value = SQLrs.Fields("SumCheck")
					If Val(SQLrs.Fields("DocNum")) <> -1 Then
						oSheet.Range("E" & p).value = Val(SQLrs.Fields("DocNum"))
					Else
						oSheet.Range("E" & p).value = "Over 2,500"
					End If
					If i = 1 Then 
						oSheet.Range("D" & p).value = sRationale1
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale1 & Chr(10) & Chr(10)
					End If					
					If i = 2 Then 
						oSheet.Range("D" & p).value = sRationale2
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale2 & Chr(10) & Chr(10)
					End If 
					If i = 3 Then 
						oSheet.Range("D" & p).value = sRationale3
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale3 & Chr(10) & Chr(10)
					End If
					If i = 4 Then 
						oSheet.Range("D" & p).value = sRationale4
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale4 & Chr(10) & Chr(10)
					End If
					If i = 5 Then 
						oSheet.Range("D" & p).value = sRationale5
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale5 & Chr(10) & Chr(10)
					End If
					If i = 6 Then 
						oSheet.Range("D" & p).value = sRationale6
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale6 & Chr(10) & Chr(10)
					End If
					If i = 7 Then 
						oSheet.Range("D" & p).value = sRationale7
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale7 & Chr(10) & Chr(10)
					End If
					If i = 8 Then 
						oSheet.Range("D" & p).value = sRationale8
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale8 & Chr(10) & Chr(10)
					End If
					If i = 9 Then 
						oSheet.Range("D" & p).value = sRationale9
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale9 & Chr(10) & Chr(10)
					End If
					If i = 10 Then 
						oSheet.Range("D" & p).value = sRationale10
						sLogMemo = sLogMemo +  "Criteria Select " & i & " - " & sRationale10 & Chr(10) & Chr(10)
					End If
					
					p = p + 1
				Set SQLrs = Nothing
			Set SQLobjConn =  Nothing
		End If 
	Next i 	
	
	oSheet.Protect PW, DrawingObjects:=False, Contents:=False, Scenarios:=False
	oSheet.Cells.Locked = False   '解鎖
	oSheet.Range("B7:E28").Locked = True  '鎖定特定儲存格 
	oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True



	
	sMsg = "處理進度 9/13 - 資料處理項目 [step4 符合高風險條件傳票] ..."
	dlgText "WPMsgText", sMsg 
	
	Set oSheet = oBook.Worksheets.Item("step4 符合高風險條件傳票")

	oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
	oSheet.Range("A2").value = "財務報表期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date
	
	Set db = client.OpenDatabase("#To_WP_Sum.IDM")
	Set ThisTable = db.TableDef
	Set rs = db.RecordSet		

	p = 1
	Q = 0
	r1 = 0
	r2 = 0
	sTemp = ""
	iFieldCount = ThisTable.count
	For j = 1 To iFieldCount
		Set field = ThisTable.GetFieldAt (j)
			rs.ToFirst
			For i = 1 To db.count
				Set rec = rs.ActiveRecord
				rs.Next
				If field.Name = "傳票號碼_JE" Then 
					oSheet.Cells(i + 12, 2).Value = Chr(39) & rec.GetCharValueAt (j)
					oSheet.Cells(i + 12, 1).Value = i 
				End If
				If field.Name = "總帳日期_JE" Then oSheet.Cells(i + 12, 3).Value = Chr(39) & rec.GetCharValueAt (j)
				'Derek Modify 2019.01.28
				If field.Name = "傳票建立人員_JE" Then 
					If field.IsCharacter Then oSheet.Cells(i + 12, 4).Value = Chr(39) & rec.GetCharValueAt (j)
					If field.IsNumeric Then oSheet.Cells(i + 12, 4).Value = Chr(39) & rec.GetNumValueAt(j)
				End If
				If field.Name = "DEBIT_傳票金額_JE_T_SUM" Then oSheet.Cells(i + 12, 5).Value = rec.GetNumValueAt (j)
				If field.Name = "C1" Then oSheet.Cells(i + 12, 6).Value = rec.GetCharValueAt (j)
				If field.Name = "C2" Then oSheet.Cells(i + 12, 7).Value = rec.GetCharValueAt (j)
				If field.Name = "C3" Then oSheet.Cells(i + 12, 8).Value = rec.GetCharValueAt (j)
				If field.Name = "C4" Then oSheet.Cells(i + 12, 9).Value = rec.GetCharValueAt (j)
				If field.Name = "C5" Then oSheet.Cells(i + 12, 10).Value = rec.GetCharValueAt (j)
				If field.Name = "C6" Then oSheet.Cells(i + 12, 11).Value = rec.GetCharValueAt (j)
				If field.Name = "C7" Then oSheet.Cells(i + 12, 12).Value = rec.GetCharValueAt (j)
				If field.Name = "C8" Then oSheet.Cells(i + 12, 13).Value = rec.GetCharValueAt (j)
				If field.Name = "C9" Then oSheet.Cells(i + 12, 14).Value = rec.GetCharValueAt (j)
				If field.Name = "C10" Then oSheet.Cells(i + 12, 15).Value = rec.GetCharValueAt (j)
				sMsg = "處理進度 9/13 - 資料處理項目 [step4 符合高風險條件傳票]..." & Chr(10) & "欄位 " & field.name & " 資料匯出中 .." & i & "/" & db.count
				dlgText "WPMsgText", sMsg
			Next i
	Next j
	
	
	'設定某一區塊儲存格鎖定無法修改
	
	oSheet.Protect PW, DrawingObjects:=False, Contents:=False, Scenarios:=False
	oSheet.Cells.Locked = False   '解鎖
	oSheet.Range("A13:O" & 13+db.count ).Locked = True  '鎖定特定儲存格 
	'oSheet.Range("A11:O11").AutoFilter
	'oSheet.EnableAutoFilter = True
	oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True ', userInterfaceOnly:=True
	
	sMsg = "處理進度 10/13 - 資料處理項目 [step4-1 符合高風險條件傳票明細] ..."
	dlgText "WPMsgText", sMsg 
	
	Set oSheet = oBook.Worksheets.Item("step4-1 符合高風險條件傳票明細")

	oSheet.Range("A1").value = "公司名稱 : " & sEngagement_Info 
	oSheet.Range("A2").value = "財務報表期間 :  " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date
	
	strr = Client.WorkingDirectory  & "Exports.ILB\step4-1 符合高風險條件傳票明細.xlsx"
	Call Z_ExportDatabaseXLSX("#To_WP.IDM", "step4-1 符合高風險條件傳票明細.xlsx" , "step4-1 符合高風險條件傳票明細")
	Set oSheet = oBook.Worksheets.Item("step4-1 符合高風險條件傳票明細")
	Set oBook2=sExcel.Workbooks.Open(strr)
	Set oSheet2=oBook2.Worksheets.item("step4-1 符合高風險條件傳票明細")
	Set oRange=oSheet2.UsedRange
	oRange.Copy
	oSheet.Range( "A5" ).PasteSpecial
	sExcel.CutCopyMode = False
	oBook2.Save
	oBook2.Close (True)
	Kill strr 	
	
	
	sMsg = "處理進度 11/13 - 資料處理項目 [Addition 自動化工具-檔案欄位資訊] ..."
	dlgText "WPMsgText", sMsg 
	
	
	Set oSheet = oBook.Worksheets.Item("自動化工具-檔案欄位資訊")
	
	oSheet.Range("A1" ).Value = sProgVer
	oSheet.Range("A1").Font.Bold = True
	oSheet.Range("A1").Font.Name = "Calibri"
	oSheet.Range("A1").Font.Size = 16
	oSheet.Range("A1").Font.ColorIndex = 3
	oSheet.Range("A1").Interior.Color = RGB(0, 102, 255)
	
	oSheet.Range("A2" ).Value = "TB檔案配對前後欄位對照表"
	
	oSheet.Range("E3").Value ="配對後欄位名稱"
	oSheet.Range("B3").Value ="欄位型態"
	oSheet.Range("C3").Value ="文字長度"
	oSheet.Range("D3").Value ="小數位數"
	oSheet.Range("A3").Value ="配對前欄位名稱"
	oSheet.Range("A3:E3").Font.Bold = True
	oSheet.Range("A3:E3").Font.Name = "Calibri"
	oSheet.Range("A3:E3").Font.Size = 12
	oSheet.Range("A3:E3").Interior.Color = RGB(240, 240, 0)

	i = 3
		
	Set db = client.openDatabase("#TB#.IDM")
	Set thistable = db.tabledef
	iFieldCount = ThisTable.count
	For j = 1 To iFieldCount
		Set field = ThisTable.GetFieldAt (j)
		iFieldType = field.Type
		
		i = i + 1
		If iRight(field.name,3) = "_TB" Then oSheet.Range("E" & i ).Value = field.name	
		
		Select Case iFieldType
			Case WI_NUM_FIELD, WI_VIRT_NUM, WI_BOOL, WI_MULTISTATE  
				oSheet.Range("B" & i ).Value = "數字型態"
				oSheet.Range("D" & i ).Value = field.Decimals
				'iDecimals = field.Decimals
				'bImpliedDecimals = field.IsImpliedDecimal
				'sEqn = field.Equation
			Case WI_CHAR_FIELD,3
				oSheet.Range("B" & i ).Value = "文字型態"
				oSheet.Range("C" & i ).Value = field.Length
				'iLen = field.Length
			Case WI_VIRT_CHAR
				oSheet.Range("B" & i ).Value = "文字型態"
				oSheet.Range("C" & i ).Value = field.Length
				'iLen = field.Length
				'sEqn = field.Equation
			Case WI_DATE_FIELD,  WI_VIRT_DATE
				oSheet.Range("B" & i ).Value = "日期型態"
				'sEqn = field.Equation
			Case WI_TIME_FIELD, WI_VIRT_TIME
				oSheet.Range("B" & i ).Value = "時間型態"
		End Select
		If field.Description = "" Then 
			oSheet.Range("A" & i ).Value = field.name
		Else
			oSheet.Range("A" & i ).Value = field.Description
		End If 
			
	Next j 


	i = i + 2
	p = i

	oSheet.Range("A" & i  ).Value = "GL檔案配對前後欄位對照表"
	
	i = i + 1
	
	oSheet.Range("E"  & i ).Value ="配對後欄位名稱"
	oSheet.Range("B"  & i ).Value ="欄位型態"
	oSheet.Range("C"  & i ).Value ="文字長度"
	oSheet.Range("D"  & i ).Value ="小數位數"
	oSheet.Range("A"  & i ).Value ="配對前欄位名稱"
	oSheet.Range("A"  & i  & ":E"  & i ).Font.Bold = True
	oSheet.Range("A"  & i  & ":E"  & i ).Font.Name = "Calibri"
	oSheet.Range("A"  & i  & ":E"  & i  ).Font.Size = 12
	oSheet.Range("A"  & i  & ":E"  & i ).Interior.Color = RGB(230, 230, 0)	
	

	If FindField("#GL#DESC.IDM","DEBIT_傳票金額_JE_T") <> 0 Then Call X_RemoveField("#GL#DESC.IDM","DEBIT_傳票金額_JE_T")
	If FindField("#GL#DESC.IDM","CREDIT_傳票金額_JE_T") <> 0 Then Call X_RemoveField("#GL#DESC.IDM","CREDIT_傳票金額_JE_T")
	If FindField("#GL#DESC.IDM","DEBIT_CREDIT_JE_T") <> 0 Then Call X_RemoveField("#GL#DESC.IDM","DEBIT_CREDIT_JE_T")
	
	Set db = client.openDatabase("#GL#DESC.IDM")
	Set thistable = db.tabledef
	iFieldCount = ThisTable.count
	For j = 1 To iFieldCount
		Set field = ThisTable.GetFieldAt (j)
		iFieldType = field.Type
		
		i = i + 1
		
		If iRight(field.name,3) = "_JE" Or  iRight(field.name,5) = "_JE_S" Then oSheet.Range("E" & i ).Value = field.name	
		
		Select Case iFieldType
			Case WI_NUM_FIELD, WI_VIRT_NUM, WI_BOOL, WI_MULTISTATE  
				oSheet.Range("B" & i ).Value = "數字型態"
				oSheet.Range("D" & i ).Value = field.Decimals
				'iDecimals = field.Decimals
				'bImpliedDecimals = field.IsImpliedDecimal
				'sEqn = field.Equation
			Case WI_CHAR_FIELD,3
				oSheet.Range("B" & i ).Value = "文字型態"
				oSheet.Range("C" & i ).Value = field.Length
				'iLen = field.Length
			Case WI_VIRT_CHAR
				oSheet.Range("B" & i ).Value = "文字型態"
				oSheet.Range("C" & i ).Value = field.Length
				'iLen = field.Length
				'sEqn = field.Equation
			Case WI_DATE_FIELD,  WI_VIRT_DATE
				oSheet.Range("B" & i ).Value = "日期型態"
				'sEqn = field.Equation
			Case WI_TIME_FIELD, WI_VIRT_TIME
				oSheet.Range("B" & i ).Value = "時間型態"
		End Select
		
		If field.Description = "" Then
			oSheet.Range("A" & i ).Value = field.name
		Else
			oSheet.Range("A" & i ).Value = field.Description
		End If
			
	Next j 
	
	For j = 1 To  5
		oSheet.Columns(j).EntireColumn.AutoFit
	Next j	
	
	oSheet.Range("A1" ,"E1" ).Merge
	oSheet.Range("A" & p,"E" & P).Merge
	
	oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True		
	
	
	sMsg = "處理進度 12/13 - 資料處理項目 [Addition 自動化工具-假期假日資訊] ..."
	dlgText "WPMsgText", sMsg 
	
	Set oSheet = oBook.Worksheets.Item("自動化工具-假期假日資訊")
		
	If Z_File_Exist("#Weekend.IDM") Then
		
		i = 1	
		
		Set db = client.OpenDatabase("#Weekend.IDM")
		Set ThisTable = db.TableDef
		Set rs = db.RecordSet		
	
		iFieldCount = ThisTable.count
		For p = 1 To ThisTable.count
		Set field = ThisTable.GetFieldAt (p)
				oSheet.Cells( i, P).Value = field.name
				oSheet.Cells( i,P).Font.Bold = True
				oSheet.Cells( i,P).Font.Name = "Calibri"
				oSheet.Cells( i,P).Font.Size = 12
		Next p
	
		For j = 1 To iFieldCount
			Set field = ThisTable.GetFieldAt (j) 
					rs.ToFirst
					For p = 1 To db.count
					Set rec = rs.ActiveRecord
						rs.Next
			
						If field.IsCharacter Then 
							oSheet.Cells(i + p, j).Value = Chr(39) & rec.GetCharValueAt (j)
						ElseIf field.IsNumeric Then
							oSheet.Cells(i + p, j).Value = rec.GetNumValueAt(j)
						ElseIf field.IsDate Then 
							oSheet.Cells(i + p, j).Value = rec.GetDateValueAt(j) 
						ElseIf field.IsTime Then 
							oSheet.Cells(i + p, j).Value = rec.GetTimeValueAt(j) 
						End If
					Next i
		Next j		

		i = i + 2 +  db.count
	Else 
		oSheet.Cells( 1, 1).Value = "未設定假日資訊"
		i = 3
	End If
	
	If Z_File_Exist("#Holiday#-Holiday.IDM") Then

		Set db = client.OpenDatabase("#Holiday#-Holiday.IDM")
		Set ThisTable = db.TableDef
		Set rs = db.RecordSet		
	
		iFieldCount = ThisTable.count
		For p = 1 To ThisTable.count
		Set field = ThisTable.GetFieldAt (p)
				oSheet.Cells( i, P).Value = field.name
				oSheet.Cells( i,P).Font.Bold = True
				oSheet.Cells( i,P).Font.Name = "Calibri"
				oSheet.Cells( i,P).Font.Size = 12
		Next p
	
		For j = 1 To iFieldCount
			Set field = ThisTable.GetFieldAt (j) 
					rs.ToFirst
					For p = 1 To db.count
					Set rec = rs.ActiveRecord
						rs.Next
			
						If field.IsCharacter Then 
							oSheet.Cells(i + p, j).Value = Chr(39) & rec.GetCharValueAt (j)
						ElseIf field.IsNumeric Then
							oSheet.Cells(i + p, j).Value = rec.GetNumValueAt(j)
						ElseIf field.IsDate Then 
							oSheet.Cells(i + p, j).Value = Chr(39) & iLeft(rec.GetDateValueAt(j),4) & "/" & imid(rec.GetDateValueAt(j),5,2) & "/" & iRight(rec.GetDateValueAt(j),2)
							'oSheet.Cells(i + p, j).NumberFormatLocal = "YYYY/MM/DD"
						ElseIf field.IsTime Then 
							oSheet.Cells(i + p, j).Value = rec.GetTimeValueAt(j) 
						End If
					Next i
		Next j	
		
		i = i + 2 +  db.count
	
		For j = 1 To  3
			oSheet.Columns(j).EntireColumn.AutoFit
		Next j	
	Else
		i = i + 2
		oSheet.Cells( i, 1).Value = "未設定假期資訊"
	End If

	If Z_File_Exist("#MakeUpDay#-Make-Up_Day.IDM") Then

		Set db = client.OpenDatabase("#MakeUpDay#-Make-Up_Day.IDM")
		Set ThisTable = db.TableDef
		Set rs = db.RecordSet		
	
		iFieldCount = ThisTable.count
		For p = 1 To ThisTable.count
		Set field = ThisTable.GetFieldAt (p)
				oSheet.Cells( i, P).Value = field.name
				oSheet.Cells( i,P).Font.Bold = True
				oSheet.Cells( i,P).Font.Name = "Calibri"
				oSheet.Cells( i,P).Font.Size = 12
		Next p
	
		For j = 1 To iFieldCount
			Set field = ThisTable.GetFieldAt (j) 
					rs.ToFirst
					For p = 1 To db.count
					Set rec = rs.ActiveRecord
						rs.Next
			
						If field.IsCharacter Then 
							oSheet.Cells(i + p, j).Value = Chr(39) & rec.GetCharValueAt (j)
						ElseIf field.IsNumeric Then
							oSheet.Cells(i + p, j).Value = rec.GetNumValueAt(j)
						ElseIf field.IsDate Then 
							oSheet.Cells(i + p, j).Value = Chr(39) & iLeft(rec.GetDateValueAt(j),4) & "/" & imid(rec.GetDateValueAt(j),5,2) & "/" & iRight(rec.GetDateValueAt(j),2)
							'oSheet.Cells(i + p, j).NumberFormatLocal = "YYYY/MM/DD"
						ElseIf field.IsTime Then 
							oSheet.Cells(i + p, j).Value = rec.GetTimeValueAt(j) 
						End If
					Next i
		Next j	
	
		For j = 1 To  3
			oSheet.Columns(j).EntireColumn.AutoFit
		Next j	
	Else
		i = i + 2
		oSheet.Cells( i, 1).Value = "未設定補班日/結帳日資訊"
	End If
	
	oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True			
	
	
	sMsg = "處理進度 13/13 - 資料處理項目 [Addition 自動化工具-科目配對資訊] ..."
	dlgText "WPMsgText", sMsg 	
	
	Set oSheet = oBook.Worksheets.Item("自動化工具-科目配對資訊")
	
	If Z_File_Exist("#AccountMapping.IDM") Then	
						
		i = 1
		
		Set db = client.OpenDatabase("#AccountMapping.IDM")
		Set ThisTable = db.TableDef
		Set rs = db.RecordSet		
	
		iFieldCount = ThisTable.count
		For p = 1 To ThisTable.count
		Set field = ThisTable.GetFieldAt (p)
				oSheet.Cells( i, P).Value = field.name
				oSheet.Cells( i,P).Font.Bold = True
				oSheet.Cells( i,P).Font.Name = "Calibri"
				oSheet.Cells( i,P).Font.Size = 12
		Next p
	
		For j = 1 To iFieldCount
			Set field = ThisTable.GetFieldAt (j) 
					rs.ToFirst
					For p = 1 To db.count
					Set rec = rs.ActiveRecord
						rs.Next
			
						If field.IsCharacter Then 
							oSheet.Cells(i + p, j).Value = Chr(39) & rec.GetCharValueAt (j)
						ElseIf field.IsNumeric Then
							oSheet.Cells(i + p, j).Value = rec.GetNumValueAt(j)
						ElseIf field.IsDate Then 
							oSheet.Cells(i + p, j).Value = rec.GetDateValueAt(j) 
						ElseIf field.IsTime Then 
							oSheet.Cells(i + p, j).Value = rec.GetTimeValueAt(j) 
						End If
					Next i
		Next j	
		
		For j = 1 To  3
			oSheet.Columns(j).EntireColumn.AutoFit
		Next j	
	End If
	
	oSheet.Protect PW, DrawingObjects:=True, Contents:=True, Scenarios:=True		
				
	'2019/05/09 Add 保護活頁簿 不可被刪除或新增
	'oBook.Protect Structure:=True, Windows:=True
	
	Set oSheet = oBook.Worksheets.Item("step1 完整性測試")
	oSheet.Activate
	
	oBook.Save
	oBook.Close (True)
	sExcel.Quit		

	sMsg ="恭喜 您，Fianl JE W/P 已順利產出!!!"
	dlgText "WPMsgText", sMsg
	
	'If sVer = "TW" And UserName <> "derekchan" Then Call X_SendMail 
	'Call X_SendMail 

	sMsg = "JE工作底稿已產生且儲存路徑為 " & Client.WorkingDirectory  & "Exports.ILB\WorkingPaper.xlsx "
	
	Result = MsgBox(sMsg ,MB_OK Or MB_ICONINFORMATION , "恭喜您，Fianl JE W/P 已順利產出!!!")
			
	Set runScript =  CreateObject("WScript.Shell")
	runScript.Run "explorer.exe /e," & client.WorkingDirectory & "Exports.ILB"
			
End Function	

Function Step5_ToWP_Sum

	Set db = Client.OpenDatabase("#To_WP.IDM")
	Set task = db.Summarization
	task.AddFieldToSummarize "傳票號碼_JE"
	task.AddFieldToSummarize "總帳日期_JE"
	If FindField("#To_WP.IDM", "傳票建立人員_JE" ) <> 0  Then task.AddFieldToSummarize "傳票建立人員_JE"
	For i = 1 To 5 
		If FindField("#To_WP.IDM", "C" & i ) <> 0  Then task.AddFieldToSummarize "C" & i
	Next i
	task.AddFieldToTotal "DEBIT_傳票金額_JE_T"
	dbName = "#Temp1.IDM"
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.StatisticsToInclude = SM_SUM
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)

	If FindField("#To_WP.IDM", "C6" ) <> 0 Or FindField("#To_WP.IDM", "C7" ) <> 0 Or FindField("#To_WP.IDM", "C8" ) <> 0 Or FindField("#To_WP.IDM", "C9" ) <> 0 Or FindField("#To_WP.IDM", "C10" ) <> 0Then 
		Set db = Client.OpenDatabase("#To_WP.IDM")
		Set task = db.Summarization
		task.AddFieldToSummarize "傳票號碼_JE"
		task.AddFieldToSummarize "總帳日期_JE"
		If FindField("#To_WP.IDM", "傳票建立人員_JE" ) <> 0  Then task.AddFieldToSummarize "傳票建立人員_JE"
		For i = 6 To 10
			If FindField("#To_WP.IDM", "C" & i ) <> 0  Then task.AddFieldToSummarize "C" & i
		Next i	
		task.AddFieldToTotal "DEBIT_傳票金額_JE_T"	
		dbName = "#Temp2.IDM"
		task.OutputDBName = dbName
		task.CreatePercentField = FALSE
		task.StatisticsToInclude = SM_SUM
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)

		Set db = Client.OpenDatabase("#Temp1.IDM")
		Set task = db.JoinDatabase
		task.FileToJoin "#Temp2.IDM"
		task.IncludeAllPFields
		For i = 6 To 10
			If FindField("#Temp2.IDM", "C" & i ) <> 0  Then task.AddSFieldToInc "C" & i
		Next i		
		task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
		task.CreateVirtualDatabase = False
		dbName = "#To_WP_Sum.IDM"
		task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)
		
		Call Z_Delete_File("#Temp1.IDM")
		Call Z_Delete_File("#Temp2.IDM")
		
	Else
		Call Z_Rename_DB("#Temp1.IDM","#To_WP_Sum.IDM")		
	End If
	
	Set db = Client.OpenDatabase("#To_WP_Sum.IDM")
	Set task = db.Sort
	task.AddKey "傳票號碼_JE", "A"
	dbName = "#Temp1.IDM"
	task.PerformTask dbName
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)	
	
	Call Z_Rename_DB("#Temp1.IDM","#To_WP_Sum.IDM")

End Function 

Function Step5_WPdata_Collection

	Dim i As Integer
	Dim j As Integer
	Dim CriteriaSelect(10) As String
	Dim sTemp As String

	sMsg = "處理進度 0/13 - 資料彙整前處理程序.... 請稍後...."
	dlgText "WPMsgText", sMsg
		
	Call Z_Delete_File("#Final_Temp.IDM")
	Call Z_Delete_File("#Final.IDM")
	Call Z_Delete_File("#To_WP.IDM")
	Call Z_Delete_File("#To_WP_Sum.IDM")
			
	For i = 1 To 10
		CriteriaSelect(i) = "#CriteriaSelect" & i & ".IDM"
	Next i

	p = 1
	Call Z_Field_Info("#GL#.IDM","傳票號碼_JE")
	For i = 1 To 10 	
		If WPselect(i) = 1 Then 
		
			For j = 1 To 10 
				If FindField( CriteriaSelect(i), "C" & j ) <> 0 Then  Call X_RemoveField( CriteriaSelect(i), "C" & j)
			Next j 
			
			Call Z_Delete_File("#Temp.IDM")
		
			'20181023 Add
			If sVer = "TW" Then 
				If GetTotal(CriteriaSelect(i) ,"" ,"DBCount" ) <> 0 Then 
					Call Step4_Summarization_Line(CriteriaSelect(i), "傳票號碼_JE", "傳票文件項次_JE_S", "FINAL_TAG", "#TempC" & p & ".IDM")
					'Call X_RemoveField("#TempC" & p & ".IDM", "NO_OF_RECS")
					Result = Z_renameFields("#TempC" & p & ".IDM","FINAL_TAG", "C" & p & "_Tag")
				End If
			End If 
		
			If  Z_File_Exist("#Final_Temp.IDM") Then 
				Call Step4_Summarization(CriteriaSelect(i), "傳票號碼_JE", "#Temp.IDM")
				'20181022 Modify
				If GetTotal("#Temp.IDM" ,"" ,"DBCount" ) = 0 Then 
					Call X_AppendField(CriteriaSelect(i), "C" & p )
				Else
					Call X_AppendField("#Temp.IDM", "C" & p )
					Call X_JoinDatabase("#Final_Temp.IDM", "#Temp.IDM", "#Temp1.IDM", "傳票號碼_JE")
					Call X_RemoveField("#Temp1.IDM", "NO_OF_RECS1")
					Call Z_renameFields("#Temp1.IDM", "傳票號碼_JE", "傳票號碼_JE2") 
					Call X_AppendField_1("#Temp1.IDM", "傳票號碼_JE", sLen)
					Call X_RemoveField("#Temp1.IDM", "傳票號碼_JE1")
					Call X_RemoveField("#Temp1.IDM", "傳票號碼_JE2")
					Call Z_Delete_File("#Temp.IDM")
					Call Z_Delete_File("#Final_Temp.IDM")
					Call Z_Rename_DB("#Temp1.IDM","#Final_Temp.IDM")
				End If
				p = p + 1		
			Else
				Call Step4_Summarization(CriteriaSelect(i), "傳票號碼_JE", "#Temp.IDM")
			
				If GetTotal("#Temp.IDM" ,"" ,"DBCount" ) = 0 Then 
					Call X_AppendField(CriteriaSelect(i), "C" & p )
				Else
					Call X_AppendField("#Temp.IDM", "C" & p )
					Call Z_Rename_DB("#Temp.IDM","#Final_Temp.IDM")
				End If
				p = p + 1
			End If 
		End If 
	Next i

	If  Z_File_Exist("#Final_Temp.IDM") Then
		Call Z_DirectExtractionTable("#Final_Temp.IDM", "#Final.IDM" , "")
		Call Z_Delete_File("#Final_Temp.IDM")
		Call X_RemoveField("#Final.IDM", "NO_OF_RECS")
		
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.JoinDatabase
		task.FileToJoin "#Final.IDM"
		task.IncludeAllPFields
		task.IncludeAllSFields
		task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
		task.CreateVirtualDatabase = False
		dbName = "#To_WP.IDM"
		task.PerformTask dbName, "", WI_JOIN_MATCH_ONLY
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)
	
		Call X_RemoveField("#To_WP.IDM", "傳票號碼_JE1")
			
		Call Z_Delete_File("#Final.IDM")
	Else
		Call Z_DirectExtractionTable("#GL#.IDM", "#To_WP.IDM" , " 1 = 2 ")
	End If
	
	If sVer = "TW" Then 
		For i = 1 To 10 
			If  Z_File_Exist("#TempC" & i & ".IDM")  Then 
				If GetTotal("#TempC" & i & ".IDM" ,"" ,"DBCount" ) <> 0 Then 
					Set db = Client.OpenDatabase("#To_WP.IDM")
					Set task = db.JoinDatabase
					task.FileToJoin "#TempC" & i & ".IDM"
					task.IncludeAllPFields
					task.AddSFieldToInc "C" & i & "_TAG"
					task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
					task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
					task.CreateVirtualDatabase = False
					dbName = "#Temp.IDM"
					task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
					Set task = Nothing
					Set db = Nothing
					Client.OpenDatabase (dbName)
					
					Call Z_Rename_DB("#Temp.IDM","#To_WP.IDM")					
				End If
				Call Z_Delete_File("#TempC" & i & ".IDM")
			End If
		Next i 	
		
		Call Z_Delete_File("#Temp.IDM")	
		
		sTemp = "@Len("
		For i = 1 To 10 
			If FindField("#To_WP.IDM", "C" & i ) <> 0 Then sTemp = sTemp + "C" & i &  " + "	
		Next i 
		
		If iLen(sTemp) > 5 Then 
	
			sTemp = iLeft(sTemp, Len(sTemp) -2) + ")"
		
			Set db = Client.OpenDatabase("#To_WP.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "Count_Doc"
			field.Description = ""
			field.Type = WI_NUM_FIELD
			field.Equation = sTemp
			field.Decimals = 0
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
		End If

		sTemp = "@Len("
		For i = 1 To 10 
			If FindField("#To_WP.IDM", "C" & i & "_TAG") <> 0 Then sTemp = sTemp + "C" & i &  "_TAG + "	
		Next i 
		
		If iLen(sTemp) > 5 Then 
	
			sTemp = iLeft(sTemp, Len(sTemp) -2) + ")"
		
			Set db = Client.OpenDatabase("#To_WP.IDM")
			Set task = db.TableManagement
			Set field = db.TableDef.NewField
			field.Name = "Count_Tag"
			field.Description = ""
			field.Type = WI_NUM_FIELD
			field.Equation = sTemp
			field.Decimals = 0
			task.AppendField field
			task.PerformTask
			Set task = Nothing
			Set db = Nothing
			Set field = Nothing
		End If
							
		Set db = Client.OpenDatabase("#To_WP.IDM")
		Set task = db.Sort
		task.AddKey "COUNT_DOC", "D"
		task.AddKey "傳票號碼_JE", "A"
		task.AddKey "傳票文件項次_JE_S", "A"
		dbName = "#Temp.IDM"
		task.PerformTask dbName
		Set task = Nothing
		Set db = Nothing
		Client.OpenDatabase (dbName)
		
		Call Z_Rename_DB("#Temp.IDM","#To_WP.IDM")
					
	End If
	
End Function 

Function X_AppendField(sPDB As String, sField As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name =sField
	field.Description = ""
	field.Type =  WI_CHAR_FIELD
	field.Equation = """Y"""
	field.Length = 1
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing
End Function

Function X_AppendField_1(sPDB As String, sField As String, sLen As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = sField
	field.Description = ""
	field.Type =  WI_CHAR_FIELD
	field.Equation = "@If( 傳票號碼_JE2 <> """",  傳票號碼_JE2 ,  傳票號碼_JE1 )"
	field.Length = sLen
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing
End Function

Function X_AppendDatabase(sPDB As String, sSDB As String, sNewDB As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.AppendDatabase
	task.AddDatabase sSDB
	dbName = sNewDB
	task.PerformTask dbName, ""
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
End Function

Function X_JoinDatabase(sPDB As String, sSDB As String, sNewDB As String, sField As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.JoinDatabase
	task.FileToJoin sSDB
	task.IncludeAllPFields
	task.IncludeAllPFields
	task.IncludeAllSFields
	task.AddMatchKey sField, sField, "A"
	task.CreateVirtualDatabase = False
	dbName = sNewDB
	task.PerformTask dbName, "", WI_JOIN_ALL_REC
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
End Function

Function X_RemoveField(sPDB As String, sField As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.TableManagement
	task.RemoveField sField
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
End Function


Function RationaleForWP_DisplayIT(ControlID$, Action%, SuppValue%)
	Select Case Action%
		Case 1
			If sVer = "TW" And sL = "CHT" Then 
				DlgText "Text1", "請紀錄您的理由"
			End If
			
		Case 2
			Select Case ControlID
				Case "BtnSave" 
					
				Case "BtnCancel"
					
			End Select
	End Select
End Function


Function X_Get_Routines_Memo

	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Select * from [JE_Routines] " 

		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
			Set SQLrs = SQLobjConn.execute(SQLeqn)
			
				sA2_Memo = SQLrs.Fields("A2_Memo")
				sA3_Memo = SQLrs.Fields("A3_Memo")
				sA4_Memo = SQLrs.Fields("A4_Memo")
			
			Set SQLrs = Nothing
		Set SQLobjConn =  Nothing

End Function


Function Step5_rationale_Check

	sTemp = ""

	If  WPselect(1) = 1  And sRationale1 = "" Then sTemp = sTemp +  "一、" 
	If  WPselect(2) = 1  And sRationale2 = "" Then sTemp = sTemp +  "二、" 
	If  WPselect(3) = 1  And sRationale3 = "" Then sTemp = sTemp +  "三、" 
	If  WPselect(4) = 1  And sRationale4 = "" Then sTemp = sTemp +  "四、" 
	If  WPselect(5) = 1  And sRationale5 = "" Then sTemp = sTemp +  "五、" 
	If  WPselect(6) = 1  And sRationale6 = "" Then sTemp = sTemp +  "六、" 
	If  WPselect(7) = 1  And sRationale7 = "" Then sTemp = sTemp +  "七、" 
	If  WPselect(8) = 1  And sRationale8 = "" Then sTemp = sTemp +  "八、" 
	If  WPselect(9) = 1  And sRationale9 = "" Then sTemp = sTemp +  "九、" 
	If  WPselect(10) = 1  And sRationale10 = "" Then sTemp = sTemp +  "十、" 
	
	If sTemp <> "" Then 
		S1Check = 1
		sTemp = iLeft(sTemp, Len(sTemp)-1)
		sMsg = "篩選結果 " & sTemp & " 未輸入選擇該篩選結果的理由"
		Result = MsgBox(sMsg, MB_OK Or MB_ICONINFORMATION , "步驟五 未輸入必要資訊 警示訊息")
	End If

End Function


Function Z_DataField_Check(sSourceFile As String, sSourceField As String)

	Set db = Client.OpenDatabase(sSourceFile)
	Set stats = db.FieldStats(sSourceField)

	Z_DataField_Check = stats.NumDataErrors()

	Set db = Nothing
	Set stats = Nothing

End Function


Function Step4_Routines_Tag(sTagCheck As Integer)	

	Set db = Client.OpenDatabase("#Temp.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "K_TAG_Temp"
	field.Description = ""
	field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
	field.Equation = sLogMemo
	field.Decimals = 0
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing	

	Set db = Client.OpenDatabase("#Temp.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "Routine_TAG"
	field.Description = ""
	field.Type =  WI_CHAR_FIELD 'WI_VIRT_CHAR
	field.Equation = "@If( K_TAG_Temp = " & sTagCheck & " ," & Chr(34) & "Y" & Chr(34) & "," & Chr(34) & "N" & Chr(34) & ")"
	field.Length = 1
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing	
	
	Call X_RemoveField("#Temp.IDM","K_TAG_Temp")
	
	'Derek 2019.01.24 add

	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.JoinDatabase
	task.FileToJoin "#Temp.IDM"
	task.IncludeAllPFields
	task.AddSFieldToInc "Routine_TAG"
	task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
	task.AddMatchKey "傳票文件項次_JE_S", "傳票文件項次_JE_S", "A"
	task.CreateVirtualDatabase = False
	dbName = "#Temp1.IDM"
	task.PerformTask dbName, "", WI_JOIN_ALL_IN_PRIM
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)	
	
	Call Z_Rename_DB("#Temp1.IDM","#Temp.IDM")
		
End Function

Function  Step4_Tag_Collection(sTagCheck As Integer)

	If FindField("#Temp.IDM","CRITERIA_TAG") <> 0 Then

		Set db = Client.OpenDatabase("#Temp.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "CRITERIA_TAG_Check"
		field.Description = ""
		field.Type =  WI_CHAR_FIELD 'WI_VIRT_CHAR
		field.Equation = "@if(@Len(@AllTrim(@Remove( CRITERIA_TAG , " & Chr(34) & "N" & Chr(34) & "))) =  " & sTagCheck & " ," & Chr(34) & "Y" & Chr(34) & "," & Chr(34) & "N" & Chr(34) & ")"
		field.Length = 1
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
	
		Call X_RemoveField("#Temp.IDM","CRITERIA_TAG")

	End If 	
	
	
	Set db = Client.OpenDatabase("#Temp.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "Final_TAG"
	field.Description = ""
	field.Type =  WI_CHAR_FIELD 'WI_CHAR_FIELD 'WI_VIRT_CHAR
	If FindField("#Temp.IDM","CRITERIA_TAG_Check") <> 0  And FindField("#Temp.IDM","ROUTINE_TAG") <> 0 Then 
		field.Equation = "@if(CRITERIA_TAG_Check= " & Chr(34) & "Y" & Chr(34) & " .AND. ROUTINE_TAG = " & Chr(34) & "Y" & Chr(34) & "," & Chr(34) & "Y" & Chr(34) & " ," & Chr(34) & "" & Chr(34) & ")"
	End If
	If FindField("#Temp.IDM","CRITERIA_TAG_Check") = 0  And FindField("#Temp.IDM","ROUTINE_TAG") <> 0 Then 
		field.Equation = "@If(ROUTINE_TAG = " & Chr(34) & "Y" & Chr(34) & "," & Chr(34) & "Y" & Chr(34) & "," & Chr(34) & Chr(34) & ")"
	End If
	If FindField("#Temp.IDM","CRITERIA_TAG_Check") <> 0  And FindField("#Temp.IDM","ROUTINE_TAG") = 0 Then 
		field.Equation = "@If(CRITERIA_TAG_Check = " & Chr(34) & "Y" & Chr(34) & "," & Chr(34) & "Y" & Chr(34) & "," & Chr(34) & Chr(34) & ")"
	End If	
	field.Length = 1
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing
		
End Function 


Function X_SendMail 

	Dim OutApp As Object
	Dim OutMail As Object
	Dim sSubject As String
	Dim sMessage As String
	Dim i As Long
	Dim CrLf As String
	Dim C As Integer
	
	CrLf = Chr(10) & Chr(13)
	
	Call X_Get_Routines_Memo
	
	sLog = ""
	
	SQLconnStr = "Provider=SQLITEDB; Data source=" & sProjectFolder & "ProjectOverview.db"
	SQLeqn = "Select Count(1) as COT from [JE_Criteria_Log] "
	
		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
		Set SQLrs = SQLobjConn.execute(SQLeqn)
			C = SQLrs.Fields("COT")
		Set SQLrs = Nothing
		Set SQLobjConn =  Nothing
	
	For i = 1 To C
		SQLeqn = "Select * from [JE_Criteria_Log] where SEQ_NUM = " & Chr(39) & "W" & i & Chr(39) 
		Set SQLobjConn = CreateObject("ADODB.Connection")
		SQLobjConn.open SQLconnStr
		Set SQLrs = SQLobjConn.execute(SQLeqn)
			sLog = sLog + "Criteria Select " & i & " : " & SQLrs.Fields("Criteria_Log") & " / " & SQLrs.Fields("SumCheck") & Chr(10)
		Set SQLrs = Nothing
		Set SQLobjConn =  Nothing	
	Next i 
	 	
	Set OutApp = CreateObject("Outlook.Application")
	Set OutMail = OutApp.CreateItem(0)

		sSubject = "【JE Tool " & sProgVer & " 】WP Export Sucess for " & sEngagement_Info
		sMessage = "Program Version : " & sProgVer  & Chr(10)  &  _
			"Total GL Line : "  & GetTotal("#GL#.IDM" ,"" ,"DBCount" ) & Chr(10) & Chr(10) & _
			 " === General Information === " & Chr(10) & Chr(10) &  _
			"Engagement : " & sEngagement_Info & Chr(10) & _
			"Period Date : " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date & Chr(10) & _
			"End of Reporting Period - Start Date : " & sLast_Accounting_Period_Date & Chr(10) & _
			"Population : " & sPopulation & Chr(10) & _
			"Industry : " & sIndustry & Chr(10) & Chr(10) & _
			" === Additional Pre-screening criteria to be added by engagement team === " & Chr(10) & Chr(10) &  _
			"Additional specific description : "  & sA2_Memo  & Chr(10) &  _
			"Additional unexpected journal entries : "  & sA3_Memo  & Chr(10) &  _
			"Additional round numbers or consistent ending numbers : " & sA4_Memo  & Chr(10) & Chr(10) &  _
			" === Criteria Selection Result === " & Chr(10) & Chr(10) &  sLog & _
			" === WP Export  === " & Chr(10) & Chr(10) & sLogMemo
			
		On Error Resume Next
		' Change the mail address and subject in the macro before you run it.
		'MsgBox rs.ActiveRecord.GetCharValue("EMAIL_ADDRESS") 
		OutMail.To = "redacted@example.invalid" ' 
		'OutMail.To = "redacted@example.invalid;redacted@example.invalid;redacted@example.invalid" ' 
		OutMail.CC = ""
		OutMail.BCC = ""
		OutMail.Subject = sSubject
		OutMail.Body = sMessage
		' You can add other files by uncommenting the following line.
		'.Attachments.Add ("C:\test.txt")
		' In place of the following statement, you can use ".Display" to
		' display the mail.
		OutMail.Display
		OutMail.Send   
		On Error GoTo 0
		
	Set OutMail = Nothing
	Set OutApp = Nothing

End Function


Function Step4_Num_Acc_Select(sAccType As String)

	Call Z_Delete_File("#Temp_Account.IDM")

	Set db = Client.OpenDatabase("#GL#Account_Mapping.IDM")
	Set task = db.Extraction
	task.IncludeAllFields
	dbName = "#Temp_Account.IDM"
	task.AddExtraction dbName, "", sAccType
	task.CreateVirtualDatabase = False
	task.PerformTask 1, db.Count
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
End Function

Function Step5_ToWP_ReDo

	Set db = Client.OpenDatabase("#To_WP.IDM")
	Set task = db.Extraction
	
	Set ThisTable = db.TableDef
	Set rs = db.RecordSet
	iFieldCount = ThisTable.count
	For j = 1 To iFieldCount
		Set field = ThisTable.GetFieldAt (j)
		If iRight(field.Name,3) = "_JE" Or  iRight(field.name,5) = "_JE_S" Then task.AddFieldToInc field.Name
	Next j 
	If FindField("#To_WP.IDM","C1_TAG") <> 0   Then task.AddFieldToInc "C1_TAG"
	If FindField("#To_WP.IDM","C2_TAG") <> 0   Then task.AddFieldToInc "C2_TAG"
	If FindField("#To_WP.IDM","C3_TAG") <> 0   Then task.AddFieldToInc "C3_TAG"
	If FindField("#To_WP.IDM","C4_TAG") <> 0   Then task.AddFieldToInc "C4_TAG"
	If FindField("#To_WP.IDM","C5_TAG") <> 0   Then task.AddFieldToInc "C5_TAG"
	If FindField("#To_WP.IDM","C6_TAG") <> 0   Then task.AddFieldToInc "C6_TAG"
	If FindField("#To_WP.IDM","C7_TAG") <> 0   Then task.AddFieldToInc "C7_TAG"
	If FindField("#To_WP.IDM","C8_TAG") <> 0   Then task.AddFieldToInc "C8_TAG"
	If FindField("#To_WP.IDM","C9_TAG") <> 0   Then task.AddFieldToInc "C9_TAG"
	If FindField("#To_WP.IDM","C10_TAG") <> 0   Then task.AddFieldToInc "C10_TAG"
	dbName = "#Temp.IDM"
	task.AddExtraction dbName, "", ""
	task.CreateVirtualDatabase = False
	task.PerformTask 1, db.Count
	Set ThisTable = Nothing
	Set field   = Nothing
	Set rs = Nothing
	Set task = Nothing
	Set db = Nothing
	'Client.OpenDatabase (dbName)
	
	Call Step5_ToWP_ReDo_Sort
	
End Function

Function Step5_ToWP_ReDo_Sort


	Client.CloseDatabase ("#To_WP.IDM")
	Call Z_Delete_File("#To_WP.IDM")

	Call Z_Rename_DB("#Temp.IDM","#To_WP.IDM")
	
	Set db = Client.OpenDatabase("#To_WP.IDM")
	Set task = db.Sort
	task.AddKey "傳票號碼_JE", "A"
	task.AddKey "傳票文件項次_JE_S", "A"
	dbName = "#Temp1.IDM"
	task.PerformTask dbName
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)	
	
	Call Z_Rename_DB("#Temp1.IDM","#To_WP.IDM")	


End Function

Function X_SendMail_Step(sStep As String) 

	Dim OutApp As Object
	Dim OutMail As Object
	Dim sSubject As String
	Dim sMessage As String
	Dim i As Long
	Dim CrLf As String
	Dim C As Integer
	

	Set OutApp = CreateObject("Outlook.Application")
	Set OutMail = OutApp.CreateItem(0)

		sSubject = "【JE Tool】" & sEngagement_Info & " - " &  sStep & " Done!!"
		sMessage = "Program Version : " & sProgVer  & Chr(10)  &  _
			"Engagement : " & sEngagement_Info & Chr(10) & _
			"Period Date : " & sPeriod_Start_Date & " ~ " & sPeriod_End_Date & Chr(10) & _
			"End of Reporting Period - Start Date : " & sLast_Accounting_Period_Date & Chr(10) & _
			"Population : " & sPopulation & Chr(10) & _
			"Industry : " & sIndustry & Chr(10) & Chr(10) & _
			" === Run Time === " & Chr(10) & Chr(10) &  _
			"Start : "  & Run_S_Time  & Chr(10) &  _
			"End   : "  & Run_E_Time  & Chr(10) 
			
		On Error Resume Next
		' Change the mail address and subject in the macro before you run it.
		'MsgBox rs.ActiveRecord.GetCharValue("EMAIL_ADDRESS") 
		OutMail.To = UserName & "@kpmg.com.tw"  
		OutMail.CC = ""
		OutMail.BCC = ""
		OutMail.Subject = sSubject
		OutMail.Body = sMessage
		' You can add other files by uncommenting the following line.
		'.Attachments.Add ("C:\test.txt")
		' In place of the following statement, you can use ".Display" to
		' display the mail.
		OutMail.Display
		OutMail.Send   
		On Error GoTo 0
		
	Set OutMail = Nothing
	Set OutApp = Nothing

End Function

Function Step4_JoinDatabase_NoMatch(sPDB As String, sSDB As String, sNewDB As String)

	Set db = Client.OpenDatabase(sPDB)
	Set task = db.JoinDatabase
	task.FileToJoin sSDB
	task.IncludeAllPFields
	task.AddSFieldToInc "DATE_OF_MAKEUPDAY" '"傳票號碼_JE"
	task.AddMatchKey "總帳日期_JE", "DATE_OF_MAKEUPDAY", "A"
	task.CreateVirtualDatabase = False
	dbName = sNewDB
	task.PerformTask dbName, "", WI_JOIN_NOC_SEC_MATCH
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
		
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.JoinDatabase
	task.FileToJoin sNewDB
	task.IncludeAllPFields
	task.AddSFieldToInc "傳票號碼_JE"
	task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
	task.CreateVirtualDatabase = False
	dbName = "#TempS.IDM"
	task.PerformTask dbName, "",  WI_JOIN_MATCH_ONLY
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
			
	Call Z_Rename_DB("#TempS.IDM","#Temp.IDM")
	
End Function

Function Step4_JoinDatabase_NoMatch1(sPDB As String, sSDB As String, sNewDB As String)

	Set db = Client.OpenDatabase(sPDB)
	Set task = db.JoinDatabase
	task.FileToJoin sSDB
	task.IncludeAllPFields
	task.AddSFieldToInc "DATE_OF_MAKEUPDAY" '"傳票號碼_JE"
	task.AddMatchKey "傳票核准日_JE", "DATE_OF_MAKEUPDAY", "A"
	task.CreateVirtualDatabase = False
	dbName = sNewDB
	task.PerformTask dbName, "", WI_JOIN_NOC_SEC_MATCH
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
		
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.JoinDatabase
	task.FileToJoin sNewDB
	task.IncludeAllPFields
	task.AddSFieldToInc "傳票號碼_JE"
	task.AddMatchKey "傳票號碼_JE", "傳票號碼_JE", "A"
	task.CreateVirtualDatabase = False
	dbName = "#TempS.IDM"
	task.PerformTask dbName, "",  WI_JOIN_MATCH_ONLY
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
			
	Call Z_Rename_DB("#TempS.IDM","#Temp.IDM")
	
End Function



Function UploadExcelFile_Dlg(ControlID$, Action%, SuppValue%)
	Dim Button As Integer
	Dim bExitFunction As Boolean
	
		
	Select Case Action%
       		Case 1 
       			'Call Introduction_Button_DisableALL
			If Z_File_Exist("#AccountMapping.IDM") Then dlgText "BtnAccountMapping", "【已上傳】特定科目配對檔案"
			If Z_File_Exist("#Weekend.IDM") Then dlgText "BtnWeekend", "【已設定】非工作日之週末"
			If Z_File_Exist("#Holiday#-Holiday.IDM") Then dlgText "BtnHoliday", "【已上傳】國定假日檔案"
			If Z_File_Exist("#MakeUpDay#-Make-Up_Day.IDM") Then dlgText "BtnMakeUpDay", "【已上傳】補班日/結帳日檔案"

	        	Case 2 
	        	
              		'UploadExcelFile_Dlg = 1

                	Select Case ControlID$
                	
                		Case "BtnAccountMapping"
                		
                			DlgEnable "BtnAccountMapping" ,  0
					DlgEnable "BtnWeekend",  0
                			DlgEnable "BtnMakeUpDay" ,  0
					DlgEnable "BtnHoliday",  0
					DlgEnable "BtnExit",  0

	                	'	Call StepN_Excel_File_Check("STEP2") '檢查使用的Excel來源檔案是否存在 & 是否有被開啟 
			'	If S1CHECK <> "1" Then
					 
	        			dlgText "BtnAccountMapping", "檔案上傳中，請稍後..."
	        			        	
					Call X_Update_Step_Info("STEP_2",1)
					Call X_Update_Step_Info("STEP_3",0)
					
					Call Z_Delete_File("#AccountMapping#-AccountMapping.IDM") 
					Call Z_Delete_File("#AccountMapping.IDM")
					Call Z_Delete_File("#AccountMapping_Sum.IDM")
					Call Z_Delete_File("#AccountMapping_R.IDM")
					Call Z_Delete_File("#AccountMapping_C.IDM")	
					
	       				Call Step2_Upload_AccountMapping_File
	       				
	       						
	       				If sFilename = "OK" Then 
	       					
	       					Call Z_DirectExtractionTable("#AccountMapping#-AccountMapping.IDM", "#AccountMapping.IDM", "STANDARDIZED_ACCOUNT_NAME <> " & Chr(34) & Chr(34) )
						Call Step2_Check_Required
						If amountTotal = 0 Then
							Call Z_Delete_File("#AccountMapping#-AccountMapping.IDM") 
							Call Z_Delete_File("#AccountMapping.IDM")
							Call Z_Delete_File("#AccountMapping_Sum.IDM")
							Call Z_Delete_File("#AccountMapping_R.IDM")
							Call Z_Delete_File("#AccountMapping_C.IDM")	
							sMsg = "未選擇必要配對科目設定，請重新檢查後再次上傳!"
							Result = MsgBox(sMsg ,MB_OK Or MB_ICONEXCLAMATION , "步驟二 科目配對檔設定錯誤 警示訊息")
						Else
							Call X_Update_Step_Info("STEP_2",0)
							Call X_Update_Step_Info("STEP_3",1)
							Call X_Update_Step_User_Info("STEP_2", UserFullName, "STEP_2_Time", DateValue(Now) & " " & TimeValue(Now))      	
						End If 
					End If 
			'	End If
				
				If Z_File_Exist("#AccountMapping.IDM") Then
					dlgText "BtnAccountMapping", "【已上傳】特定科目配對檔案"
				Else 
					dlgText "BtnAccountMapping", "特定科目配對檔案尚未上傳"
				End If

                			DlgEnable "BtnAccountMapping" ,  1
					DlgEnable "BtnWeekend",  1
                			DlgEnable "BtnMakeUpDay" , 1
					DlgEnable "BtnHoliday",  1
					DlgEnable "BtnExit",  1
	
                		Case "BtnWeekend"

                			DlgEnable "BtnAccountMapping" ,  0
					DlgEnable "BtnWeekend",  0
                			DlgEnable "BtnMakeUpDay" ,  0
					DlgEnable "BtnHoliday",  0
					DlgEnable "BtnExit",  0
                		                		
				dlgText "BtnWeekend", "設定中，請稍後..."
				Button = Dialog (Dlgw)
				'*Derek - 待處理
				'dlgText "BtnWeekend", sTemp
						
				If Z_File_Exist("#Weekend.IDM") Then
					dlgText "BtnWeekend", "【已設定】非工作日之週末"
				Else 
					dlgText "BtnWeekend", "非工作日之週末尚未設定"
				End If

                			DlgEnable "BtnAccountMapping" ,  1
					DlgEnable "BtnWeekend",  1
                			DlgEnable "BtnMakeUpDay" , 1
					DlgEnable "BtnHoliday",  1
					DlgEnable "BtnExit",  1
								
			Case "BtnHoliday" 

                			DlgEnable "BtnAccountMapping" ,  0
					DlgEnable "BtnWeekend",  0
                			DlgEnable "BtnMakeUpDay" ,  0
					DlgEnable "BtnHoliday",  0
					DlgEnable "BtnExit",  0
			
       			        	dlgText "BtnHoliday", "檔案上傳中，請稍後..."
				Call Step2_Upload_Holiday_File
				If Z_File_Exist("#Holiday#-Holiday.IDM") Then
					dlgText "BtnHoliday", "【已上傳】國定假日檔案"
				Else 
					dlgText "BtnHoliday", "國定假日檔案尚未上傳"
				End If

                			DlgEnable "BtnAccountMapping" ,  1
					DlgEnable "BtnWeekend",  1
                			DlgEnable "BtnMakeUpDay" , 1
					DlgEnable "BtnHoliday",  1
					DlgEnable "BtnExit",  1
				       			        	       			        	       
              			Case "BtnMakeUpDay"

                			DlgEnable "BtnAccountMapping" ,  0
					DlgEnable "BtnWeekend",  0
                			DlgEnable "BtnMakeUpDay" ,  0
					DlgEnable "BtnHoliday",  0
					DlgEnable "BtnExit",  0
              			              			

	                	'	Call StepN_Excel_File_Check("STEP2") '檢查使用的Excel來源檔案是否存在 & 是否有被開啟 

				dlgText "BtnMakeUpDay", "檔案上傳中，請稍後..."
				Call Step2_Upload_MakeUpDay_File
				If Z_File_Exist("#MakeUpDay#-Make-Up_Day.IDM") Then
					dlgText "BtnMakeUpDay", "【已上傳】補班日/結帳日檔案"
				Else 
					dlgText "BtnMakeUpDay", "補班日/結帳日檔案尚未上傳"
				End If

                			DlgEnable "BtnAccountMapping" ,  1
					DlgEnable "BtnWeekend",  1
                			DlgEnable "BtnMakeUpDay" , 1
					DlgEnable "BtnHoliday",  1
					DlgEnable "BtnExit",  1
										
			Case "BtnOK"
				Call StepN_Excel_File_Check("STEP3") '檢查使用的Excel來源檔案是否存在 & 是否有被開啟 
				Call AddAccPairing_DC
					
				If S1CHECK <> "1" Then
					Call Introduction_Button_DisableALL
					S3Run :
					If GetTotal("#GL#DESC.IDM" ,"" ,"DBCount" ) = 0 Then
						sMsg = "匯入IDEA的檔案資料區間與案件設定的財務報導期間不一致，請重新確認!!!"
						Result = MsgBox(sMsg, MB_OK Or MB_ICONEXCLAMATION ,"步驟三 測試資料錯誤 警示訊息")
					Else
 						Run_S_Time = DateValue(Now) & " " & TimeValue(Now)
						Call Step3_Routines
					End If
					If s1Check = 1 Then GoTo S3Run
					Call X_Get_Project_Info
					Call Introduction_Button_Control
					MsgBox "恭喜，步驟三作業順利完成，記得確認是否需要編製人員清單。"
					bExitFunction = true
				End If				
	                    'bExitFunction = true
	                                	
			End Select
                            
	End Select 

        If  bExitFunction Then
		 UploadExcelFile_Dlg= 0
	Else 
		 UploadExcelFile_Dlg= 1
	End If        
        
End Function


Function DealSheetAmount_Dlg(ControlID$, Action%, SuppValue%)

Select Case Action%
	Case 1
		Call TBFillData("DropListBox_SA1")
		Call TBFillData("DropListBox_SA2")
		Call TBFillData("DropListBox_SA3")
		Call TBFillData("DropListBox_SA4")
		Call TBFillData("DropListBox_SA5")	
		
		DlgEnable "DropListBox_SA1", 0	
		DlgEnable "DropListBox_SA2", 0
		DlgEnable "DropListBox_SA3", 0
		DlgEnable "DropListBox_SA4", 0
		DlgEnable "DropListBox_SA5", 0
		
		Let status_SA=0
		

	Case 2
		If SuppValue%>0 Then 
			Select Case ControlID$
			
			Case "PushButton_SA1" '設定變動金額
				sTBLogMemo = "NA"

				Let status_SA=1
				DlgEnable "DropListBox_SA1", 1	
				DlgEnable "DropListBox_SA2", 0
				DlgEnable "DropListBox_SA3", 0
				DlgEnable "DropListBox_SA4", 0
				DlgEnable "DropListBox_SA5", 0
				
				Call TBFillData("DropListBox_SA1")
				Call TBFillData("DropListBox_SA2")
				Call TBFillData("DropListBox_SA3")
				Call TBFillData("DropListBox_SA4")
				Call TBFillData("DropListBox_SA5")
				
				Let  TB_DifAmount_name =""
				Let  TB_First_Amount_Field =""
				Let  TB_Last_Amount_Field =""
				Let  TB_D_amount =""
				Let  TB_C_Amount =""
				Let   TB_First_D_Amount =""
				Let  TB_Last_D_Amount  =""
				Let  TB_First_C_Amount  =""
				Let  TB_Last_C_Amount =""
								
			Case "PushButton_SA2" '設定期初與期末金額
				sTBLogMemo = "NA"

				Let status_SA=2
				DlgEnable "DropListBox_SA1", 0	
				DlgEnable "DropListBox_SA2", 1
				DlgEnable "DropListBox_SA3", 1
				DlgEnable "DropListBox_SA4", 0
				DlgEnable "DropListBox_SA5", 0
				
				Call TBFillData("DropListBox_SA1")
				Call TBFillData("DropListBox_SA2")
				Call TBFillData("DropListBox_SA3")
				Call TBFillData("DropListBox_SA4")
				Call TBFillData("DropListBox_SA5")

				Dlgtext "t2", "期初金額："
				Dlgtext "t3", "期末金額："			
				Dlgtext "t4", "借方金額："
				Dlgtext "t5", "貸方金額："
				
				Let  TB_DifAmount_name =""
				Let  TB_First_Amount_Field =""
				Let  TB_Last_Amount_Field =""
				Let  TB_D_amount =""
				Let  TB_C_Amount =""
				Let   TB_First_D_Amount =""
				Let  TB_Last_D_Amount  =""
				Let  TB_First_C_Amount  =""
				Let  TB_Last_C_Amount =""

				
			Case "PushButton_SA3"  '設定借方金額與貸方金額
				sTBLogMemo = "NA"

	
				Let status_SA=3
				DlgEnable "DropListBox_SA1", 0	
				DlgEnable "DropListBox_SA2", 0
				DlgEnable "DropListBox_SA3", 0
				DlgEnable "DropListBox_SA4", 1
				DlgEnable "DropListBox_SA5", 1
					
				Call TBFillData("DropListBox_SA1")
				Call TBFillData("DropListBox_SA2")
				Call TBFillData("DropListBox_SA3")
				Call TBFillData("DropListBox_SA4")
				Call TBFillData("DropListBox_SA5")

				Dlgtext "t2", "期初金額："
				Dlgtext "t3", "期末金額："			
				Dlgtext "t4", "借方金額："
				Dlgtext "t5", "貸方金額："

				Let  TB_DifAmount_name =""
				Let  TB_First_Amount_Field =""
				Let  TB_Last_Amount_Field =""
				Let  TB_D_amount =""
				Let  TB_C_Amount =""
				Let   TB_First_D_Amount =""
				Let  TB_Last_D_Amount  =""
				Let  TB_First_C_Amount  =""
				Let  TB_Last_C_Amount =""

								
			Case "PushButton_SA4"  '同時設定借方、貸方以及期初、期末金額
				sTBLogMemo = "NA"

		
				Let status_SA=4
				DlgEnable "DropListBox_SA1", 0	
				DlgEnable "DropListBox_SA2", 1
				DlgEnable "DropListBox_SA3", 1
				DlgEnable "DropListBox_SA4", 1
				DlgEnable "DropListBox_SA5", 1

				Call TBFillData("DropListBox_SA1")
				Call TBFillData("DropListBox_SA2")
				Call TBFillData("DropListBox_SA3")
				Call TBFillData("DropListBox_SA4")
				Call TBFillData("DropListBox_SA5")
				
				Dlgtext "t2", "期初借方金額："
				Dlgtext "t3", "期初貸方金額："
				Dlgtext "t4", "期末借方金額："
				Dlgtext "t5", "期末貸方金額："
				
				Let  TB_DifAmount_name =""
				Let  TB_First_Amount_Field =""
				Let  TB_Last_Amount_Field =""
				Let  TB_D_amount =""
				Let  TB_C_Amount =""
				Let   TB_First_D_Amount =""
				Let  TB_Last_D_Amount  =""
				Let  TB_First_C_Amount  =""
				Let  TB_Last_C_Amount =""
							
			Case "DropListBox_SA1" '期間變動金額欄位
				Let TB_DifAmount_name  = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
				
			Case "DropListBox_SA2" '期初金額  or 期初借方金額
				If status_SA=4 Then 
					Let TB_First_D_Amount = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
					Let TB_First_Amount_Field  =""
				Else
					Let TB_First_Amount_Field  = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
					Let TB_First_D_Amount =""
				End If 
				
			Case "DropListBox_SA3" '期末金額  or 期初貸方金額

				If status_SA=4 Then 
					Let TB_First_C_Amount = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
					Let TB_Last_Amount_Field  =""
				Else
					Let TB_Last_Amount_Field  = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
					Let TB_First_C_Amount =""
				End If 
		
			Case "DropListBox_SA4" '借方金額 or 期末借方金額

				If status_SA=4 Then 
					Let TB_Last_D_Amount = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
					Let TB_D_Amount =""
				Else
					Let TB_D_Amount = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
					Let TB_Last_D_Amount=""
				End If 
						
			Case "DropListBox_SA5" '貸方金額 or 期末貸方金額
				
				If status_SA=4 Then 
					Let TB_Last_C_Amount = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
					Let TB_C_Amount =""
				Else
					Let TB_C_Amount = TBFieldArray_num(SuppValue%) 'TB_Field(SuppValue%)
					Let TB_Last_C_Amount=""
				End If 
							
			Case "OKButton_SA1"
				' MsgBox  "status_SA："&status_SA
				 'MsgBox "TB_DifAmount_name："& TB_DifAmount_name
				 'MsgBox "TB_First_Amount_Field ："&TB_First_Amount_Field 
				 'MsgBox "TB_Last_Amount_Field  ："&TB_Last_Amount_Field  
				If status_SA=1 Then 
					If TB_DifAmount_name="" Then 
						MsgBox "請選擇期間變動金額"
						
						'若使用者輸入錯誤，則要求重設
						DlgEnable "DropListBox_SA1", 0	
						DlgEnable "DropListBox_SA2", 0
						DlgEnable "DropListBox_SA3", 0
						DlgEnable "DropListBox_SA4", 0
						DlgEnable "DropListBox_SA5", 0
						
					Else  '已正確設定
						sTBLogMemo = "OK"
						Exit Function 
					End If 
					
				ElseIf status_SA=2 Then 
					If TB_First_Amount_Field ="" Or TB_Last_Amount_Field  ="" Then 
						MsgBox "請設定期初與期末金額"
						
						'若使用者輸入錯誤，則要求重設
						DlgEnable "DropListBox_SA1", 0	
						DlgEnable "DropListBox_SA2", 0
						DlgEnable "DropListBox_SA3", 0
						DlgEnable "DropListBox_SA4", 0
						DlgEnable "DropListBox_SA5", 0

						
					Else  '已正確設定
						sTBLogMemo = "OK"
						Exit Function 	
					End If 
					
				ElseIf status_SA=3 Then 
					If TB_D_Amount ="" Or TB_C_Amount ="" Then 
						MsgBox "請設定借方與貸方金額"
						
						'若使用者輸入錯誤，則要求重設
						DlgEnable "DropListBox_SA1", 0	
						DlgEnable "DropListBox_SA2", 0
						DlgEnable "DropListBox_SA3", 0
						DlgEnable "DropListBox_SA4", 0
						DlgEnable "DropListBox_SA5", 0

						
					Else  '已正確設定
						sTBLogMemo = "OK"
						Exit Function 	
					End If 

				ElseIf status_SA=4 Then 
				
					If TB_First_D_Amount="" Or TB_First_C_Amount=""  Or  TB_Last_D_Amount="" Or TB_Last_C_Amount="" Then 
						MsgBox "請設定期初、期末與借方、貸方金額"
						'若使用者輸入錯誤，則要求重設
						DlgEnable "DropListBox_SA1", 0	
						DlgEnable "DropListBox_SA2", 0
						DlgEnable "DropListBox_SA3", 0
						DlgEnable "DropListBox_SA4", 0
						DlgEnable "DropListBox_SA5", 0

					Else  '已正確設定
						sTBLogMemo = "OK"
						Exit Function 	
					End If 
				ElseIf status_SA=0 Then
					Exit Function 				
				End If
				
			Case "BtnCancel" 
				DealSheetAmount_Dlg = 0
				Exit Function 				
				
			End Select	
	DealSheetAmount_Dlg = 1	'若按下的是一般 PushBotton, 持續顯示對話框	
		End If
	End Select	
End Function

Function FillData(ListBoxName As String)
	DlgListBoxArray ListBoxName, FieldArray_num()
End Function

Function FillData_mix(ListBoxName As String)
	DlgListBoxArray ListBoxName, FieldArray_mix()
End Function

'Function FillData_Char(ListBoxName As String)
'	DlgListBoxArray ListBoxName, FieldArray_Char()
'End Function

Function TBFillData(ListBoxName As String)
	DlgListBoxArray ListBoxName, TBFieldArray_num()
End Function

Function TBFillData_mix(ListBoxName As String)
	DlgListBoxArray ListBoxName, TBFieldArray_mix()
End Function

Function TBFillData_Char(ListBoxName As String)
	DlgListBoxArray ListBoxName, TBFieldArray_Char()
End Function

Function GLFillData(ListBoxName As String)
	DlgListBoxArray ListBoxName, GLFieldArray_num()
End Function

Function GLFillData_mix(ListBoxName As String)
	DlgListBoxArray ListBoxName, GLFieldArray_mix()
End Function

Function GLFillData_Char(ListBoxName As String)
	DlgListBoxArray ListBoxName, GLFieldArray_Char()
End Function

Function DealAmount_Dlg(ControlID$, Action%, SuppValue%)

	Select Case Action%
		Case 1
		
			DlgEnable "DropListBox_A1", 0
			DlgEnable "DropListBox_A5", 0
			DlgEnable "TextBox_A1", 0
			DlgEnable "DropListBox_A2", 0
			DlgEnable "DropListBox_A3", 0
			DlgEnable "DropListBox_A4", 0
			
		Case 2
			If SuppValue%>0 Then 
				Select Case ControlID$
						
					Case "PushButton_A1"  '表不需特別處理金額
						Let status_Amount  =0  '用於判斷金額處理方法	0表尚未設定；1表不須分辨借貸；2表需要分辨借方金額與貸方金額；3表示依據借貸別區分
						sGLLogMemo = "NA"
	
						Let status_Amount  = 1
						Let JE_DC_name = ""
						Let  JE_D_Amount_Field =""
						Let  JE_C_Amount_Field =""
						
						Let JE_D_Amount =""
						Let JE_C_Amount =""
						
	'					Call FillData (thisTable1, "DropListBox_A1", JE_Field())
						
						Call GLFillData("DropListBox_A1")
						Call GLFillData("DropListBox_A2")
						Call GLFillData("DropListBox_A3")
						Call GLFillData("DropListBox_A4")
						Call GLFillData_mix("DropListBox_A5")
						
						DlgEnable "DropListBox_A2", 0
						DlgEnable "DropListBox_A3", 0
						DlgEnable "DropListBox_A4", 0
						DlgEnable "DropListBox_A5", 0
						DlgEnable "TextBox_A1", 0
						DlgEnable "DropListBox_A1", 1
						
					Case "PushButton_A2" '設定借方金額與貸方金額
						Let status_Amount  = 0  '用於判斷金額處理方法	0表尚未設定；1表不須分辨借貸；2表需要分辨借方金額與貸方金額；3表示依據借貸別區分
						sGLLogMemo = "NA"
	
						DlgEnable "DropListBox_A4", 0
						DlgEnable "DropListBox_A1", 0
						DlgEnable "DropListBox_A2", 1
						DlgEnable "DropListBox_A3", 1
						DlgEnable "DropListBox_A5", 0
						DlgEnable "TextBox_A1", 0
						Let status_Amount  =2
						
						Call GLFillData("DropListBox_A1")
						Call GLFillData("DropListBox_A2")
						Call GLFillData("DropListBox_A3")
						Call GLFillData("DropListBox_A4")
						Call GLFillData_mix("DropListBox_A5")
						
						Let  JE_D_Amount_Field =""
						Let  JE_C_Amount_Field =""
						Let JE_DC_name = ""
						Let JE_Amount_name =""
															
					Case "PushButton_A3" '表需要依借貸別處理金額
						Let status_Amount  = 0  '用於判斷金額處理方法	0表尚未設定；1表不須分辨借貸；2表需要分辨借方金額與貸方金額；3表示依據借貸別區分
						sGLLogMemo = "NA"
	
						DlgEnable "DropListBox_A4", 1
						DlgEnable "DropListBox_A5", 1
						DlgEnable "DropListBox_A1", 0
						DlgEnable "TextBox_A1", 1
						DlgEnable "DropListBox_A2", 0
						DlgEnable "DropListBox_A3", 0
						Let status_Amount  =3
						
						Call GLFillData("DropListBox_A1")
						Call GLFillData("DropListBox_A2")
						Call GLFillData("DropListBox_A3")
						Call GLFillData("DropListBox_A4")
						Call GLFillData_mix("DropListBox_A5")
						
						Let JE_D_Amount =""
						Let JE_C_Amount =""
						
	
					Case "DropListBox_A1"  '抓取傳票金額
						Let JE_Amount_name =GLFieldArray_num(SuppValue%)
						'MsgBox "JE_Amount_name為:"&JE_Amount_name  '除錯用
						
					Case "DropListBox_A2"  '抓取借方金額
						Let JE_D_Amount =GLFieldArray_num(SuppValue%)
						'MsgBox "JE_Amount_name為:"&JE_Amount_name  '除錯用
						
					Case "DropListBox_A3"  '抓取借方金額
						Let JE_C_Amount =GLFieldArray_num(SuppValue%)
						'MsgBox "JE_Amount_name為:"&JE_Amount_name  '除錯用
						
					Case "DropListBox_A4" ''抓取傳票金額(仍須判斷借貸欄位)
						Let JE_Amount_name =GLFieldArray_num(SuppValue%)
						Client.OpenDatabase sGLFilename				
						Client.FindAndHighlightFieldInDatabase sGLFilename, 5,  JE_Amount_name
						'MsgBox "JE_Amount_name為:"&JE_Amount_name  '除錯用					
											
					Case "DropListBox_A5"  '借貸別
						Let JE_DC_name=GLFieldArray_mix(SuppValue%)
						Client.OpenDatabase sGLFilename				
						Client.FindAndHighlightFieldInDatabase sGLFilename, 5,  JE_DC_name
	
					Case "OKButton_A1"
						
						'僅要輸入傳票金額即可
						If status_Amount  = 1 Then 
							If JE_Amount_name ="" Then
								 MsgBox "請輸入傳票金額"
								 DealAmount_Dlg = 1
							Else 
								 sGLLogMemo = "OK"
								 DealAmount_Dlg = 0 
							End If
						End If
						
						'需要同時輸入借方金額與貸方金額
						
						If  status_Amount  = 2 Then 
							If JE_D_Amount =""  Or  JE_C_Amount =""  Then
								 MsgBox "請輸入借方金額與貸方金額"
								  DealAmount_Dlg = 1
							Else 
								 sGLLogMemo = "OK"
								 DealAmount_Dlg = 0
	
							End If				
						End If
	
						'表須要考量借貸別
						If status_Amount  =3  Then   				 
							If JE_Amount_name <> "" And JE_DC_name <> "" And iAllTrim(DlgA.TextBox_A1) <> "" Then  '表資料皆正確被輸入
								Let status_Amount  = 3
								Let JE_D_Amount_Field = iAllTrim(DlgA.TextBox_A1)
								sGLLogMemo = "OK"
								DealAmount_Dlg = 0	'若輸入正確才關閉視窗
							Else
								DealAmount_Dlg = 1	
							End If
						End If
						
						 If  DealAmount_Dlg = 0 Then Exit Function 
						 
					Case "BtnCancel" 
						 DealAmount_Dlg = 0
						Exit Function 				
						 					 
				End Select	
	
				 DealAmount_Dlg = 1	'若按下的是一般 PushBotton, 持續顯示對話框	
			End If	
	End Select	
				
End Function

Function Step1_GL_Amount_Append_1

	Call Z_Field_Info("#GL#.IDM", JE_Amount_name )

	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "傳票金額_JE"
	field.Description = "傳票金額_JE 由系統產生 : " & JE_Amount_name 
	field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
	field.Equation =  JE_Amount_name 
	field.Decimals = sDecimals
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing


End Function

Function Step1_GL_Amount_Append_2

	Call Z_Field_Info("#GL#.IDM", JE_C_Amount)

	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "傳票金額_JE"
	field.Description = "傳票金額_JE 由系統產生 : " & JE_D_Amount & "-" & JE_C_Amount
	field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
	field.Equation =  JE_D_Amount & " - " &  JE_C_Amount
	field.Decimals = sDecimals
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing

End Function

Function Step1_GL_Amount_Append_3

	Call Z_Field_Info("#GL#.IDM", JE_DC_name)
	
	If sType = "WI_VIRT_CHAR" Then 

		Call Z_Field_Info("#GL#.IDM", JE_Amount_name)
	'MsgBox "@if(" & JE_DC_name & " = " & Chr(34) & JE_D_Amount_Field & Chr(34) & ", " & JE_Amount_name  & " , " & JE_Amount_name & " * -1) "
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "傳票金額_JE"
		field.Description = "傳票金額_JE 由系統產生 : 金額欄位【" & JE_Amount_name & "】、借貸方判斷欄位【" & JE_DC_name & "】，借方為【" & JE_D_Amount_Field & "】"
		field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
		field.Equation =  "@if(" & JE_DC_name & " = " & Chr(34) & JE_D_Amount_Field & Chr(34) & ", " & JE_Amount_name  & " , " & JE_Amount_name & " * -1) "
		field.Decimals = sDecimals
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
	
	ElseIf sType = "WI_VIRT_NUM" Then 
	
		Call Z_Field_Info("#GL#.IDM", JE_Amount_name)
	
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "傳票金額_JE"
		field.Description = "傳票金額_JE 由系統產生 : 金額欄位【" & JE_Amount_name & "】、借貸方判斷欄位【" & JE_DC_name & "】，借方為【" & JE_D_Amount_Field & "】"
		field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
		field.Equation =  "@if(" & JE_DC_name & " = " &  JE_D_Amount_Field  & " , " & JE_Amount_name  & " , " & JE_Amount_name & " * -1) "
		field.Decimals = sDecimals
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
	
	End If 

End Function

Function Step1_GL_Vaild_Field

JE_Vaild_Field = GLFieldArray_Char(VaildDialog.DropListBox_V1)
JE_Vaild = dlgB.TextBox_V1  

Call Z_Field_Info("#GL#.IDM", JE_Vaild_Field)

If sType = "WI_VIRT_CHAR" Then 

		
		Set db = Client.OpenDatabase("#GL#.IDM")
		Set task = db.TableManagement
		Set field = db.TableDef.NewField
		field.Name = "過帳碼_JE"
		field.Description =  ""& JE_Vaild_Field &" ：過帳判斷值【" & JE_Vaild &"】"
		field.Equation = JE_Vaild_Field 
		field.Type = WI_VIRT_CHAR
		task.AppendField field
		task.PerformTask
		Set task = Nothing
		Set db = Nothing
		Set field = Nothing
End If

End Function


Function Step1_TB_Amount_Append_2

	Call Z_Field_Info("#TB#.IDM", TB_First_Amount_Field)

	Set db = Client.OpenDatabase("#TB#.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "試算表變動金額_TB"
	field.Description = "試算表變動金額_TB 由系統產生 : " & TB_Last_Amount_Field & " - " & TB_First_Amount_Field
	field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
	field.Equation =  TB_Last_Amount_Field & " - " &  TB_First_Amount_Field
	field.Decimals = sDecimals
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing

End Function


Function Step1_TB_Amount_Append_3

	Call Z_Field_Info("#TB#.IDM", TB_D_Amount)

	Set db = Client.OpenDatabase("#TB#.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "試算表變動金額_TB"
	field.Description = "試算表變動金額_TB 由系統產生 : " &  TB_D_Amount & " - " & TB_C_Amount
	field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
	field.Equation =  TB_D_Amount & " - " &  TB_C_Amount
	field.Decimals = sDecimals
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing

End Function

Function Step1_TB_Amount_Append_4

	Call Z_Field_Info("#TB#.IDM", TB_Last_D_Amount)

	Set db = Client.OpenDatabase("#TB#.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "試算表變動金額_TB"
	field.Description = "試算表變動金額_TB 由系統產生 : (" &  TB_Last_D_Amount  & " - " & TB_Last_C_Amount & ") - (" & TB_First_D_Amount & "-" & TB_First_C_Amount &")"
	field.Type = WI_NUM_FIELD 'WI_VIRT_NUM
	field.Equation =  "(" & TB_Last_D_Amount & "-" & TB_Last_C_Amount & ") - (" & TB_First_D_Amount & "-" & TB_First_C_Amount &")"
	field.Decimals = sDecimals
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing

End Function


Function VaildDialog_Dlg(ControlID$, Action%, SuppValue%)

Select Case Action%
	Case 1
		DlgEnable "TextBox_V1", 0	
		DlgEnable "DropListBox_V1", 0
		Let status_Vaild=0
	Case 2
		If SuppValue%>0 Then 
			Select Case ControlID$
			
			Case "PushButton_V1" 'YES
				Let status_Vaild=2
				Call GLFillData_Char("DropListBox_V1")
				Let JE_Vaild = "Null"
				Let JE_Vaild_Field  = ""
				DlgEnable "TextBox_V1", 1	
				DlgEnable "DropListBox_V1", 1

			Case "PushButton_V2" 'NO then exit function
				Let status_Vaild=1
				'無須設定，則預定為Joe
				Let JE_Vaild ="Null"   
				Let  JE_Vaild_Field  ="Null" 
				sLog = "NA"
				'MsgBox "無須設定篩選條件，視窗即將關閉"
				 VaildDialog_Dlg = 0	'輸入正確資料則結束顯示對話框
				Exit Function
				
			Case "DropListBox_V1"
				'預設使用者選第一個，因為第一個listbox 天生殘缺有bug 無法讀到...  所以矩陣第0個為  請使用者輸入資料
				'Let JE_Vaild_Field =  JE_Field(0)
				'MsgBox JE_Vaild_Field  
				Let JE_Vaild_Field  =GLFieldArray_Char(SuppValue%)'有效性篩選之欄位
				Client.OpenDatabase sGLFilename			
				Client.FindAndHighlightFieldInDatabase sGLFilename, 5,  JE_Vaild_Field
				'MsgBox JE_Field(dlgV.DropListBox_V1)

			Case "OKButton_V1"
				Let JE_Vaild = dlgB.TextBox_V1  '有效性篩選用之關鍵字
				'MsgBox "選擇欄位："&JE_Vaild_Field
				'MsgBox "關鍵字:"&JE_Vaild
				
				If  status_Vaild>=2 Then
					If JE_Vaild =""  Or  JE_Vaild_Field  ="" Then 
					Let status_Vaild=3
					 MsgBox "請選擇欄位與篩選關鍵字" 	
					 End If 
				End If
				 
				If JE_Vaild <>"" And JE_Vaild_Field  <>"" Then 
					 VaildDialog_Dlg  = 0	
					Let status_Vaild=2 '表資料皆有正確'輸入
					sLog = "OK"
					'MsgBox JE_Vaild_Field  
					Exit Function 
				End If
				Exit Function 				
			End Select	
		 VaildDialog_Dlg = 1	'若按下的是一般 PushBotton, 持續顯示對話框	
		End If
	End Select 	
End Function

Function Step1_Approval_Date_Append
	Set db = Client.OpenDatabase("#GL#.IDM")
	Set task = db.TableManagement
	Set field = db.TableDef.NewField
	field.Name = "傳票核准日_JE"
	field.Description = "系統產生 : 同總帳日期_JE來源"
	field.Type = WI_DATE_FIELD
	field.Equation = " 總帳日期_JE "
	task.AppendField field
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Set field = Nothing
End Function

Function Z_Sum_Doc(sPDB As String, sField As String, sNewDB As String)
	Set db = Client.OpenDatabase(sPDB)
	Set task = db.Summarization
	task.AddFieldToSummarize sField
	dbName = sNewDB
	task.OutputDBName = dbName
	task.CreatePercentField = FALSE
	task.PerformTask
	Set task = Nothing
	Set db = Nothing
	Client.OpenDatabase (dbName)
	
	Set db = Client.OpenDatabase(sNewDB)
	Set stats = db.FieldStats(sField)
	
		Z_Sum_Doc = db.count	
		
	Set db = Nothing
	Set stats = Nothing

End Function

'計算字串中有幾個字串
Function X_CountItem(Inputstring As String) As Variant

Dim TempString As String
TempString = ilen(Inputstring) - ilen(iremove(Inputstring,","))+1
X_CountItem = TempString

End Function


Function X_GetStringArray( inputstring As String,Arrayname As String) 

On Error GoTo EM
Dim MaxValue As Integer
MaxValue = X_CountItem(inputstring)

Dim i As Integer
Dim j As Integer
Dim bFirstPass As Boolean
Dim StringArray() As Variant

ReDim StringArray(MaxValue+1)
StringArray(0) =""
For i = 1 To MaxValue	
	StringArray(i) = isplit(Inputstring,"",",", i )
Next i	
	
Select Case Arrayname
	Case "MatchArray"
		ReDim MatchArray(UBound(StringArray))
		For j = LBound(StringArray) To UBound(StringArray)
			MatchArray(j) =StringArray(j)
		Next j
	Case "EqualArray"
		ReDim EqualArray(UBound(StringArray))
		For j = LBound(StringArray) To UBound(StringArray)
			EqualArray(j) = StringArray(j)
		Next j
	Case Else
		MsgBox "Invalid array name specified."
End Select

	Erase StringArray
	Exit Function
	
EM:
    Erase StringArray
    MsgBox "X_GetStringArray_" & Err.Description

End Function


Function X_GetCriteriatoSel_Match( inputstring As String, fieldArray As String)As String

On Error GoTo EM
Dim MaxValueMatch As Integer
MaxValueMatch = X_CountItem(inputstring)

Call X_GetStringArray(inputstring,"MatchArray")

Dim Matchwords As String
Matchwords =""

Dim i As Integer

If inputstring <>"" Then
	For i = 1 To MaxValueMatch 
		Matchwords = Matchwords & ",@Upper(""" & MatchArray(i)  & """" & ")"
	Next 
	Matchwords = "(@Match(@Upper("& fieldArray &")" & Matchwords & ")<>0)"
End If 

X_GetCriteriatoSel_Match = Matchwords 

	Erase MatchArray
	Exit Function

EM:
    Erase MatchArray
    MsgBox "X_GetCriteriatoSel_Match_" & Err.Description

	
End Function

Function X_GetCriteriatoSel_Match_Num( inputstring As String, fieldArray As String)As String

On Error GoTo EM
Dim MaxValueMatch As Integer
MaxValueMatch = X_CountItem(inputstring)

Call X_GetStringArray(inputstring,"MatchArray")

Dim Matchwords As String
Matchwords =""

Dim i As Integer


If inputstring <>"" Then
	For i = 1 To MaxValueMatch 
		Matchwords = Matchwords & ",""" & MatchArray(i)  & """"
	Next 
	Matchwords = "(@Match(@str(@int(" & fieldArray & "),1,0)" & Matchwords & ")<>0)"
End If 


X_GetCriteriatoSel_Match_Num = Matchwords 


	Erase MatchArray
	Exit Function

EM:
    Erase MatchArray
    MsgBox "X_GetCriteriatoSel_Match_Num_" & Err.Description

	
End Function

Function SendMailtoDorian

	Dim OutApp As Object
	Dim OutMail As Object
	Dim sSubject As String
	Dim sMessage As String
	Dim i As Long
	Dim CrLf As String
	Dim C As Integer
	
	CrLf = Chr(10) & Chr(13)
	

	Set OutApp = CreateObject("Outlook.Application")
	Set OutMail = OutApp.CreateItem(0)

	sSubject = "【JE Team】笑話提交_" & sEngagement_Info
	sMessage = "我自願講一個笑話給大家! 請Teams我，我會提供完整笑話。"
			
	On Error Resume Next
	' Change the mail address and subject in the macro before you run it.
	'MsgBox rs.ActiveRecord.GetCharValue("EMAIL_ADDRESS") 
	OutMail.To = "redacted@example.invalid" ' 
	'OutMail.To = "redacted@example.invalid;redacted@example.invalid;redacted@example.invalid" ' 
	OutMail.CC = "redacted@example.invalid"
	OutMail.BCC = ""
	OutMail.Subject = sSubject
	OutMail.Body = sMessage
	' You can add other files by uncommenting the following line.
	'.Attachments.Add ("C:\test.txt")
	' In place of the following statement, you can use ".Display" to
	OutMail.Send   
	On Error GoTo 0
		
	Set OutMail = Nothing
	Set OutApp = Nothing

End Function

Function AccPairing_Dlg(ControlID$, Action%, SuppValue%)
	Dim bExitMenu As Boolean
	Dim i As Integer
	Dim j As Integer
	Dim k, l As Integer 'used to update tempListbox1 and 2 array
	Dim bListBox2Set As Boolean 'flag to show item was in placed in listbox2

	Select Case action
		Case 1
			If AccPairingMode = 1 Then
	                	    DlgText "Text1", "將欲選擇之借方科目配對，選至右邊的方框中。"
			ElseIf AccPairingMode = 2 Then
		                DlgText "Text1", "將欲選擇之貸方科目配對，選至右邊的方框中。"
			End If

			'initialize the variables
			ReDim tempListbox1(0)
			ReDim tempListbox2(0)

			l = 0
			k = 0
			For i = 1 To UBound(FieldArray_AddAccPairing) + 1
				ReDim Preserve tempListbox1(UBound(tempListbox1) + 1)

				tempListbox1(i) = FieldArray_AddAccPairing(i - 1)
			Next i
			'Call sortArray(tempListbox1)
			Call removeBlanksFromArray(1)
			DlgListboxArray "ListBoxLeft", tempListbox1
			DlgListBoxArray "ListBoxRight", tempListbox2
			tempListSelect1 = 0  'set to 1st items as default
		Case 2
			Select Case ControlId$
				Case "PushButton1"
					If UBound(tempListbox1) > 0 Then

						ReDim Preserve tempListbox2(UBound(tempListbox2) + 1)

						tempListbox2(UBound(tempListbox2)) = tempListbox1(tempListSelect1)
						'MsgBox tempListbox2(UBound(tempListbox2))
						tempListbox1(tempListSelect1 ) = ""

						Call removeBlanksFromArray(1)
						Call removeBlanksFromArray(2)
						'MsgBox tempListbox2(UBound(tempListbox2))
					End If
					DlgListBoxArray "ListBoxRight", tempListbox2()
					'For i = 0 To UBound(tempListbox1)
					'	MsgBox i & ": " &  tempLIstbox1(i)
					'Next i
					DlgListBoxArray "ListBoxLeft", tempListbox1()
					tempListSelect1 = 0 'set to 1st items as default
					bExitMenu = FALSE
				Case "PushButton2"
					If UBound(tempListbox2) > 0 Then

						ReDim Preserve tempListbox1(UBound(tempListbox1) + 1)

						tempListbox1(UBound(tempListbox1)) = tempListbox2(tempListSelect2)
						'MsgBox tempListbox2(UBound(tempListbox2))
						tempListbox2(tempListSelect2 ) = ""

						Call removeBlanksFromArray(1)
						Call removeBlanksFromArray(2)
						'MsgBox tempListbox2(UBound(tempListbox2))
					End If
					DlgListBoxArray "ListBoxRight", tempListbox2
					'For i = 0 To UBound(tempListbox1)
					'	MsgBox i & ": " &  tempLIstbox1(i)
					'Next i
					DlgListBoxArray "ListBoxLeft", tempListbox1
					tempListSelect2 = 0 'set to 1st items as default
					bExitMenu = FALSE
				Case "PushButton3"
					ReDim tempListbox2(UBound(FieldArray_AddAccPairing)+1)
					ReDim  tempListbox1(0)
					For i = 1 To UBound(FieldArray_AddAccPairing) + 1
						tempListbox2(i) = FieldArray_AddAccPairing(i - 1)
					Next i
					Call removeBlanksFromArray(2)

					DlgListBoxArray "ListBoxRight", tempListbox2
					DlgListBoxArray "ListBoxLeft", tempListbox1
					bExitMenu = FALSE
				Case "PushButton4"
					ReDim tempListbox1(UBound(FieldArray_AddAccPairing))
					ReDim tempListbox2(0)
					For i = 0 To UBound(FieldArray_AddAccPairing)
						tempListbox1(i) = FieldArray_AddAccPairing(i)
					Next i
					Call removeBlanksFromArray(1)
					DlgListBoxArray "ListBoxRight", tempListbox2
					DlgListBoxArray "ListBoxLeft", tempListbox1
					bExitMenu = FALSE
				Case "ListBoxLeft"
					tempListSelect1 = SuppValue%

				Case "ListBoxRight"
					tempListSelect2 = SuppValue%

				Case "OKButton1"
					If  tempListbox2(0) = "" Then
						MsgBox "Please select at least one file"
						bExitMenu = FALSE
					Else
						If AccPairingMode = 1 Then
							sSelAcc1 ="OK"
						ElseIf  AccPairingMode = 2 Then
							sSelAcc2 = "OK" 
						End If
						bExitMenu = True
					End If
			End Select
	End Select

	If tempListbox1(0) = "" Then 'firt list box is emtpy
		DlgEnable "PushButton1", 0
		DlgEnable "PushButton3", 0
		DlgEnable "PushButton2", 1
		DlgEnable "PushButton4", 1

	ElseIf tempListbox2(0) ="" Then 'second list box is empty
		DlgEnable "PushButton1", 1
		DlgEnable "PushButton3", 1
		DlgEnable "PushButton2", 0
		DlgEnable "PushButton4", 0

	Else 'something in boxes
		DlgEnable "PushButton1", 1
		DlgEnable "PushButton3", 1
		DlgEnable "PushButton2", 1
		DlgEnable "PushButton4", 1

	End If
	If bExitMenu Then
		AccPairing_Dlg = 0
	Else
		AccPairing_Dlg = 1
	End If

End Function

Private Function removeBlanksFromArray(tempType As Integer) '將項目從一個 ListBox 移到另一個時，原 ListBox 中的項目會被設為 ""。清理空白項目，讓 ListBox 顯示乾淨的內容。
	Dim tempArray() As String
	Dim i, ILoop As Integer
	ReDim tempArray(0)

	If tempType = 1 Then
		For ILoop = 0 To UBound(tempListbox1)
			If tempListbox1(ILoop) <> "" Then
				tempArray(UBound(tempArray)) = tempListbox1(ILoop)
				If ILoop <> UBound(tempListBox1) Then 'don't increment on the last pass
					ReDim preserve tempArray(UBound(tempArray) + 1)
				End If
			End If
		Next ILoop
		'MsgBox UBound(MyArray)
		i = UBound(tempArray)
		Erase tempListbox1

		ReDim tempListbox1(i)
		For ILoop = 0 To UBound(tempArray)
			'MsgBox "i " & ILoop & " - " & tempArray(ILoop)
			tempListbox1(ILoop) = tempArray(ILoop)
		Next ILoop
	Else
		For ILoop = 0 To UBound(tempListbox2)
			If tempListbox2(ILoop) <> "" And  tempListbox2(ILoop) <> "Select..." Then
				tempArray(UBound(tempArray)) = tempListbox2(ILoop)
				If ILoop <> UBound(tempListBox2) Then 'don't increment on the last pass
					ReDim preserve tempArray(UBound(tempArray) + 1)
				End If
			End If
		Next ILoop
		'MsgBox UBound(MyArray)
		i = UBound(tempArray)
		Erase tempListbox2

		ReDim tempListbox2(i)
		For ILoop = 0 To UBound(tempArray)
			'MsgBox "i " & ILoop & " - " & tempArray(ILoop)
			tempListbox2(ILoop) = tempArray(ILoop)
		Next ILoop

	End If

End Function

Function AccPairingtoString_Match( MyArray() As String)As String

On Error GoTo EM
Dim MaxValueMatch As Integer
MaxValueMatch = UBound(MyArray)


Dim Matchwords As String
Matchwords =""

Dim i As Integer


If  MaxValueMatch = 0 Then
	Matchwords = "(@Match(@Upper(STANDARDIZED_ACCOUNT_NAME )," & "@Upper(""" & MyArray(0)  & """"  & "))<>0)"
Else
	For i = 1 To MaxValueMatch 
		Matchwords = Matchwords & ",@Upper(""" & MyArray(i)  & """" & ")"
	Next 
	Matchwords = "(@Match(@Upper(STANDARDIZED_ACCOUNT_NAME )" & Matchwords & ")<>0)"
End If 

AccPairingtoString_Match = Matchwords 

	Exit Function

EM:
    MsgBox "AccPairingtoString_Match_" & Err.Description

	
End Function



Function AccPairingtoString(MyArray() As String) As String
    On Error GoTo EM

    Dim MaxValueMatch As Integer
    Dim Matchwords As String
    Dim i As Integer

    Matchwords = ""

    If UBound(MyArray) >= 0 Then
        Matchwords = MyArray(0)
        For i = 1 To UBound(MyArray)
            Matchwords = Matchwords & "," & MyArray(i)
        Next i
    End If

    AccPairingtoString = Matchwords
    Exit Function
EM:
    MsgBox "AccPairingtoString_" & Err.Description
End Function































































































































































































































































































































































