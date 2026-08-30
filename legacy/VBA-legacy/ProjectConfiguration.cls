VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ProjectConfiguration"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: ProjectConfiguration
' Purpose: 用來處理專案所需各類資訊的資料實體
'
' Responsibility:
'   - 儲存專案基本資訊（公司名稱、測試期間）
'   - 管理使用者資訊（姓名、網域）
'   - 提供工作日設定
'
' Usage:
'   Dim info As ProjectConfiguration
'   Set info = context.ProjectConfig
'
'   MsgBox "公司: " & info.CompanyName & vbCrLf & _
'          "期間: " & info.PeriodStart & " ~ " & info.PeriodEnd
'===============================================================================
'--使用者資訊
Private m_WSHnet As WshNetwork
Private m_UserName As String        '--使用者名稱
Private m_UserDomain As String
Private m_UserFullName As String    '--使用者全名
Private m_UserObject As Object
'-- 專案資訊
Private m_CompanyName As String     '--公司名稱
Private m_PeriodStart As Date       '--測試資料期間開始日
Private m_PeriodEnd As Date         '--測試資料期間結束日
Private m_PrepStartDate As Date     '--財報準備期間開始日

Public Sub Initialize()
    Set m_WSHnet = New WshNetwork
    Set m_UserObject = GetObject("WinNT://" & m_UserDomain & "/" & m_UserName & ",user")
    '取得使用者名稱與網域
    Let m_UserName = m_WSHnet.UserName
    Let m_UserDomain = m_WSHnet.UserDomain
    Let m_UserFullName = m_UserObject.FullName
    If m_UserFullName = "" Then m_UserFullName = m_UserName
End Sub

'--使用者名稱
Public Property Get UserName() As String
    Let UserName = m_UserName
End Property
'--使用者全名
Public Property Get UserFullName() As String
    Let UserFullName = m_UserFullName
End Property
'--測試資料期間開始日
Public Property Get PeriodStart() As Date
    Let PeriodStart = m_PeriodStart
End Property
Public Property Let PeriodStart(ByVal value As Date)
    Let m_PeriodStart = value
End Property
'--測試資料期間結束日
Public Property Get PeriodEnd() As Date
    Let PeriodEnd = m_PeriodEnd
End Property
Public Property Let PeriodEnd(ByVal value As Date)
    Let m_PeriodEnd = value
End Property
'--財報準備期間開始日
Public Property Get PrepStartDate() As Date
    Let PrepStartDate = m_PrepStartDate
End Property
Public Property Let PrepStartDate(ByVal value As Date)
    Let m_PrepStartDate = value
End Property
'--公司名稱
Public Property Get CompanyName() As String
    Let CompanyName = m_CompanyName
End Property
Public Property Let CompanyName(ByVal value As String)
    Let m_CompanyName = value
End Property
