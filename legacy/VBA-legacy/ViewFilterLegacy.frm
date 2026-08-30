VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewFilterLegacy 
   Caption         =   "Legacy Filter"
   ClientHeight    =   8370.001
   ClientLeft      =   120
   ClientTop       =   465
   ClientWidth     =   8880.001
   OleObjectBlob   =   "ViewFilterLegacy.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewFilterLegacy"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Form:     ViewFilterLegacy
' Purpose:
' Methods:
'===============================================================================
Public Event ExecuteCriteriaRequested()
Public Event ShowCriteriaRequested()
Public Event Submitted(ByVal dto As DataTransferObject)
'--
Public criteriaStates As New Dictionary
Private m_LastSelection As String
Private Const LEGACY_SHEET_PREFIX As String = "LegacyFilter"
Private Const RESULT_TABLE_PREFIX As String = "FILTER_RESULT"
Private m_Db As DbAccess

'-------------------------------------------------------------------------------
' 先取消用不了的元件
'-------------------------------------------------------------------------------
Private Sub DisableControls()
    Me.chkApprovedOnWeekend.Enabled = False
    Me.chkApprovedOnHoliday.Enabled = False
    Me.chkExcludeApprovedOnMakeupDay.Enabled = False
    Me.chkSelectManualEntries.Enabled = False
End Sub

'-------------------------------------------------------------------------------
' 初始化
'-------------------------------------------------------------------------------
Public Sub Initialize(ByRef db As DbAccess)
    Call DisableControls
    Set m_Db = db
    Dim fields As Collection
    Set fields = db.GetTableFields("JE")
    Call UpdateFields(fields)
    Me.cboCriteriaSelector.Clear
    Dim i As Long
    Dim criteriaName As String
    Dim state As Dictionary
    ' 預設新增十組條件
    For i = 1 To 8
        Set state = New Dictionary
        criteriaName = "條件_" & CStr(i)
        Me.cboCriteriaSelector.AddItem criteriaName
        criteriaStates.Add criteriaName, state
    Next i
    ' 設定初始狀態
    m_LastSelection = "條件_1"
    Me.cboCriteriaSelector.value = "條件_1"
End Sub

'-------------------------------------------------------------------------------
' 若改變條件組合選單，則儲存改變前的狀態，並載入改變後所選取的條件組合
'-------------------------------------------------------------------------------
Private Sub cboCriteriaSelector_Change()
    Dim newKey As String
    newKey = Me.cboCriteriaSelector.value
    ' 儲存上一次選取項目的狀態  - Save Old
    Call SaveState(m_LastSelection)
    ' 載入目前選取項目的狀態    - Load New
    Call LoadState(newKey)
    ' 更新指標                  - Update Pointer
    m_LastSelection = newKey
End Sub

'-------------------------------------------------------------------------------
' 執行篩選條件
'-------------------------------------------------------------------------------
Private Sub btnExecuteCriteria_Click()
    SaveState (m_LastSelection)
    RaiseEvent ExecuteCriteriaRequested
End Sub

Private Sub btnShowCriteria_Click()
    Call SaveState(m_LastSelection)
    Call ShowSelectedCriteriaSheets
    RaiseEvent ShowCriteriaRequested
End Sub

'-------------------------------------------------------------------------------
' 退出表單
'-------------------------------------------------------------------------------
Private Sub btnExit_Click()
    '檢查並驗證
    Dim dto As New DataTransferObject
    '...
    Me.Hide
    Unload Me
    RaiseEvent Submitted(dto)
End Sub

'===============================================================================
' HELPER
'===============================================================================
' 儲存狀態，遍歷控制項 -> 寫入字典
'-------------------------------------------------------------------------------
Private Sub SaveState(ByVal key As String)
    If key = "" Then Exit Sub
    Dim state As New Dictionary
    Dim ctrl As MSForms.Control
    On Error Resume Next ' 忽略無 Value 屬性的控制項
    For Each ctrl In Me.Controls
        If ctrl.name <> "cboCriteriaSelector" Then
            Select Case TypeName(ctrl)
                Case "CheckBox", "ComboBox", "TextBox"
                    state(ctrl.name) = ctrl.value
            End Select
        End If
    Next ctrl
    On Error GoTo 0
    
    Set criteriaStates(key) = state
End Sub

'-------------------------------------------------------------------------------
' 載入狀態，遍歷控制項 -> (有紀錄? 載入/清空)
'-------------------------------------------------------------------------------
Private Sub LoadState(ByVal key As String)
    Dim state As Dictionary
    Set state = criteriaStates(key)
    Dim ctrl As MSForms.Control
    ' 如果該組設定是空的(全新)，則清空表單
    If state.Count = 0 Then
        On Error Resume Next
        For Each ctrl In Me.Controls
            If ctrl.name <> "cboCriteriaSelector" Then
                Select Case TypeName(ctrl)
                    Case "CheckBox": ctrl.value = False
                    Case "TextBox": ctrl.value = ""
                    Case "ComboBox": ctrl.value = ""
                End Select
            End If
        Next ctrl
        On Error GoTo 0
    Else
        ' 否則載入設定值
        Dim ctrlName As Variant
        On Error Resume Next
        For Each ctrlName In state.Keys
            Me.Controls(ctrlName).value = state(ctrlName)
        Next ctrlName
        On Error GoTo 0
    End If
End Sub

Private Sub UpdateFields(ByVal fields As Collection)
    '更新欄位
    Dim ctrl As MSForms.Control
    Dim cbo As MSForms.ComboBox
    Dim i As Long
    If fields Is Nothing Then Exit Sub
    '遍歷控制項
    For Each ctrl In Me.Controls
        If TypeOf ctrl Is MSForms.ComboBox Then
            Set cbo = ctrl
            cbo.Clear
            For i = 1 To fields.Count
                cbo.AddItem fields.item(i)
            Next i
        End If
    Next ctrl
End Sub

Private Sub ShowSelectedCriteriaSheets()
    Dim wb As Workbook
    Dim key As Variant
    Dim state As Dictionary
    Dim details As Collection
    Dim ws As Worksheet
    Dim sheetCount As Long
    Dim sheetName As String
    On Error Resume Next
    Set wb = ThisWorkbook
    On Error GoTo 0
    If wb Is Nothing Then Exit Sub
    Call ClearLegacyCriteriaSheets(wb)
    For Each key In criteriaStates.Keys
        Set state = criteriaStates(key)
        Set details = BuildCriteriaDetails(state)
        If details.Count > 0 Then
            sheetCount = sheetCount + 1
            Set ws = wb.Worksheets.Add(After:=wb.Worksheets(wb.Worksheets.Count))
            ' 設定工作表名稱與 CodeName
            sheetName = NextLegacySheetName(wb, sheetCount)
            ws.name = sheetName
            Call SetSheetCodeName(ws, sheetName)
            Dim nextRow As Long
            Call PopulateCriteriaSheet(ws, CStr(key), details, nextRow)
            Call PopulateCriteriaResultTable(ws, CStr(key), nextRow)
            ws.Columns.AutoFit
        End If
    Next key

    If sheetCount = 0 Then
        Set ws = wb.Worksheets.Add(After:=wb.Worksheets(wb.Worksheets.Count))
        sheetName = NextLegacySheetName(wb, 1)
        ws.name = sheetName
        Call SetSheetCodeName(ws, sheetName)
        
        ws.Cells(1, 1).value = "未啟用任何篩選條件"
        ws.Cells(2, 1).value = "請至少勾選一組條件後再試一次。"
        ws.Columns.AutoFit
    End If
End Sub

Private Sub ClearLegacyCriteriaSheets(ByVal wb As Workbook)
    Dim i As Long
    Dim prevAlerts As Boolean

    prevAlerts = Application.DisplayAlerts
    On Error GoTo Cleanup
    Application.DisplayAlerts = False

    For i = wb.Worksheets.Count To 1 Step -1
        If Left$(wb.Worksheets(i).name, Len(LEGACY_SHEET_PREFIX)) = LEGACY_SHEET_PREFIX Then
            wb.Worksheets(i).Delete
        End If
    Next i

Cleanup:
    Application.DisplayAlerts = prevAlerts
End Sub

Private Sub PopulateCriteriaSheet(ByVal ws As Worksheet, _
                                  ByVal key As String, _
                                  ByVal details As Collection, _
                                  Optional ByRef nextRow As Long)
    Dim rowIndex As Long
    Dim detail As Dictionary
    ws.Cells(1, 1).value = "篩選組合"
    ws.Cells(1, 2).value = key
    ws.Cells(3, 1).value = "欄位 / 條件"
    ws.Cells(3, 2).value = "值"
    ws.Cells(3, 3).value = "說明"
    rowIndex = 4
    For Each detail In details
        ws.Cells(rowIndex, 1).value = detail("field")
        ws.Cells(rowIndex, 2).value = detail("value")
        ws.Cells(rowIndex, 3).value = detail("description")
        rowIndex = rowIndex + 1
    Next detail
    ws.Columns.AutoFit
    nextRow = rowIndex + 2
End Sub

Private Sub PopulateCriteriaResultTable(ByVal ws As Worksheet, _
                                        ByVal key As String, _
                                        ByVal startRow As Long)
    Dim tableName As String
    Dim currentRow As Long
    Dim headerRow As Long
    Dim rs As DAO.Recordset
    Dim rowsCopied As Long
    Dim col As Long
    tableName = ResultTableName(key)
    If startRow < 1 Then startRow = 6
    currentRow = startRow
    ws.Cells(currentRow, 1).value = "篩選結果資料表"
    ws.Cells(currentRow, 2).value = tableName
    currentRow = currentRow + 1
    If m_Db Is Nothing Then
        ws.Cells(currentRow, 1).value = "DbAccess 尚未初始化，無法載入資料。"
        ws.Columns.AutoFit
        Exit Sub
    End If
    If Not m_Db.TableExists(tableName) Then
        ws.Cells(currentRow, 1).value = "尚未產生對應資料表，請先執行篩選。"
        ws.Columns.AutoFit
        Exit Sub
    End If
    On Error GoTo RenderError
    Set rs = m_Db.ExecuteQuery("SELECT * FROM [" & tableName & "];")
    If rs.BOF And rs.EOF Then
        ws.Cells(currentRow, 1).value = "(沒有資料列)"
        GoTo Cleanup
    End If
    headerRow = currentRow + 1
    For col = 0 To rs.fields.Count - 1
        ws.Cells(headerRow, col + 1).value = rs.fields(col).name
    Next col
    With ws.Range(ws.Cells(headerRow, 1), ws.Cells(headerRow, rs.fields.Count))
        .Font.Bold = True
        .Interior.color = RGB(245, 241, 222)
    End With
    currentRow = headerRow + 1
    If rs.RecordCount = -1 Then rs.MoveLast
    rowsCopied = rs.RecordCount
    rs.MoveFirst
    ws.Cells(currentRow, 1).CopyFromRecordset rs
    currentRow = currentRow + rowsCopied + 1
Cleanup:
    ws.Columns.AutoFit
    On Error Resume Next
    If Not rs Is Nothing Then
        rs.Close
        Set rs = Nothing
    End If
    On Error GoTo 0
    Exit Sub
RenderError:
    ws.Cells(currentRow, 1).value = "載入資料時發生錯誤：" & Err.description
    Debug.Print "PopulateCriteriaResultTable: " & Err.Number & " - " & Err.description
    Resume Cleanup
End Sub

Private Function ResultTableName(ByVal key As String) As String
    ResultTableName = RESULT_TABLE_PREFIX & "_" & key
End Function

Private Function BuildCriteriaDetails(ByVal state As Dictionary) As Collection
    Dim details As New Collection
    Dim fieldLabel As String
    Dim startValue As String
    Dim endValue As String
    Dim formattedValue As String
    If state Is Nothing Then
        Set BuildCriteriaDetails = details
        Exit Function
    End If
    If DictionaryBool(state, "chkPostedOnWeekend") Then AppendDetail details, "過帳日期", "週末", "過帳日期為週末"
    If DictionaryBool(state, "chkApprovedOnWeekend") Then AppendDetail details, "核准日期", "週末", "核准日期為週末"
    If DictionaryBool(state, "chkPostedOnHoliday") Then AppendDetail details, "過帳日期", "假日", "過帳日期為假日"
    If DictionaryBool(state, "chkApprovedOnHoliday") Then AppendDetail details, "核准日期", "假日", "核准日期為假日"
    If DictionaryBool(state, "chkExcludePostedOnMakeupDay") Then AppendDetail details, "過帳日期", "排除補班日", "排除補班日過帳"
    If DictionaryBool(state, "chkExcludeApprovedOnMakeupDay") Then AppendDetail details, "核准日期", "排除補班日", "排除補班日核准"
    If DictionaryBool(state, "chkOnlyDebit") Then AppendDetail details, "金額", "> 0", "僅借方"
    If DictionaryBool(state, "chkOnlyCredit") Then AppendDetail details, "金額", "< 0", "僅貸方"
    If DictionaryBool(state, "chkSelectManualEntries") Then AppendDetail details, "傳票類型", "手動", "僅手動輸入"
    If DictionaryBool(state, "chkKeywordFilter") Then
        fieldLabel = DictionaryText(state, "cboKeywordFilter")
        formattedValue = DictionaryText(state, "txtbKeywordFilter")
        If Len(fieldLabel) > 0 And Len(formattedValue) > 0 Then
            AppendDetail details, fieldLabel, formattedValue, "關鍵字篩選"
        End If
    End If
    If DictionaryBool(state, "chkDateRangeFilter") Then
        fieldLabel = DictionaryText(state, "cboDateRangeFilter")
        startValue = DictionaryText(state, "txtbDateRangeStart")
        endValue = DictionaryText(state, "txtbDateRangeEnd")
        formattedValue = FormatRangeValue(startValue, endValue)
        If Len(fieldLabel) > 0 And Len(formattedValue) > 0 Then
            AppendDetail details, fieldLabel, formattedValue, "日期區間"
        End If
    End If
    If DictionaryBool(state, "chkNumericValueFilter") Then
        fieldLabel = DictionaryText(state, "cboNumericValueFilter")
        startValue = DictionaryText(state, "txtbNumericValueStart")
        endValue = DictionaryText(state, "txtbNumericValueEnd")
        formattedValue = FormatRangeValue(startValue, endValue)
        If Len(fieldLabel) > 0 And Len(formattedValue) > 0 Then
            AppendDetail details, fieldLabel, formattedValue, "數值區間"
        End If
    End If
    Set BuildCriteriaDetails = details
End Function

Private Sub AppendDetail(ByVal details As Collection, ByVal fieldLabel As String, ByVal valueLabel As String, ByVal description As String)
    Dim item As Dictionary
    Set item = New Dictionary
    item.Add "field", fieldLabel
    item.Add "value", valueLabel
    item.Add "description", description
    details.Add item
End Sub

Private Function DictionaryBool(ByVal state As Dictionary, ByVal key As String) As Boolean
    On Error Resume Next
    DictionaryBool = CBool(state(key))
    If Err.Number <> 0 Then
        DictionaryBool = False
        Err.Clear
    End If
    On Error GoTo 0
End Function

Private Function DictionaryText(ByVal state As Dictionary, ByVal key As String) As String
    On Error Resume Next
    DictionaryText = Trim$(CStr(state(key)))
    If Err.Number <> 0 Then
        DictionaryText = ""
        Err.Clear
    End If
    On Error GoTo 0
End Function

Private Function FormatRangeValue(ByVal startValue As String, ByVal endValue As String) As String
    If Len(startValue) = 0 And Len(endValue) = 0 Then
        FormatRangeValue = ""
    ElseIf Len(startValue) = 0 Then
        FormatRangeValue = "<= " & endValue
    ElseIf Len(endValue) = 0 Then
        FormatRangeValue = ">= " & startValue
    Else
        FormatRangeValue = startValue & " ~ " & endValue
    End If
End Function

Private Function NextLegacySheetName(ByVal wb As Workbook, ByVal index As Long) As String
    Dim candidate As String
    candidate = LEGACY_SHEET_PREFIX & "_" & Format$(index, "00")
    Do While SheetExists(wb, candidate)
        index = index + 1
        candidate = LEGACY_SHEET_PREFIX & "_" & Format$(index, "00")
    Loop
    NextLegacySheetName = candidate
End Function

Private Function SheetExists(ByVal wb As Workbook, ByVal sheetName As String) As Boolean
    Dim ws As Worksheet
    On Error Resume Next
    Set ws = wb.Worksheets(sheetName)
    On Error GoTo 0
    SheetExists = Not ws Is Nothing
    Set ws = Nothing
End Function

Private Sub SetSheetCodeName(ByVal ws As Worksheet, ByVal newCodeName As String)
    ' 注意：此功能需要 Excel 信任中心設定中啟用「信任存取 VBA 專案物件模型」
    On Error Resume Next
    ThisWorkbook.VBProject.VBComponents(ws.CodeName).name = newCodeName
    If Err.Number <> 0 Then
        Debug.Print "SetSheetCodeName Warning: Unable to set CodeName to '" & newCodeName & "'. Error: " & Err.description
        Err.Clear
    End If
    On Error GoTo 0
End Sub
