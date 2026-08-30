VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewImport 
   Caption         =   "Import"
   ClientHeight    =   8430.001
   ClientLeft      =   105
   ClientTop       =   405
   ClientWidth     =   7155
   OleObjectBlob   =   "ViewImport.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewImport"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Form:     ViewImport
' Purpose:
' Methods:
'===============================================================================
Public Event ImportJournalEntries(ByVal Format As String)
Public Event ImportTrialBalance(ByVal Format As String)
Public Event JeFieldMappingRequested()
Public Event TbFieldMappingRequested()
Public Event UpdateDateDimensionRequested(ByVal dto As DataTransferObject)
Public Event TestDefaultRequested() '僅作測試用途
Public Event Submitted(ByVal dto As DataTransferObject)
Public Event BeepBoop()
'--
Private m_Format As String
' [New] State Flags
Private m_IsJeReady As Boolean
Private m_IsTbReady As Boolean
Private m_IsCalendarReady As Boolean

'===============================================================================
'===============================================================================
Private Sub btnTestDefault_Click()
    '---------------------------------------------------------------------------
    ' //WARNING: ONLY FOR TESTING
    '---------------------------------------------------------------------------
    '填上控制項
    Me.txtbCompanyName.Text = "精能"
    Me.txtbPeriodStart.Text = "2024/01/01"
    Me.txtbPeriodEnd.Text = "2024/12/31"
    Me.txtbPrepStartDate = "2024/12/31"
    RaiseEvent TestDefaultRequested
    '填上假期
    Call btnConfigureHolidays_Click
    Dim ws As Worksheet
    Set ws = SHT_HOLIDAYS
    ws.Range("A2").value = DateSerial(2024, 10, 12)
    ws.Range("B2").value = "國慶日"
    ws.Columns("A:B").AutoFit
    '填上補班日
    Call btnConfigureMakeUpDays_Click
    Set ws = SHT_MAKEUPDAYS
    ws.Range("A2").value = DateSerial(2024, 11, 4)
    ws.Range("B2").value = "補班日"
    ws.Columns("A:B").AutoFit
End Sub
'===============================================================================
'===============================================================================

Public Sub Initialize()
    '---------------------------------------------------------------------------
    ' ...
    '---------------------------------------------------------------------------
    '預設匯入格式為 XLSX
    Me.optXlsx.value = True
    Call optXlsx_Click
    '預設非工作日清單
    With Me.lstWeekend
        .AddItem "Sunday"       '1
        .AddItem "Monday"       '2
        .AddItem "Tuesday"      '3
        .AddItem "Wednesday"    '4
        .AddItem "Thursday"     '5
        .AddItem "Friday"       '6
        .AddItem "Saturday"     '7
        '預設選取周日和周六
        .selected(0) = True
        .selected(6) = True
    End With
    ' [New] Reset State & UI
    m_IsJeReady = False
    m_IsTbReady = False
    m_IsCalendarReady = False
    Call UpdateUiState
End Sub

' [New] Public Methods for Controller to push state
Public Sub SetJeState(ByVal isReady As Boolean)
    m_IsJeReady = isReady
    Call UpdateUiState
End Sub

Public Sub SetTbState(ByVal isReady As Boolean)
    m_IsTbReady = isReady
    Call UpdateUiState
End Sub

Public Sub SetCalendarState(ByVal isReady As Boolean)
    m_IsCalendarReady = isReady
    Call UpdateUiState
End Sub

' [New] Centralized UI State Logic
Private Sub UpdateUiState()
    ' Enforce: Cannot map what you don't have
    Me.btnMapJe.Enabled = m_IsJeReady
    Me.btnMapTb.Enabled = m_IsTbReady
    
End Sub

Private Sub btnConfigureHolidays_Click()
    '---------------------------------------------------------------------------
    ' 開啟 HolidaysSheet 讓用戶填入假期資料
    '---------------------------------------------------------------------------
    Dim ws As Worksheet
    Set ws = SHT_HOLIDAYS
    ws.Activate
    '清空並初始化
    ws.Cells.Clear
    ws.Columns("A").NumberFormat = "m/d/yyyy"   '簡短日期
    ws.Columns("B").NumberFormat = "@"          '文字
    ws.Range("A1").value = "Date"
    ws.Range("B1").value = "Description"
    With ws.Range("A1:B1")
        .Font.Bold = True
        .Interior.color = RGB(245, 241, 222)
    End With
End Sub

Private Sub btnConfigureMakeUpDays_Click()
    '---------------------------------------------------------------------------
    ' 開啟 MakeupDaysSheet 讓用戶填入補班日資料
    '---------------------------------------------------------------------------
    Dim ws As Worksheet
    Set ws = SHT_MAKEUPDAYS
    ws.Activate
    '清空並初始化
    ws.Cells.Clear
    ws.Columns("A").NumberFormat = "m/d/yyyy"   '簡短日期
    ws.Columns("B").NumberFormat = "@"          '文字
    ws.Range("A1").value = "Date"
    ws.Range("B1").value = "Description"
    With ws.Range("A1:B1")
        .Font.Bold = True
        .Interior.color = RGB(245, 241, 222)
    End With
End Sub

Private Sub btnImportJe_Click()
    '---------------------------------------------------------------------------
    ' 交由觸發事件打開資料庫匯入精靈處理 JE
    '---------------------------------------------------------------------------
    RaiseEvent ImportJournalEntries(m_Format)
End Sub

Private Sub btnImportTb_Click()
    '---------------------------------------------------------------------------
    ' 交由觸發事件打開資料庫匯入精靈處理 TB
    '---------------------------------------------------------------------------
    RaiseEvent ImportTrialBalance(m_Format)
End Sub

Private Sub btnMapJe_Click()
    '---------------------------------------------------------------------------
    ' 交由觸發事件打開 MapJe 表單
    '---------------------------------------------------------------------------
    ' [Optimized] Double check, though UI should be disabled
    If Not m_IsJeReady Then
        MsgBox "請先完成 JE 匯入。", vbExclamation
        Exit Sub
    End If
    RaiseEvent JeFieldMappingRequested
End Sub

Private Sub btnMapTb_Click()
    '---------------------------------------------------------------------------
    ' 交由觸發事件打開 MapTb 表單
    '---------------------------------------------------------------------------
    ' [Optimized] Double check
    If Not m_IsTbReady Then
        MsgBox "請先完成 TB 匯入。", vbExclamation
        Exit Sub
    End If
    RaiseEvent TbFieldMappingRequested
End Sub

Private Sub btnApplyDateConfig_Click()
    '---------------------------------------------------------------------------
    ' 將日期設定流程獨立出來處理
    ' Date source validation marker: Holidays, MakeupDays, Weekend
    '---------------------------------------------------------------------------
    ' 觸發事件
    ' 檢查必填欄位
    Dim i As Long
    Dim errors As New Collection
    '檢查輸入 - 期間開始日
    If Trim(Me.txtbPeriodStart.value & "") = "" Then
        errors.Add "請填寫會計期間開始日"
    ElseIf Not IsDate(Me.txtbPeriodStart.value) Then
        errors.Add "會計期間開始日格式錯誤，請使用 yyyy/mm/dd 格式"
    End If
    '檢查輸入 - 期間結束日
    If Trim(Me.txtbPeriodStart.value & "") = "" Then
        errors.Add "請填寫會計期間結束日"
    ElseIf Not IsDate(Me.txtbPeriodEnd.value) Then
        errors.Add "會計期間結束日格式錯誤，請使用 yyyy/mm/dd 格式"
    End If
    '顯示錯誤訊息(若有)
    If errors.Count > 0 Then
        Dim errMsg As String
        errMsg = "請修正以下問題:" & vbCrLf & vbCrLf
        For i = 1 To errors.Count
            errMsg = errMsg & i & ". " & errors(i) & vbCrLf
        Next i
        MsgBox errMsg, vbExclamation, "資料設定失敗"
        Exit Sub
    End If
    '驗證資料邏輯
    If CDate(Me.txtbPeriodStart.value) > CDate(Me.txtbPeriodEnd.value) Then
        MsgBox "會計期間開始日不能晚於結束日", vbExclamation, "日期邏輯錯誤"
        Me.txtbPeriodStart.SetFocus
        Exit Sub
    End If
    '取得週末設定集合
    Dim weekendIndices As New Collection
    For i = 0 To Me.lstWeekend.ListCount - 1
        If Me.lstWeekend.selected(i) Then
            weekendIndices.Add i + 1
        End If
    Next i
    ' 驗證週末設定
    If weekendIndices.Count = 0 Then
        MsgBox "請至少選擇一個週末日", vbExclamation, "日期設定"
        Exit Sub
    End If
    '組裝 DTO 物件交給 Presenter 處理
    Dim dto As New DataTransferObject
    dto.PeriodStart = CDate(Me.txtbPeriodStart.value)
    dto.PeriodEnd = CDate(Me.txtbPeriodEnd.value)
    Set dto.weekendIndices = weekendIndices
    RaiseEvent UpdateDateDimensionRequested(dto)
End Sub

Private Sub btnExit_Click()
    '---------------------------------------------------------------------------
    ' 退出視窗前執行資料輸入檢查
    '---------------------------------------------------------------------------
    Dim errors As New Collection
    '檢查輸入 - 公司名稱
    If Trim(Me.txtbCompanyName.value & "") = "" Then
        errors.Add "請填寫公司名稱"
    End If
    '檢查輸入 - 財報準備期間開始日
    If Trim(Me.txtbPrepStartDate.value & "") = "" Then
        errors.Add "請填寫財報準備期間開始日"
    ElseIf Not IsDate(Me.txtbPrepStartDate.value) Then
        errors.Add "財報準備期間開始日格式錯誤，請使用 yyyy/mm/dd 格式"
    End If
    ' [New] Critical Process Check
    If Not m_IsCalendarReady Then
        MsgBox "嚴重錯誤：尚未建立日期維度表 (Date Dimension)。" & vbCrLf & _
               "請先設定日期區間並點擊「套用設定」。", vbCritical, "流程錯誤"
        Me.btnApplyDateConfig.SetFocus
        Exit Sub
    End If
    '顯示錯誤訊息(若有)
    If errors.Count > 0 Then
        Dim errMsg As String
        Dim i As Long
        errMsg = "請修正以下問題:" & vbCrLf & vbCrLf
        For i = 1 To errors.Count
            errMsg = errMsg & i & ". " & errors(i) & vbCrLf
        Next i
        MsgBox errMsg, vbExclamation, "資料設定失敗"
        Exit Sub
    End If
    '組裝 DTO 物件交給 Presenter 處理
    Dim dto As New DataTransferObject
    dto.CompanyName = CStr(Me.txtbCompanyName.value)
    dto.PrepStartDate = CDate(Me.txtbPrepStartDate.value)
    Me.Hide
    RaiseEvent Submitted(dto)
End Sub

Private Sub optCsv_Click()
    m_Format = "csv"
End Sub

Private Sub optXlsx_Click()
    m_Format = "xlsx"
End Sub
