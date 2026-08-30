VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "DbAccess"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class:    DbAccess
' Purpose:  統一所有資料庫操作介面
' Methods:
'===============================================================================
Private Const CHUNK_SIZE As Long = 100000
Private Const DO_EVENTS_INTERVAL As Double = 2#
Private m_Fso As FileSystemObject
Private m_Ws As DAO.Workspace
Private m_Db As DAO.Database
Private m_Conn As DAO.Connection
Private m_Path As String
Private m_IsConnected As Boolean
Private m_Tx As Boolean

'-- 屬性
Public Property Get Database() As DAO.Database
    Set Database = m_Db
End Property
Public Property Get Workspace() As DAO.Workspace
    Set Workspace = m_Ws
End Property
Public Property Get path() As String
    path = m_Path
End Property
Public Property Let path(ByVal value As String)
    m_Path = value
End Property

'-- 方法
Private Sub Class_Initialize()
    Set m_Fso = New FileSystemObject
End Sub

Public Sub Connect()
    '---------------------------------------------------------------------------
    ' 連線方法
    '---------------------------------------------------------------------------
    If m_IsConnected Then
        Exit Sub
    Else
        Dim wsName As String
        wsName = "WS_" & Format(Now(), "yyyymmddnnss") & "_" & Rnd()
        Set m_Ws = DAO.DBEngine.CreateWorkspace(wsName, "admin", "")
        Set m_Db = m_Ws.OpenDatabase(m_Path, False, False)
        m_IsConnected = True
    End If
End Sub

Public Sub Disconnect()
    '---------------------------------------------------------------------------
    ' 斷線方法
    '---------------------------------------------------------------------------
    If m_Tx Then
        Call RollbackTx
    End If
    Dim wsName As String
    If Not m_Ws Is Nothing Then wsName = m_Ws.name
    If Not m_Db Is Nothing Then
        m_Db.Close
        Set m_Db = Nothing
    End If
    If Not m_Ws Is Nothing Then
        m_Ws.Close
        Set m_Ws = Nothing
    End If
    m_IsConnected = False
End Sub

Public Sub Refresh(Optional forceDeep As Boolean = False)
    '---------------------------------------------------------------------------
    ' 統一的快取刷新方法
    ' 參數:
    '     - foreceDeep:
    '         - False (預設): 僅刷新資料快取與資料表清單，適用一般讀寫後
    '         - True: 強制深度刷新，會逐一刷新所有資料表的欄位結構
    '---------------------------------------------------------------------------
    On Error Resume Next
    If Not m_IsConnected Then Exit Sub
    '刷新 JET/ACE 引擎快取
    DAO.DBEngine.Idle dbRefreshCache
    '刷新資料表定義集合
    m_Db.TableDefs.Refresh
    '刷新資料表欄位結構
    If forceDeep Then
        Dim tdf As DAO.TableDef
        For Each tdf In m_Db.TableDefs
            '跳過系統表和臨時表
            If Not (tdf.name Like "MSys*" Or tdf.name Like "~TMPCLP*") Then
                tdf.fields.Refresh
                If Err.Number <> 0 Then
                    Debug.Print "警告: 無法刷新資料表欄位: " & tdf.name & " (錯誤: " & Err.Number & ")"
                    Err.Clear
                End If
            End If
        Next tdf
    End If
    On Error GoTo 0
End Sub

Public Function CompactAndRepair() As Boolean
    '---------------------------------------------------------------------------
    ' 統一的壓縮及修復資料庫方法
    '---------------------------------------------------------------------------
    On Error GoTo ErrorHandler
    CompactAndRepair = False
    '檢查是否連線
    If Not m_IsConnected Then Connect
    '檢查路徑是否有效
    If m_Path = "" Or Dir(m_Path) = "" Then
        Debug.Print "CompactAndRepair(): 路徑無效:" & m_Path
        Exit Function
    End If
    '儲存事務狀態
    Dim wasTx As Boolean
    wasTx = m_Tx
    If m_Tx Then Call CommitTx
    '建立暫存檔案路徑
    Dim tempPath As String
    tempPath = m_Fso.GetParentFolderName(m_Path) & "\" & _
               m_Fso.GetBaseName(m_Path) & "_temp_" & _
               Format(Now(), "yyyymmddnnss") & ".accdb"
    '關閉所有連線
    Call Disconnect
    '執行壓縮與修復
    Debug.Print "  - 壓縮與修復資料庫: " & m_Path
    DAO.DBEngine.CompactDatabase m_Path, tempPath
    '刪除原始檔案
    Kill m_Path
    '重新命名檔案
    Name tempPath As m_Path
    '重新連線資料庫
    Call Connect
    '返回原有的事務 (若有)
    If wasTx Then Call BeginTx
    '刷新快取
    Call Refresh(True)
    Debug.Print "CompactAndRepair(): 壓縮完成"
    CompactAndRepair = True
    Exit Function
ErrorHandler:
    Debug.Print "CompactAndRepair(): " & Err.Number & " - " & Err.description
End Function

Public Sub BeginTx()
    '---------------------------------------------------------------------------
    ' 開始一個交易區段，後續所有資料操作將暫存於此交易中，直到明確提交或回滾。
    '---------------------------------------------------------------------------
    If Not m_IsConnected Then Connect
    If m_Tx Then Exit Sub
    m_Ws.BeginTrans
    '刷新快取
    Call Refresh
    m_Tx = True
End Sub

Public Sub CommitTx()
    '---------------------------------------------------------------------------
    ' 提交目前交易區段，將所有暫存的資料操作正式寫入資料庫
    '---------------------------------------------------------------------------
    If Not m_Tx Then Exit Sub
    m_Ws.CommitTrans
    '刷新快取
    Call Refresh
    m_Tx = False
End Sub

Public Sub RollbackTx()
    '---------------------------------------------------------------------------
    ' 回滾目前交易區段，取消所有暫存的資料操作，並恢復至交易開始前的狀態
    '---------------------------------------------------------------------------
    If Not m_Tx Then Exit Sub
    m_Ws.Rollback
    '刷新快取
    Call Refresh
    m_Tx = False
End Sub

Public Function CreateTable() As Boolean
    '---------------------------------------------------------------------------
    '創建資料表
    '---------------------------------------------------------------------------
    If Not m_IsConnected Then Connect
End Function

Public Function DropTable(ByVal tableName As String) As Boolean
    '刪除資料表
    If Not m_IsConnected Then Connect
    If Not TableExists(tableName) Then
        DropTable = False
        Exit Function
    End If
    Call ExecuteNonQuery("DROP TABLE [" & tableName & "]")
    DropTable = True
End Function

Public Function TableExists(ByVal tableName As String) As Boolean
    '---------------------------------------------------------------------------
    ' 檢查資料表
    '---------------------------------------------------------------------------
    If Not m_IsConnected Then Connect
    Dim dummyTdf As DAO.TableDef
    On Error Resume Next
    Set dummyTdf = m_Db.TableDefs(tableName)
    If Err.Number = 0 Then
        TableExists = ((dummyTdf.Attributes And dbSystemObject) = 0)
    Else
        TableExists = False
    End If
    Err.Clear
    On Error GoTo 0
End Function

Public Function CreateField(ByVal fieldName As String) As Boolean
    '---------------------------------------------------------------------------
    ' ...
    '---------------------------------------------------------------------------
End Function


Public Function FieldExists(ByVal tdf As DAO.TableDef, ByVal fieldName As String) As Boolean
    '---------------------------------------------------------------------------
    ' 檢查指定欄位名稱是否存在於指定的資料表中
    '---------------------------------------------------------------------------
    '檢查欄位是否存在
    Dim dummyFld As DAO.field
    On Error Resume Next
    Set dummyFld = tdf.fields(fieldName)
    FieldExists = (Err.Number = 0)
    Err.Clear
    On Error GoTo 0
End Function

Public Function ExecuteQuery(ByVal sql As String) As DAO.Recordset
    '---------------------------------------------------------------------------
    ' 執行指定的 SQL 查詢語句並回傳 DAO.Recordset 結果集（唯讀快照）
    '---------------------------------------------------------------------------
    If Not m_IsConnected Then Connect
    Debug.Print String(60, "-")
    Debug.Print "<<" & Dir(m_Db.name) & ">> : "
    Debug.Print sql
    Debug.Print String(60, "-")
    '刷新快取
    Call Refresh
    '回傳記錄集
    Set ExecuteQuery = m_Db.OpenRecordset(sql, dbOpenSnapshot)
End Function

Public Function ExecuteNonQuery(ByVal sql As String, Optional logging As Boolean = True) As Long
    '---------------------------------------------------------------------------
    ' 執行指定的 SQL 指令（非查詢型），並回傳受影響的記錄筆數。
    '---------------------------------------------------------------------------
    If Not m_IsConnected Then Connect
    If logging Then
        Debug.Print String(60, "-")
        Debug.Print "<<" & Dir(m_Db.name) & ">> : "
        Debug.Print sql
        Debug.Print String(60, "-")
    End If
    m_Db.Execute sql, dbFailOnError
    '刷新快取
    Call Refresh
    '回傳受影響筆數
    ExecuteNonQuery = m_Db.RecordsAffected
End Function

Public Function Summarization( _
    ByVal tableName As String, _
    fieldToSum As String, _
    fieldToGroup As String _
)
    '---------------------------------------------------------------------------
    '負責資料彙總的功能，對資料按某些維度進行分組，並加總指定的數值欄位。
    '參數:
    '   - tableName:     要彙總的資料表
    '   - fieldToSum:    要加總的數值欄位
    '   - fieldToGroup:  要分組的屬性欄位
    '---------------------------------------------------------------------------
    Dim sql As String
    Dim newTable As String
    Dim newField As String
    newTable = tableName & "_SUM"
    newField = fieldToSum & "_SUM"
    sql = "SELECT " & _
          "  [" & fieldToGroup & "]," & _
          "  SUM([" & fieldToSum & "]) AS [" & newField & "]" & _
          "INTO " & newTable & " " & _
          "FROM " & tableName & " " & _
          "GROUP BY [" & fieldToGroup & "];"
    Call ExecuteNonQuery(sql)
End Function

Public Function GetTableFields(ByVal tableName As String) As Collection
    '---------------------------------------------------------------------------
    ' 取得指定資料表的所有欄位名稱，並以 Collection 形式回傳
    '---------------------------------------------------------------------------
    '取得指定資料表的欄位
    If Not m_IsConnected Then Connect
    Dim fields As New Collection
    Dim fld As DAO.field
    Dim rs As DAO.Recordset
    Set rs = m_Db.OpenRecordset(name:=tableName, Type:=dbOpenDynaset)
    '遍歷 RS 的欄位
    For Each fld In rs.fields
        fields.Add fld.name
    Next fld
    rs.Close
    Set rs = Nothing
    Set GetTableFields = fields
End Function


'-- 額外定義封裝的方法
Public Function ExtractNullRecords( _
    ByVal tableName As String, _
    ByVal fieldName As String, _
    ByVal newTableName As String, _
    Optional ByVal destinationDb As String = "" _
) As Boolean
    '---------------------------------------------------------------------------
    '方法: 提取指定欄位資料為空的記錄，並存為新資料表。
    '參數:
    '   - tableName:     來源資料表名稱
    '   - fieldName:     要檢查是否為空值的欄位名稱
    '   - newTableName:  新資料表名稱
    '   - targetDbPath:  目標資料庫路徑 (空字串表示同資料庫)
    '---------------------------------------------------------------------------
    On Error GoTo ErrorHandler
    ExtractNullRecords = False
    '確認資料庫連線且資料表存在
    If Not m_IsConnected Then Connect
    If Not TableExists(tableName) Then Exit Function
    '取得資料表定義，檢查指定欄位是否存在
    Dim tdf As DAO.TableDef: Set tdf = m_Db.TableDefs(tableName)
    If Not FieldExists(tdf, fieldName) Then Exit Function
    '取得欄位物件，準備分析欄位型別
    Dim fld As DAO.field: Set fld = tdf.fields(fieldName)
    Dim isNumeric As Boolean
    isNumeric = (fld.Type = dbByte Or fld.Type = dbInteger Or fld.Type = dbLong Or _
                 fld.Type = dbSingle Or fld.Type = dbDouble Or fld.Type = dbCurrency Or _
                 fld.Type = dbDecimal)
    '根據欄位型別建構不同的空值檢查條件
    Dim Condition As String
    If isNumeric Then
        '數值欄位：只檢查 NULL 值
        Condition = "[" & fieldName & "] IS NULL"
    Else
        '文字欄位：檢查 NULL 或去除空白後的空字串
        Condition = "[" & fieldName & "] IS NULL OR TRIM([" & fieldName & "]) = ''"
    End If
    '建構 SQL
    Dim sql As String
    If destinationDb <> "" Then
        '跨資料庫查詢
         sql = "SELECT * " & vbCrLf & _
              "INTO [" & newTableName & "] IN '" & destinationDb & "' " & vbCrLf & _
              "FROM [" & tableName & "] " & vbCrLf & _
              "WHERE " & Condition & ";"
    Else
        '同資料庫查詢
        sql = "SELECT * " & vbCrLf & _
              "INTO [" & newTableName & "] " & vbCrLf & _
              "FROM [" & tableName & "] " & vbCrLf & _
              "WHERE " & Condition & ";"
    End If
    '執行 SQL
    Call ExecuteNonQuery(sql)
    ExtractNullRecords = True
    Exit Function
ErrorHandler:
    Debug.Print "ExtractNullRecords():  " & Err.Number
    Debug.Print "  - 錯誤描述: " & Err.description
End Function

Public Function GetTotal( _
                    ByVal tableName As String, _
                    ByVal fieldName As String, _
                    ByVal totalType As String) As Double
    '---------------------------------------------------------------------------
    '方法: 取得資料表的統計值
    '參數:
    '   - tableName:    要統計的資料表名稱
    '   - fieldName:    要統計的欄位名稱 (空字串時僅統計記錄數)
    '   - totalType:    DBCount, NetValue, PositiveValue, NegativeValue, MaxValue, AverageValue
    '---------------------------------------------------------------------------
    On Error GoTo ErrorHandler
    If Not m_IsConnected Then Connect
    Dim sql As String
    Dim rs As DAO.Recordset
    '檢查資料表是否存在
    If Not TableExists(tableName) Then
        Debug.Print "  - 錯誤: 資料表 [" & tableName & "] 不存在"
        GetTotal = Null
        Exit Function
    End If
    '根據欄位名稱和統計類型建構 SQL
    If fieldName = "" Then
        Select Case totalType
            Case "DBCount"
                sql = "SELECT COUNT(*) AS Total FROM [" & tableName & "]"
            Case Else
                Debug.Print "  - 錯誤: 空欄位僅支援 DBCount, 收到: " & totalType
                GetTotal = Null
                Exit Function
        End Select
    Else
        '檢查欄位是否存在
        Dim tdf As DAO.TableDef
        Set tdf = m_Db.TableDefs(tableName)
        If Not FieldExists(tdf, fieldName) Then
            Debug.Print "  - 錯誤: 欄位 [" & fieldName & "] 在資料表 ["; tableName & "] 不存在"
            GetTotal = Null
            Exit Function
        End If
        '建構欄位統計 SQL
        Select Case totalType
            Case "NetValue"
                '淨值 (絕對值總和)
                sql = "SELECT ABS(SUM([" & fieldName & "])) AS Total FROM [" & tableName & "]"
            Case "PositiveValue"
                '正值總和 (借方值)
                sql = "SELECT SUM(IIF([" & fieldName & "] > 0, [" & fieldName & "], 0)) AS Total FROM [" & tableName & "]"
            Case "NegativeValue"
                '負值總和 (貸方值的絕對值)
                sql = "SELECT ABS(SUM(IIF([" & fieldName & "] < 0, [" & fieldName & "], 0))) AS Total FROM [" & tableName & "]"
            Case "MaxValue"
                '最大值
                sql = "SELECT MAX([" & fieldName & "]) AS Total FROM [" & tableName & "]"
            Case "AverageValue"
                '平均值
                sql = "SELECT AVG([" & fieldName & "]) AS Total FROM [" & tableName & "]"
            Case Else
                Debug.Print "  - 錯誤: 不支援的統計類型"
                GetTotal = Null
                Exit Function
        End Select
    End If
    '執行查詢並取得結果
    Debug.Print "  - 執行SQL: " & sql
    Set rs = m_Db.OpenRecordset(sql, dbOpenSnapshot)
    If Not rs.EOF And rs.BOF Then
        Debug.Print "  - 狀態: 記錄集為空"
        GetTotal = 0
    ElseIf IsNull(rs!Total) Then
        Debug.Print "  -  狀態: 查詢結果為 NULL"
        GetTotal = 0
    Else
        Dim actualTotal As Double
        actualTotal = CDbl(rs!Total)
        Debug.Print "  - 結果: " & GetTotal
        GetTotal = CDbl(rs!Total)
    End If
    '釋放資源
    rs.Close
    Set rs = Nothing
    Exit Function
ErrorHandler:
    Debug.Print "GetTotal():  " & Err.Number
    Debug.Print "  - 錯誤描述: " & Err.description
End Function


