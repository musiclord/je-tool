VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ContextManager"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class:    ContextManager
' Purpose:  上下文管理器，用於在各模組間傳遞專案運作所需的物件
' Methods:
'===============================================================================
Private m_DbSchema As DbSchema
'--
Private m_DbInput As DbAccess
Private m_DbValid As DbAccess
Private m_DbCriteria As DbAccess
Private m_DbReport As DbAccess
'--
Private m_Info As ProjectConfiguration
Private m_JE As FieldMapperJe
Private m_TB As FieldMapperTb
'------------------------------------------------------------------------------
' 屬性：m_DbSchema
' 功能：提供資料表與欄位的名稱定義
' 型別：DatabaseSchema (用戶自定義型別物件)
' 存取：Get – 唯讀
' 備註：
'------------------------------------------------------------------------------
Public Property Get DbSchema() As DbSchema
    Set DbSchema = m_DbSchema
End Property
'------------------------------------------------------------------------------
' 屬性：DbInput
' 功能：提供輸入資料的資料庫存取物件
' 型別：DbAccess (物件)
' 存取：Get/Set – 可讀寫
' 備註：需先初始化 DbAccess 實例，否則傳回 Nothing
'------------------------------------------------------------------------------
Public Property Get DbInput() As DbAccess
    Set DbInput = m_DbInput
End Property
Public Property Set DbInput(ByVal db As DbAccess)
    Set m_DbInput = db
End Property
'------------------------------------------------------------------------------
' 屬性：DbValid
' 功能：提供驗證資料的資料庫存取物件
' 型別：DbAccess (物件)
' 存取：Get/Set – 可讀寫
' 備註：用於驗證流程，需與 DbInput 保持一致性
'------------------------------------------------------------------------------
Public Property Get DbValid() As DbAccess
    Set DbValid = m_DbValid
End Property
Public Property Set DbValid(ByVal db As DbAccess)
    Set m_DbValid = db
End Property
'------------------------------------------------------------------------------
' 屬性：DbCriteria
' 功能：提供篩選條件的資料庫存取物件
' 型別：DbAccess (物件)
' 存取：Get/Set – 可讀寫
' 備註：用於過濾輸入資料，支援查詢條件設定
'------------------------------------------------------------------------------
Public Property Get DbCriteria() As DbAccess
    Set DbCriteria = m_DbCriteria
End Property
Public Property Set DbCriteria(ByVal db As DbAccess)
    Set m_DbCriteria = db
End Property
'------------------------------------------------------------------------------
' 屬性：DbReport
' 功能：提供報告輸出的資料庫存取物件
' 型別：DbAccess (物件)
' 存取：Get/Set – 可讀寫
' 備註：用於產生報告或輸出結果
'------------------------------------------------------------------------------
Public Property Get DbReport() As DbAccess
    Set DbReport = m_DbReport
End Property
Public Property Set DbReport(ByVal db As DbAccess)
    Set m_DbReport = db
End Property
'------------------------------------------------------------------------------
' 屬性：Info
' 功能：提供專案資訊的資料實體 (ProjectConfiguration)
' 型別：ProjectConfiguration (物件)
' 存取：Get – 唯讀
' 備註：由外部模組初始化後傳入，用於存放專案層級的基本資訊
'------------------------------------------------------------------------------
Public Property Get info() As ProjectConfiguration
    Set info = m_Info
End Property
'------------------------------------------------------------------------------
' 方法：Initialize
' 功能：初始化 ContextManager 所需的所有內部物件
' 輸入：無
' 回傳：無
' 備註：呼叫後，所有 DbAccess 與實體物件皆已建立，可直接使用
'------------------------------------------------------------------------------
Public Sub Initialize()
    Set m_DbSchema = New DbSchema
    Set m_DbInput = New DbAccess
    Set m_DbValid = New DbAccess
    Set m_DbCriteria = New DbAccess
    Set m_DbReport = New DbAccess
    Set m_JE = New FieldMapperJe
    Set m_TB = New FieldMapperTb
    Set m_Info = New ProjectConfiguration
    '-- 封裝至 Schema，使外部呼叫皆以 Schema 為主
    Call m_DbSchema.Initialize(m_JE, m_TB)
End Sub

