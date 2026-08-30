VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "FieldMapperJe"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: FieldMapperJe
' Purpose: 管理 JE (Journal Entry) 資料表的欄位映射
'
' Responsibility:
'   - 儲存「邏輯欄位名稱 → 實際欄位名稱」的對應關係
'   - 處理使用者自訂欄位映射
'   - 提供統一的欄位名稱存取介面
'
' Usage:
'   Dim JE As JeFieldMapper
'   Set JE = New JeFieldMapper
'   Call JE.SetField("DebitAmount", "借方金額")
'   sql = "SELECT [" & JE.DebitAmount & "] FROM JE"
'===============================================================================
Private m_Name As String
Private m_Method As Long
Private m_Fields As New Dictionary
'-- 方法
Private Sub Class_Initialize()
    '---------------------------------------------------------------------------
    ' 初始化 JE 資料實體物件
    '---------------------------------------------------------------------------
    m_Name = "JE"
    Dim fields As Variant
    Dim i As Long
    fields = Array( _
        "Amount", _
        "DebitAmount", _
        "CreditAmount", _
        "EntryDescription", _
        "AccountNumber", _
        "AccountName", _
        "DocumentNumber", _
        "ApprovedBy", _
        "CreatedBy", _
        "SourceModule", _
        "DrCr", _
        "IsDebit", _
        "IsManual", _
        "IsApprovedDateAsLedgerDate", _
        "PostDate", _
        "ApprovalDate", _
        "LineItem" _
    )
    For i = LBound(fields) To UBound(fields)
        m_Fields(fields(i)) = fields(i)
    Next i
End Sub

Public Sub SetField(ByVal key As String, ByVal value As String)
    '---------------------------------------------------------------------------
    ' 設定指定欄位鍵值對，僅在欄位已存在且值不為空字串時更新
    '---------------------------------------------------------------------------
    If m_Fields.Exists(key) And Len(value) > 0 Then
        m_Fields(key) = value
    End If
End Sub

Public Sub PrintFields()
    '---------------------------------------------------------------------------
    ' 將 JE 欄位映射集合 m_Fields 鍵值對輸出至即時視窗
    '---------------------------------------------------------------------------
    Dim key As Variant
    Debug.Print "********** [ JE 欄位映射 ] **********"
    Debug.Print String(60, "-")
    For Each key In m_Fields.Keys
        Debug.Print CStr(key) & ": " & CStr(m_Fields(key))
    Next key
    Debug.Print String(60, "-")
End Sub

'-- 屬性訪問
Public Property Get name() As String
    name = m_Name
End Property
Public Property Let name(ByVal value As String)
    m_Name = value
End Property
Public Property Get method() As Long
    method = m_Method
End Property
Public Property Let method(ByVal value As Long)
    m_Method = value
End Property
Public Property Get fields() As Dictionary
    Set fields = m_Fields
End Property

'-- 屬性快速補全
Public Property Get Amount() As String
    '傳票金額
    Amount = m_Fields("Amount")
End Property
Public Property Get DebitAmount() As String
    '借方金額
    DebitAmount = m_Fields("DebitAmount")
End Property
Public Property Get CreditAmount() As String
    '貸方金額
    CreditAmount = m_Fields("CreditAmount")
End Property
Public Property Get EntryDescription() As String
    '傳票摘要
    EntryDescription = m_Fields("EntryDescription")
End Property
Public Property Get AccountNumber() As String
    '科目號碼
    AccountNumber = m_Fields("AccountNumber")
End Property
Public Property Get AccountName() As String
    '科目名稱
    AccountName = m_Fields("AccountName")
End Property
Public Property Get DocumentNumber() As String
    '傳票號碼
    DocumentNumber = m_Fields("DocumentNumber")
End Property
Public Property Get ApprovedBy() As String
    '傳票核准人員
    ApprovedBy = m_Fields("ApprovedBy")
End Property
Public Property Get CreatedBy() As String
    '傳票建立人員
    CreatedBy = m_Fields("CreatedBy")
End Property
Public Property Get SourceModule() As String
    '分錄來源模組
    SourceModule = m_Fields("SourceModule")
End Property
Public Property Get DrCr() As String
    '借貸別
    DrCr = m_Fields("DrCr")
End Property
Public Property Get IsDebit() As String
    '借方標誌
    IsDebit = m_Fields("IsDebit")
End Property
Public Property Get IsManual() As String
    '人工/自動分錄
    IsManual = m_Fields("IsManual")
End Property
Public Property Get IsApprovedDateAsLedgerDate() As String
    '核准日同總帳日
    IsApprovedDateAsLedgerDate = m_Fields("IsApprovedDateAsLedgerDate")
End Property
Public Property Get PostDate() As String
    '總帳日期
    PostDate = m_Fields("PostDate")
End Property
Public Property Get ApprovalDate() As String
    '核准日期
    ApprovalDate = m_Fields("ApprovalDate")
End Property
Public Property Get LineItem() As String
    '項次
    LineItem = m_Fields("LineItem")
End Property
