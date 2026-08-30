VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceFilterCriteria"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class:    ServiceFilterCriteria
' Purpose:  定義篩選條件規則的服務
' Methods:
'
' Criteria 結構設計:
'   每個 Criterion 為一個 Dict:
'       - "Type"        : String, 條件類型 (Date/Text/Numeric)
'       - "Field"       : String, 欄位名稱 (PostDate, AccountNumber)
'       - "Operator"    : String, 運算子 (=, <>, >=, =< LIKE, IN, BETWEEN)
'       - "Value"       : Variant, 條件值 (String/Date/Number/Array)
'       - "Logic"       : String, 邏輯運算子 (AND/OR, AND)
'       - "Description" : String, 條件描述 (供UI顯示)
'   範例:
'       Dim criterion As New Dictionary
'       criterion("Type") = "Date"
'       criterion("Field") = "PostDate"
'       criterion("Operator") = "IN"
'       criterion("Value") = Array(#1/1/2025#, #1/7/2025#)
'       criterion("Logic") = "AND"
'       criterion("Description") = "總帳日期為週末"
'===============================================================================
'-- 全域常數
Private Const KEY_TYPE As String = "type"
Private Const KEY_FLD As String = "field"
Private Const KEY_OP As String = "operator"
Private Const KEY_VALUE As String = "value"
Private Const KEY_LOGIC As String = "logic"
Private Const KEY_DESC As String = "description"
'--
Private m_Db As DbAccess
Private m_Context As ContextManager
Private m_Criteria As Collection    '<-- Criteria is a collection of Criterion objects
Private m_Criterion As Dictionary   '<-- Criterion is a dictionary of parameters for one rule

Public Sub Initialize(context As ContextManager)
    Set m_Db = context.DbInput
    Set m_Context = context
    Call ClearCriteria
End Sub

'-------------------------------------------------------------------------------
' 取得指定索引的條件
' Usage:
'   Dim criteria As Collection
'   Set criteria = criteria.GetCriteria
'-------------------------------------------------------------------------------
Public Function GetCriteria() As Collection
    Call EnsureCriteria
    Set GetCriteria = m_Criteria
End Function

'-------------------------------------------------------------------------------
' 取得指定索引的條件
' Usage:
'   Dim criterion As Dictionary
'   Set criterion = criteria.GetCriterion(1)
'-------------------------------------------------------------------------------
Public Function GetCriterion(index As Long) As Dictionary
    Call EnsureCriteria
    Set GetCriterion = m_Criteria.item(index)
End Function

'-------------------------------------------------------------------------------
' 將條件轉換成 SQL WHERE 子句
' 回傳:
'   String - 完整的 WHERE 子句（不含 WHERE 關鍵字）
' 範例:
'   [IsManual] = True AND [PostDate[ IN (#1/1/2025#, #1/7/2025#)
'-------------------------------------------------------------------------------
Public Function BuildWhereClause() As String
    Dim whereClause As String
    Dim conditionSql As String
    Dim criterion As Dictionary
    Dim logicOperator As String
    Dim i As Long
    
    whereClause = ""
    '逐一處理 m_Criteria 裡的每一個篩選條件
    For i = 1 To m_Criteria.Count
        Set criterion = m_Criteria(i)
        
        '將條件轉成 SQL（例如 [Amount] > 100）
        conditionSql = BuildConditionSql(criterion)
        
        '若條件無效（例如欄位空、值空），跳過
        If Len(conditionSql) = 0 Then GoTo ContinueLoop
        
        '第一次加入條件：直接放進 whereClause
        If Len(whereClause) = 0 Then
            whereClause = conditionSql
        Else
            '第二條以上的條件，需要先加邏輯運算子（AND / OR）
            logicOperator = NormalizeLogic(criterion(KEY_LOGIC))
            whereClause = whereClause & " " & logicOperator & " " & conditionSql
        End If
ContinueLoop:
    Next i
    '若至少有一條條件，最前面加上 WHERE
    If Len(whereClause) > 0 Then
        whereClause = "WHERE " & whereClause
    End If
    BuildWhereClause = whereClause
End Function

'-------------------------------------------------------------------------------
' 將條件轉換為 SQL 片段，根據條件的 Operator 和 Value 型別生成對應語法
'-------------------------------------------------------------------------------
Public Function BuildConditionSql(criterion As Dictionary) As String
    Dim conditionSql As String
    Dim value As Variant
    ' 格式化欄位名稱
    Dim fieldName As String
    fieldName = FormatFieldName(criterion(KEY_FLD))
    If Len(fieldName) = 0 Then
        BuildConditionSql = ""
        Exit Function
    End If
    ' 正規化運算符
    Dim operatorText As String
    operatorText = NormalizeOperator(criterion(KEY_OP))
    value = criterion(KEY_VALUE)
    ' 根據運算符產生 SQL
    Select Case operatorText
        Case "IN", "NOT IN"
            ' 檢查 Value 是否為子查詢字串 (以 SELECT 開頭)
            ' 如果是子查詢，直接放入括號中，不進行字串跳脫
            If VarType(value) = vbString And UCase(Left(Trim(value), 6)) = "SELECT" Then
                conditionSql = fieldName & " " & operatorText & " (" & value & ")"
            Else
                ' 一般值列表 (Array 或 Collection)
                conditionSql = fieldName & " " & operatorText & " " & BuildInValues(value)
            End If
        Case "BETWEEN"
            ' BETWEEN a AND b
            conditionSql = BuildBetweenSql(fieldName, value)
        Case "LIKE"
            ' LIKE '*xxx*'
            conditionSql = fieldName & " LIKE " & FormatLikeValue(value)
        Case "IS NULL", "IS NOT NULL"
            ' IS NULL / IS NOT NULL
            conditionSql = fieldName & " " & operatorText
        Case Else
            ' 一般運算符 =, >, <, <>
            conditionSql = fieldName & " " & operatorText & " " & FormatSqlValue(value)
    End Select
    ' 加上括號保護
    If Len(conditionSql) > 0 Then
        conditionSql = "(" & conditionSql & ")"
    End If
    BuildConditionSql = conditionSql
End Function
'-------------------------------------------------------------------------------
' 新增單一條件至集合
' 範例:
'   Dim criterion As New Dictionary
'   criterion("Type") = "Field"
'   ...
'   Call criterion.AddCriterion(criteria)
'-------------------------------------------------------------------------------
Public Sub AddCriterion(criterion As Dictionary)
    Dim i As Long
    Dim errors As New Collection
    Call EnsureCriteria
    '驗證資料
    If Not criterion.Exists(KEY_TYPE) Then errors.Add "Criterion 缺少 " & KEY_TYPE
    If Not criterion.Exists(KEY_FLD) Then errors.Add "Criterion 缺少 " & KEY_FLD
    If Not criterion.Exists(KEY_OP) Then errors.Add "Criterion 缺少 " & KEY_OP
    If Not criterion.Exists(KEY_VALUE) Then errors.Add "Criterion 缺少 " & KEY_VALUE
    If Not criterion.Exists(KEY_LOGIC) Then errors.Add "Criterion 缺少 " & KEY_LOGIC
    If Not criterion.Exists(KEY_DESC) Then errors.Add "Criterion 缺少 " & KEY_DESC
    '顯示錯誤訊息(若有)
    If errors.Count > 0 Then
        Dim errMsg As String
        errMsg = "請修正以下問題: " & vbCrLf & vbCrLf
        For i = 1 To errors.Count
            errMsg = errMsg & i & ". " & errors(i) & vbCrLf
        Next i
        MsgBox errMsg, vbExclamation, "條件設定失敗"
        Exit Sub
    End If
    '增加條件
    m_Criteria.Add criterion
End Sub

'-------------------------------------------------------------------------------
' 移除指定索引的條件
' Usage:
'   Call filter.RemoveCriterion(2)
'-------------------------------------------------------------------------------
Public Sub RemoveCriterion(index As Long)
    m_Criteria.Remove (index)
End Sub

'-------------------------------------------------------------------------------
' 清空所有條件
' Usage:
'   Call filter.ClearCriteria()             '清空所有條件
'   Call filter.AddCriterion(newCriteria)   '重新開始新增條件
'-------------------------------------------------------------------------------
Public Sub ClearCriteria()
    Set m_Criteria = Nothing
    Dim criteria As New Collection
    Set m_Criteria = criteria
End Sub

'===============================================================================
' Helper
'===============================================================================

'-------------------------------------------------------------------------------
' 確保物件實體
'-------------------------------------------------------------------------------
Private Sub EnsureCriteria()
    If m_Criteria Is Nothing Then
        Set m_Criteria = New Collection
    End If
End Sub

'-------------------------------------------------------------------------------
' 正規化欄位名稱，加上 [] 以避免保留字衝突
'-------------------------------------------------------------------------------
Private Function FormatFieldName(ByVal fieldName As String) As String
    fieldName = Trim$(fieldName)
    '如果欄位名稱為空，則回傳空字串
    If Len(fieldName) = 0 Then
        FormatFieldName = ""
    '如果欄位名稱包含括號 (例如函數或運算式)，則直接回傳原始名稱
    ElseIf InStr(fieldName, "(") > 0 And InStr(fieldName, ")") > 0 Then
        FormatFieldName = fieldName
    '如果欄位名稱已用中括號包住，則直接回傳原始名稱
    ElseIf Left$(fieldName, 1) = "[" And Right$(fieldName, 1) = "]" Then
        FormatFieldName = fieldName
    '其他情況則自動加上中括號
    Else
        FormatFieldName = "[" & fieldName & "]"
    End If
End Function

'-------------------------------------------------------------------------------
' 正規化運算子
'-------------------------------------------------------------------------------
Private Function NormalizeOperator(ByVal opText As String) As String
    opText = Trim$(UCase$(opText))
    If opText = "" Then opText = "="
    NormalizeOperator = opText
End Function

'-------------------------------------------------------------------------------
' 正規化邏輯運算子 (AND/OR)
'-------------------------------------------------------------------------------
Private Function NormalizeLogic(ByVal logicText As String) As String
    logicText = Trim$(UCase$(logicText))
    If logicText <> "OR" Then logicText = "AND"
    NormalizeLogic = logicText
End Function

'-------------------------------------------------------------------------------
' 格式化 SQL Value
'-------------------------------------------------------------------------------
Private Function FormatSqlValue(ByVal value As Variant) As String
    If IsNull(value) Then
        FormatSqlValue = "Null"
        Exit Function
    End If

    Select Case VarType(value)
        Case vbString
            FormatSqlValue = "'" & EscapeSqlString(CStr(value)) & "'"
        Case vbBoolean
            FormatSqlValue = IIf(CBool(value), "True", "False")
        Case vbDate
            FormatSqlValue = "#" & Format$(CDate(value), "yyyy-mm-dd") & "#"
        Case vbByte, vbInteger, vbLong, vbSingle, vbDouble, vbCurrency, vbDecimal
            FormatSqlValue = CStr(value)
        Case Else
            FormatSqlValue = "'" & EscapeSqlString(CStr(value)) & "'"
    End Select
End Function

'-------------------------------------------------------------------------------
' LIKE 語句的值格式化，使用 * 作為萬用字元 (Jet SQL)
'-------------------------------------------------------------------------------
Private Function FormatLikeValue(ByVal value As Variant) As String
    Dim textValue As String
    If IsNull(value) Then value = ""
    ' 根據需求使用 *keyword* 格式
    textValue = "*" & EscapeSqlString(CStr(value)) & "*"
    FormatLikeValue = "'" & textValue & "'"
End Function

'-------------------------------------------------------------------------------
' 建立 IN (...) 內容
'-------------------------------------------------------------------------------
Private Function BuildInValues(ByVal value As Variant) As String
    Dim inClause As String
    Dim i As Long
    Dim item As Variant

    If IsArray(value) Then
        For i = LBound(value) To UBound(value)
            inClause = AppendListValue(inClause, FormatSqlValue(value(i)))
        Next i
    ElseIf TypeName(value) = "Collection" Then
        For Each item In value
            inClause = AppendListValue(inClause, FormatSqlValue(item))
        Next item
    ElseIf Not IsEmpty(value) And Not IsNull(value) Then
        inClause = AppendListValue(inClause, FormatSqlValue(value))
    End If
    If Len(inClause) = 0 Then
        inClause = "(Null)"
    Else
        inClause = "(" & inClause & ")"
    End If

    BuildInValues = inClause
End Function

'-------------------------------------------------------------------------------
' 建立 BETWEEN 語句內容
'-------------------------------------------------------------------------------
Private Function BuildBetweenSql(ByVal fieldName As String, ByVal value As Variant) As String
    ' Value 預期為 Array(Start, End)
    If IsArray(value) Then
        Dim valStart As String
        Dim valEnd As String
        valStart = FormatSqlValue(value(0))
        valEnd = FormatSqlValue(value(1))
        BuildBetweenSql = fieldName & " BETWEEN " & valStart & " AND " & valEnd
    Else
        BuildBetweenSql = ""
    End If
End Function

'-------------------------------------------------------------------------------
' 串接清單值
'-------------------------------------------------------------------------------
Private Function AppendListValue(ByVal listText As String, ByVal valueText As String) As String
    If Len(valueText) = 0 Then
        AppendListValue = listText
    ElseIf Len(listText) = 0 Then
        AppendListValue = valueText
    Else
        AppendListValue = listText & ", " & valueText
    End If
End Function

'-------------------------------------------------------------------------------
' SQL 字串跳脫
'-------------------------------------------------------------------------------
Private Function EscapeSqlString(ByVal valueText As String) As String
    EscapeSqlString = Replace(valueText, "'", "''")
End Function
