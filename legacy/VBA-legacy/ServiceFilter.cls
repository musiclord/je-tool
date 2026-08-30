VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceFilter"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class:    ServiceFilter
' Purpose:  管理篩選條件作業的服務
'
' Note:     這裡主要負責執行篩選條件，管理篩選條件由 ServiceFilterCriteria 負責
'           因此該服務主要職責應該是負責運行篩選條件的程序，
'===============================================================================
Private m_Db As DbAccess
Private m_Context As ContextManager
Private m_Schema As DbSchema
Private m_CriteriaSet As Dictionary 'key:String, item:ServiceFilterCriteria
Private m_JeFieldLookup As Dictionary
'--
Private Const CRIT_KEY_FIELD As String = "field"
Private Const CRIT_KEY_OPERATOR As String = "operator"
Private Const CRIT_KEY_VALUE As String = "value"
Private Const CRIT_KEY_DESC As String = "description"

Public Sub Initialize(context As ContextManager)
    Set m_Db = context.DbInput
    Set m_Schema = context.DbSchema
    Set m_Context = context
    Set m_CriteriaSet = New Dictionary
    Call CacheJeFieldNames
End Sub

'-------------------------------------------------------------------------------
' 執行批次篩選條件組合
'-------------------------------------------------------------------------------
Public Sub ExecuteBatch(ByVal prefix As String)
    On Error GoTo ErrorHandler
    Dim criteria As ServiceFilterCriteria
    Dim whereClause As String
    Dim sql As String
    Dim lastSql As String
    Dim key As Variant
    Dim tableName As String
    '遍歷所有條件組合 (條件_1 ~ 條件_8)
    For Each key In m_CriteriaSet.Keys
        '設定結果表名稱
        tableName = prefix & "_" & key
        '如果表存在則刪除
        If m_Db.TableExists(tableName) Then m_Db.DropTable (tableName)
        '取得該組合的篩選條件
        Set criteria = m_CriteriaSet(key)
        '建立 WHERE 子句
        whereClause = criteria.BuildWhereClause()
        If Len(whereClause) > 0 Then
            sql = "SELECT * INTO [" & tableName & "] " & _
                  "FROM [" & m_Schema.JE.name & "] " & _
                  whereClause
            lastSql = sql
            Debug.Print "Executing SQL for " & key & ": " & sql
            m_Db.ExecuteNonQuery sql
        Else
            Debug.Print "Executing SQL for " & key & ": skipped (WHERE clause empty)."
        End If
    Next key
    Exit Sub
ErrorHandler:
    Debug.Print "ExecuteBatch Error:" & Err.Number & " - " & Err.description
    If Len(lastSql) > 0 Then Debug.Print "  Failed SQL: " & lastSql
End Sub

'-------------------------------------------------------------------------------
' 處理篩選條件集合
'-------------------------------------------------------------------------------
Public Sub ProcessCriteriaSet(ByVal criteriaSet As Dictionary, ByVal source As String)
    On Error GoTo ErrorHandler
    If criteriaSet Is Nothing Then Exit Sub

    If m_CriteriaSet Is Nothing Then
        Set m_CriteriaSet = New Dictionary
    Else
        m_CriteriaSet.RemoveAll
    End If

    Select Case LCase$(source)
        Case "legacy"
            Debug.Print "Processing [Legacy]:"
            Dim state As Dictionary
            Dim key As Variant
            Dim keyName As String
            Dim rules As Long
            For Each key In criteriaSet.Keys
                keyName = CStr(key)
                Debug.Print "  -> Inspecting [" & keyName & "]"
                Set state = criteriaSet(key)
                '...existing guards...
                Call TraceStateSnapshot(keyName, state)
                Call AddCriteria(keyName)
                Call MapLegacyState(keyName, state)
                rules = CriteriaRuleCount(keyName)
                If rules = 0 Then
                    TraceFilterInfo keyName, "Removed (no valid rules built)."
                    If m_CriteriaSet.Exists(keyName) Then m_CriteriaSet.Remove keyName
                Else
                    TraceFilterInfo keyName, "Registered " & rules & " rule(s)."
                    Call TraceCriteriaDetail(keyName)
                End If
ContinueLoop:
            Next key
        Case "current"
            Debug.Print "Processing [Current]: (not implemented in legacy demo)"
        Case Else
            Debug.Print "Processing [" & source & "] skipped."
    End Select
    Exit Sub
ErrorHandler:
    Debug.Print "  - ProcessCriteriaSet:" & Err.Number & " - " & Err.description
End Sub

'===============================================================================
' Helper
'===============================================================================

'-------------------------------------------------------------------------------
' 新增一個條件組合至集合內（key = 使用情境名稱）
'-------------------------------------------------------------------------------
Public Sub AddCriteria(ByVal key As String)
    On Error GoTo ErrorHandler
    If m_CriteriaSet Is Nothing Then Set m_CriteriaSet = New Dictionary
    If m_CriteriaSet.Exists(key) Then m_CriteriaSet.Remove key

    Dim criteria As New ServiceFilterCriteria
    Call criteria.Initialize(m_Context)
    m_CriteriaSet.Add key, criteria
    Exit Sub
ErrorHandler:
    Debug.Print "  - AddCriteria:" & Err.Number & " - " & Err.description
End Sub

'-------------------------------------------------------------------------------
' 移除指定鍵值的條件組合
'-------------------------------------------------------------------------------
Public Sub RemoveCriteria(ByVal key As String)
    On Error GoTo ErrorHandler
    ' key 已經由使用者輸入過，因此前端的 ListBox 可以選擇目前儲存的條件組合
    ' 使用者選取後，傳入參數為 key 來指定使用者選擇的條件組合做移除
    Call m_CriteriaSet.Remove(key)
    Exit Sub
ErrorHandler:
    Debug.Print "  - RemoveCriteria:" & Err.Number & " - " & Err.description
End Sub

'-------------------------------------------------------------------------------
' 增加一個篩選條件至「指定鍵值」的條件組合內
'-------------------------------------------------------------------------------
Public Sub AddCriterion(ByVal key As String, ByVal criterion As Dictionary)
    On Error GoTo ErrorHandler
    Dim criteria As ServiceFilterCriteria
    Set criteria = GetCriteria(key)
    Call criteria.AddCriterion(criterion)
    Exit Sub
ErrorHandler:
    Debug.Print "  - AddCriterion:" & Err.Number & " - " & Err.description
End Sub

'-------------------------------------------------------------------------------
' 移除指定條件組合鍵值的篩選條件
'-------------------------------------------------------------------------------
Public Sub RemoveCriterion(ByVal key As String, ByVal index As Long)
    On Error GoTo ErrorHandler
    Dim criteria As ServiceFilterCriteria
    Set criteria = GetCriteria(key)
    Call criteria.RemoveCriterion(index)
    Exit Sub
ErrorHandler:
    Debug.Print "  - RemoveCriterion:" & Err.Number & " - " & Err.description
End Sub

'-------------------------------------------------------------------------------
' 取出指定鍵值的條件組合（內部使用）
'-------------------------------------------------------------------------------
Private Function GetCriteria(ByVal key As String) As ServiceFilterCriteria
    On Error Resume Next '若已存在 key 則忽略
    Set GetCriteria = m_CriteriaSet(key)
    On Error GoTo 0
End Function


'-------------------------------------------------------------------------------
' 設置條件組合，傳入參數為該條件組合的鍵值及對應的字典
' 由於 Legacy 方法是藉由元件名稱對應 BOOLEAN 值來啟用指定篩選條件
' 在單個條件組合中若啟用多個項目
' 最後仍由 ServiceFilterCriteria 合併為 SQL 字串
'-------------------------------------------------------------------------------
Public Sub MapLegacyState(ByVal key As String, state As Dictionary)
    If state Is Nothing Then Exit Sub
    Dim criterion As Dictionary
    Dim fieldName As String
    Dim startDate As Variant
    Dim endDate As Variant
    Dim startValue As Variant
    Dim endValue As Variant
    
    '1.
    If StateBool(state, "chkPostedOnWeekend") Then
        If m_JeFieldLookup.Exists(UCase$(m_Schema.JE.PostDate)) Then
            Set criterion = New Dictionary
            With m_Schema
                criterion("type") = "Date"
                criterion("field") = "CDate([" & .JE.PostDate & "])"
                criterion("operator") = "IN"
                criterion("value") = "SELECT [" & .DateDimension.DateKey & "] FROM " & .DateDimension.name & _
                                     " WHERE [" & .DateDimension.IsWeekend & "] = True"
                criterion("logic") = "AND"
                criterion("description") = "過帳日期在週末"
            End With
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Skip 'chkPostedOnWeekend': Field '" & m_Schema.JE.PostDate & "' not found in JE."
        End If
    End If
    '2.
    If StateBool(state, "chkApprovedOnWeekend") Then
        If m_JeFieldLookup.Exists(UCase$(m_Schema.JE.ApprovalDate)) Then
            Set criterion = New Dictionary
            With m_Schema
                criterion("type") = "Date"
                criterion("field") = "CDate([" & .JE.ApprovalDate & "])"
                criterion("operator") = "IN"
                criterion("value") = "SELECT [" & .DateDimension.DateKey & "] FROM " & .DateDimension.name & _
                                     " WHERE [" & .DateDimension.IsWeekend & "] = True"
                criterion("logic") = "AND"
                criterion("description") = "核准日期在週末"
            End With
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Skip 'chkApprovedOnWeekend': Field '" & m_Schema.JE.ApprovalDate & "' not found in JE."
        End If
    End If
    '3.
    If StateBool(state, "chkPostedOnHoliday") Then
        If m_JeFieldLookup.Exists(UCase$(m_Schema.JE.PostDate)) Then
            Set criterion = New Dictionary
            With m_Schema
                criterion("type") = "Date"
                criterion("field") = "CDate([" & .JE.PostDate & "])"
                criterion("operator") = "IN"
                criterion("value") = "SELECT [" & .DateDimension.DateKey & "] FROM " & .DateDimension.name & _
                                     " WHERE [" & .DateDimension.IsHolidays & "] = True"
                criterion("logic") = "AND"
                criterion("description") = "過帳日期在假日"
            End With
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Skip 'chkPostedOnHoliday': Field '" & m_Schema.JE.PostDate & "' not found in JE."
        End If
    End If
    '4.
    If StateBool(state, "chkApprovedOnHoliday") Then
        If m_JeFieldLookup.Exists(UCase$(m_Schema.JE.ApprovalDate)) Then
            Set criterion = New Dictionary
            With m_Schema
                criterion("type") = "Date"
                criterion("field") = "CDate([" & .JE.ApprovalDate & "])"
                criterion("operator") = "IN"
                criterion("value") = "SELECT [" & .DateDimension.DateKey & "] FROM " & .DateDimension.name & _
                                     " WHERE [" & .DateDimension.IsHolidays & "] = True"
                criterion("logic") = "AND"
                criterion("description") = "核准日期在假日"
            End With
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Skip 'chkApprovedOnHoliday': Field '" & m_Schema.JE.ApprovalDate & "' not found in JE."
        End If
    End If
    '5.
    If StateBool(state, "chkExcludePostedOnMakeupDay") Then
        If m_JeFieldLookup.Exists(UCase$(m_Schema.JE.PostDate)) Then
            Set criterion = New Dictionary
            With m_Schema
                criterion("type") = "Date"
                criterion("field") = "CDate([" & .JE.PostDate & "])"
                criterion("operator") = "NOT IN"
                criterion("value") = "SELECT [" & .DateDimension.DateKey & "] FROM " & .DateDimension.name & _
                                     " WHERE [" & .DateDimension.IsMakeupDays & "] = True"
                criterion("logic") = "AND"
                criterion("description") = "排除補班日過帳"
            End With
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Skip 'chkExcludePostedOnMakeupDay': Field '" & m_Schema.JE.PostDate & "' not found in JE."
        End If
    End If
    '6.
    If StateBool(state, "chkExcludeApprovedOnMakeupDay") Then
        If m_JeFieldLookup.Exists(UCase$(m_Schema.JE.ApprovalDate)) Then
            Set criterion = New Dictionary
            With m_Schema
                criterion("type") = "Date"
                criterion("field") = "CDate([" & .JE.ApprovalDate & "])"
                criterion("operator") = "NOT IN"
                criterion("value") = "SELECT [" & .DateDimension.DateKey & "] FROM " & .DateDimension.name & _
                                     " WHERE [" & .DateDimension.IsMakeupDays & "] = True"
                criterion("logic") = "AND"
                criterion("description") = "排除補班日核准"
            End With
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Skip 'chkExcludeApprovedOnMakeupDay': Field '" & m_Schema.JE.ApprovalDate & "' not found in JE."
        End If
    End If

    '7. 僅借方：使用金額為正判斷
    If StateBool(state, "chkOnlyDebit") Then
        ' FIX: Use m_Schema.JE.Amount instead of m_Schema.fields.JeAmount
        fieldName = m_Schema.JE.Amount
        If m_JeFieldLookup.Exists(UCase$(fieldName)) Then
            Set criterion = New Dictionary
            criterion("type") = "Numeric"
            criterion("field") = "[" & fieldName & "]"
            criterion("operator") = ">"
            criterion("value") = 0
            criterion("logic") = "AND"
            criterion("description") = "僅借方 (金額 > 0)"
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Skip 'chkOnlyDebit': Field '" & fieldName & "' not found in JE."
        End If
    End If

    '8. 僅貸方：使用金額為負判斷
    If StateBool(state, "chkOnlyCredit") Then
        ' FIX: Use m_Schema.JE.Amount instead of m_Schema.fields.JeAmount
        fieldName = m_Schema.JE.Amount
        If m_JeFieldLookup.Exists(UCase$(fieldName)) Then
            Set criterion = New Dictionary
            criterion("type") = "Numeric"
            criterion("field") = "[" & fieldName & "]"
            criterion("operator") = "<"
            criterion("value") = 0
            criterion("logic") = "AND"
            criterion("description") = "僅貸方 (金額 < 0)"
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Skip 'chkOnlyCredit': Field '" & fieldName & "' not found in JE."
        End If
    End If
    '9.
    If StateBool(state, "chkSelectManualEntries") Then
        Set criterion = New Dictionary
        With m_Schema
            criterion("type") = "Boolean"
            criterion("field") = "[" & .JE.IsManual & "]"
            criterion("operator") = "="
            criterion("value") = True
            criterion("logic") = "AND"
            criterion("description") = "僅手動分錄"
        End With
        Call AddCriterion(key, criterion)
    End If
    '10.
    If StateBool(state, "chkKeywordFilter") Then
        Dim keywordRawField As String
        keywordRawField = StateText(state, "cboKeywordFilter")
        fieldName = ResolveJeField(keywordRawField)
        Dim keywordValue As String
        keywordValue = StateText(state, "txtbKeywordFilter")
        If Len(fieldName) > 0 And Len(keywordValue) > 0 Then
            Set criterion = New Dictionary
            criterion("type") = "String"
            criterion("field") = fieldName
            criterion("operator") = "LIKE"
            criterion("value") = keywordValue
            criterion("logic") = "AND"
            criterion("description") = "關鍵字篩選"
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Keyword filter ignored (field/value incomplete: " & keywordRawField & ")."
        End If
    End If
    '11.
    If StateBool(state, "chkDateRangeFilter") Then
        Dim dateRawField As String
        dateRawField = StateText(state, "cboDateRangeFilter")
        fieldName = ResolveJeField(dateRawField)
        startDate = StateDateValue(state, "txtbDateRangeStart")
        endDate = StateDateValue(state, "txtbDateRangeEnd")
        If Len(fieldName) > 0 And Not IsNull(startDate) And Not IsNull(endDate) Then
            Set criterion = New Dictionary
            criterion("type") = "Date"
            criterion("field") = "CDate([" & fieldName & "])"
            criterion("operator") = "BETWEEN"
            criterion("value") = Array(startDate, endDate)
            criterion("logic") = "AND"
            criterion("description") = "日期範圍篩選"
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Date range filter ignored (field/value incomplete: " & dateRawField & ")."
        End If
    End If
    '12.
    If StateBool(state, "chkNumericValueFilter") Then
        Dim numericRawField As String
        numericRawField = StateText(state, "cboNumericValueFilter")
        fieldName = ResolveJeField(numericRawField)
        startValue = StateNumericValue(state, "txtbNumericValueStart")
        endValue = StateNumericValue(state, "txtbNumericValueEnd")
        If Len(fieldName) > 0 And Not IsNull(startValue) And Not IsNull(endValue) Then
            Set criterion = New Dictionary
            criterion("type") = "Numeric"
            criterion("field") = fieldName
            criterion("operator") = "BETWEEN"
            criterion("value") = Array(startValue, endValue)
            criterion("logic") = "AND"
            criterion("description") = "數值範圍篩選"
            Call AddCriterion(key, criterion)
        Else
            TraceFilterInfo key, "Numeric range filter ignored (field/value incomplete: " & numericRawField & ")."
        End If
    End If
End Sub

Private Function CriteriaRuleCount(ByVal key As String) As Long
    Dim criteria As ServiceFilterCriteria
    Set criteria = GetCriteria(key)
    If criteria Is Nothing Then Exit Function
    On Error Resume Next
    CriteriaRuleCount = criteria.GetCriteria.Count
    On Error GoTo 0
End Function
Private Function StateHasFilters(state As Dictionary) As Boolean
    If state Is Nothing Then Exit Function
    If state.Count = 0 Then Exit Function

    Dim flagKeys As Variant
    Dim flag As Variant
    flagKeys = Array("chkPostedOnWeekend", "chkApprovedOnWeekend", _
                     "chkPostedOnHoliday", "chkApprovedOnHoliday", _
                     "chkExcludePostedOnMakeupDay", "chkExcludeApprovedOnMakeupDay", _
                     "chkOnlyDebit", "chkOnlyCredit", "chkSelectManualEntries", _
                     "chkKeywordFilter", "chkDateRangeFilter", "chkNumericValueFilter")
    For Each flag In flagKeys
        If StateBool(state, CStr(flag)) Then
            StateHasFilters = True
            Exit Function
        End If
    Next flag
End Function

Private Function StateBool(state As Dictionary, ByVal field As String) As Boolean
    Dim raw As Variant
    raw = GetStateValue(state, field)
    If IsNull(raw) Or IsEmpty(raw) Then Exit Function
    On Error Resume Next
    StateBool = CBool(raw)
    On Error GoTo 0
End Function

Private Function StateText(state As Dictionary, ByVal field As String) As String
    Dim raw As Variant
    raw = GetStateValue(state, field)
    If IsNull(raw) Or IsEmpty(raw) Then
        StateText = ""
    Else
        StateText = Trim$(CStr(raw))
    End If
End Function

Private Function StateDateValue(state As Dictionary, ByVal field As String) As Variant
    Dim raw As Variant
    raw = GetStateValue(state, field)
    If IsNull(raw) Or IsEmpty(raw) Then
        StateDateValue = Null
    ElseIf IsDate(raw) Then
        StateDateValue = CDate(raw)
    Else
        StateDateValue = Null
    End If
End Function

Private Function StateNumericValue(state As Dictionary, ByVal field As String) As Variant
    Dim raw As Variant
    raw = GetStateValue(state, field)
    If IsNull(raw) Or IsEmpty(raw) Then
        StateNumericValue = Null
        Exit Function
    End If

    Dim cleaned As String
    cleaned = Replace(Trim$(CStr(raw)), ",", "")
    If cleaned = "" Then
        StateNumericValue = Null
    ElseIf isNumeric(cleaned) Then
        StateNumericValue = CDbl(cleaned)
    Else
        StateNumericValue = Null
    End If
End Function

Private Function NormalizeFieldToken(ByVal fieldName As String) As String
    fieldName = Trim$(fieldName)
    fieldName = Replace(fieldName, "[", "")
    fieldName = Replace(fieldName, "]", "")
    NormalizeFieldToken = fieldName
End Function

Private Function GetStateValue(state As Dictionary, ByVal field As String) As Variant
    If state Is Nothing Then
        GetStateValue = Empty
        Exit Function
    End If
    On Error GoTo MissingKey
    GetStateValue = state(field)
    Exit Function
MissingKey:
    GetStateValue = Empty
    Err.Clear
End Function

Private Sub TraceFilterInfo(ByVal key As String, ByVal message As String)
    Debug.Print "     [" & key & "] " & message
End Sub

Private Sub TraceStateSnapshot(ByVal key As String, state As Dictionary)
    Dim flagKeys As Variant
    Dim name As Variant
    flagKeys = Array("chkPostedOnWeekend", "chkApprovedOnWeekend", _
                     "chkPostedOnHoliday", "chkApprovedOnHoliday", _
                     "chkExcludePostedOnMakeupDay", "chkExcludeApprovedOnMakeupDay", _
                     "chkOnlyDebit", "chkOnlyCredit", "chkSelectManualEntries", _
                     "chkKeywordFilter", "chkDateRangeFilter", "chkNumericValueFilter")
    For Each name In flagKeys
        If StateBool(state, CStr(name)) Then
            Debug.Print "     [" & key & "] flag ON -> " & name
        End If
    Next name

    Dim textFields As Variant
    textFields = Array("cboKeywordFilter", "txtbKeywordFilter", _
                       "cboDateRangeFilter", "txtbDateRangeStart", "txtbDateRangeEnd", _
                       "cboNumericValueFilter", "txtbNumericValueStart", "txtbNumericValueEnd")
    For Each name In textFields
        Dim valueText As String
        valueText = StateText(state, CStr(name))
        If Len(valueText) > 0 Then
            Debug.Print "     [" & key & "] value -> " & name & " = " & valueText
        End If
    Next name
End Sub

Private Sub TraceCriteriaDetail(ByVal key As String)
    Dim criteria As ServiceFilterCriteria
    Set criteria = GetCriteria(key)
    If criteria Is Nothing Then Exit Sub

    Dim items As Collection
    Set items = criteria.GetCriteria
    Dim i As Long
    Dim rule As Dictionary
    For i = 1 To items.Count
        Set rule = items(i)
        Debug.Print "     [" & key & "] #" & i & " " & CriterionSummary(rule)
    Next i
End Sub

Private Function CriterionSummary(criterion As Dictionary) As String
    CriterionSummary = NzString(criterion(CRIT_KEY_DESC)) & " -> " & _
                       NzString(criterion(CRIT_KEY_FIELD)) & " " & _
                       NzString(criterion(CRIT_KEY_OPERATOR)) & " " & _
                       ValuePreview(criterion(CRIT_KEY_VALUE))
End Function

Private Function NzString(ByVal value As Variant) As String
    If IsNull(value) Or IsEmpty(value) Then
        NzString = ""
    Else
        NzString = CStr(value)
    End If
End Function

Private Function ValuePreview(ByVal value As Variant) As String
    If IsNull(value) Then
        ValuePreview = "(Null)"
    ElseIf IsArray(value) Then
        Dim i As Long
        Dim tmp As String
        For i = LBound(value) To UBound(value)
            tmp = tmp & IIf(Len(tmp) > 0, " ~ ", "") & CStr(value(i))
        Next i
        ValuePreview = "[" & tmp & "]"
    ElseIf TypeName(value) = "Collection" Then
        Dim item As Variant
        Dim buffer As String
        For Each item In value
            buffer = buffer & IIf(Len(buffer) > 0, ", ", "") & CStr(item)
        Next item
        ValuePreview = "[" & buffer & "]"
    Else
        ValuePreview = CStr(value)
    End If
End Function

Private Function ResolveJeField(ByVal fieldToken As String) As String
    fieldToken = StripBrackets(fieldToken)
    If Len(fieldToken) = 0 Then Exit Function
    Call EnsureFieldLookup
    If m_JeFieldLookup Is Nothing Then Exit Function

    Dim key As String
    key = UCase$(fieldToken)
    If m_JeFieldLookup.Exists(key) Then
        ResolveJeField = m_JeFieldLookup(key)
    End If
End Function

Private Sub EnsureFieldLookup()
    If m_JeFieldLookup Is Nothing Or m_JeFieldLookup.Count = 0 Then
        Call CacheJeFieldNames
    End If
End Sub

Private Sub CacheJeFieldNames()
    On Error GoTo ExitProc
    Dim fields As Collection
    Set fields = m_Db.GetTableFields(m_Schema.JE.name)
    Set m_JeFieldLookup = New Dictionary
    If fields Is Nothing Then GoTo ExitProc

    Dim i As Long
    Dim fieldName As String
    For i = 1 To fields.Count
        fieldName = StripBrackets(CStr(fields(i)))
        If Len(fieldName) > 0 Then
            m_JeFieldLookup(UCase$(fieldName)) = fieldName
        End If
    Next i
ExitProc:
End Sub

Private Function StripBrackets(ByVal textValue As String) As String
    textValue = Trim$(textValue)
    textValue = Replace$(textValue, "[", "")
    textValue = Replace$(textValue, "]", "")
    StripBrackets = textValue
End Function
