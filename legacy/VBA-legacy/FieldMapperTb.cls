VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "FieldMapperTb"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: FieldMapperTb
' Purpose: 管理 TB (Trial Balance) 資料表的欄位映射
'
' Responsibility:
'   - 儲存「邏輯欄位名稱 → 實際欄位名稱」的對應關係
'   - 處理使用者自訂欄位映射
'   - 提供統一的欄位名稱存取介面
'
' Usage:
'   Dim TB As JeFieldMapper
'   Set TB = New JeFieldMapper
'   Call TB.SetField("DebitAmount", "借方金額")
'   sql = "SELECT [" & TB.DebitAmount & "] FROM TB"
'===============================================================================
Private m_Name As String
Private m_Method As Long
Private m_Fields As New Dictionary

'-- 方法
Private Sub Class_Initialize()
    '---------------------------------------------------------------------------
    ' 初始化 TB 資料實體物件
    '---------------------------------------------------------------------------
    m_Name = "TB"
    Dim fields As Variant
    Dim i As Long
    fields = Array( _
        "ChangeAmount", _
        "OpeningBalance", _
        "OpeningDebitBalance", _
        "OpeningCreditBalance", _
        "ClosingBalance", _
        "ClosingDebitBalance", _
        "ClosingCreditBalance", _
        "DebitAmount", _
        "CreditAmount", _
        "AccountNumber", _
        "AccountName" _
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
    ' 將 TB 欄位映射集合 m_Fields 鍵值對輸出至即時視窗
    '---------------------------------------------------------------------------
    Dim key As Variant
    Debug.Print "********** [ TB 欄位映射 ] **********"
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
Public Property Get ChangeAmount() As String
    ChangeAmount = m_Fields("ChangeAmount")
End Property
Public Property Get OpeningBalance() As String
    OpeningBalance = m_Fields("OpeningBalance")
End Property
Public Property Get OpeningDebitBalance() As String
    OpeningDebitBalance = m_Fields("OpeningDebitBalance")
End Property
Public Property Get OpeningCreditBalance() As String
    OpeningCreditBalance = m_Fields("OpeningCreditBalance")
End Property
Public Property Get ClosingBalance() As String
    ClosingBalance = m_Fields("ClosingBalance")
End Property
Public Property Get ClosingDebitBalance() As String
    ClosingDebitBalance = m_Fields("ClosingDebitBalance")
End Property
Public Property Get ClosingCreditBalance() As String
    ClosingCreditBalance = m_Fields("ClosingCreditBalance")
End Property
Public Property Get DebitAmount() As String
    DebitAmount = m_Fields("DebitAmount")
End Property
Public Property Get CreditAmount() As String
    CreditAmount = m_Fields("CreditAmount")
End Property
Public Property Get AccountNumber() As String
    AccountNumber = m_Fields("AccountNumber")
End Property
Public Property Get AccountName() As String
    AccountName = m_Fields("AccountName")
End Property
