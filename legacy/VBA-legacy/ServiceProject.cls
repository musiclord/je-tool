VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceProject"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: ServiceProject
' Purpose: 管理專案的服務，主要負責創建與載入
'===============================================================================
Private m_Context As ContextManager
Private m_Path As String

Public Sub Initialize(ByRef context As ContextManager)
    Set m_Context = context
End Sub
Public Function NewProject(ByVal path As String) As Boolean
    '---------------------------------------------------------------------------
    ' 依路徑創建專案目錄，並創建資料庫檔案
    ' 參數:
    '     - path: 要建立專案目錄的路徑
    '---------------------------------------------------------------------------
    m_Path = path
    If Dir(m_Path, vbDirectory) <> "" Then
        MsgBox "專案已存在", vbExclamation, "創建專案"
        Exit Function
    Else
        MkDir m_Path
        Set m_Context.DbInput = CreateDb("input")
        Set m_Context.DbValid = CreateDb("valid")
        Set m_Context.DbCriteria = CreateDb("criteria")
        Set m_Context.DbReport = CreateDb("report")
    End If
End Function

Public Function SelectProject(ByVal path As String) As Boolean
    '---------------------------------------------------------------------------
    ' 依路徑載入專案目錄，並載入資料庫檔案
    ' 參數:
    '     - path: 要載入專案目錄的路徑
    '---------------------------------------------------------------------------
    m_Path = path
    If Dir(m_Path, vbDirectory) = "" Then
        MsgBox "專案不存在", vbExclamation, "載入專案"
        Exit Function
    Else
        Set m_Context.DbInput = LoadDb("input")
        Set m_Context.DbValid = LoadDb("valid")
        Set m_Context.DbCriteria = LoadDb("criteria")
        Set m_Context.DbReport = LoadDb("report")
    End If
End Function

Private Function CreateDb(ByVal dbName As String) As DbAccess
    '---------------------------------------------------------------------------
    ' 根據專案目錄創建資料庫檔案 (.accdb)
    ' 參數:
    '     - dbName: 資料庫名稱
    '---------------------------------------------------------------------------
    Dim dbe As New DAO.DBEngine
    Dim dbs As DAO.Database
    Dim db As New DbAccess
    Dim dbPath As String
    dbPath = m_Path & "\" & dbName & ".accdb"
    Set dbs = dbe.CreateDatabase(dbPath, dbLangGeneral)
    dbs.Close
    db.path = dbPath
    Set CreateDb = db
End Function

Private Function LoadDb(ByVal dbName As String) As DbAccess
    '---------------------------------------------------------------------------
    ' 根據專案目錄載入資料庫檔案 (.accdb)
    ' 參數:
    '     - dbName: 資料庫名稱
    '---------------------------------------------------------------------------
    Dim dbPath As String
    dbPath = m_Path & "\" & dbName & ".accdb"
    If Dir(dbPath) = "" Then '
        Debug.Print "File Not Found."
    End If
    Dim db As New DbAccess
    db.path = dbPath
    Set LoadDb = db
End Function
