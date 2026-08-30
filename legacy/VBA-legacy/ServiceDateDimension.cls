VERSION 1.0 CLASS
BEGIN
  MultiUse = -1  'True
END
Attribute VB_Name = "ServiceDateDimension"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = False
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Class: ServiceDateDimension
' Purpose:  負責管理日期維度表 (Date Dimension)
'           以每日為單位提供標準化的日期屬性，其用途為:
'               - 提供唯一的日期鍵值 YYYYMMDD 為主鍵
'               - 儲存日期相關屬性如 Year, Month, Day, DayOfWeek
'               - 標示是否為周末(IsWeekend)、假日(IsHolidays)、補班日(IsMakeupDays)
'               - 提供假期與補班日的文字描述 (HolidaysDesc, MakeupDaysDesc)
' NonBusinessDays:
'     - Holidays: 國定假日
'     - MakeUpDays: 補班日/加班日
'     - Weekend: 週末
'===============================================================================
'--
Private m_Context As ContextManager
Private m_Db As DbAccess
Private m_Schema As DbSchema

Public Sub Initialize(context As ContextManager)
    Set m_Context = context
    Set m_Db = context.DbInput
    Set m_Schema = context.DbSchema
End Sub

Public Sub MergeWeekends(ByVal selectedDays As Variant)
    '---------------------------------------------------------------------------
    ' 將使用者於表單中設定的周末，合併至 DateDimension 資料表
    '---------------------------------------------------------------------------
    Dim dayList As String
    Dim item As Variant
    Dim affectedCount As Long
    '指定資料表綱要物件
    Dim tbl As TypeTableDateDimension
    tbl = m_Schema.DateDimension
    '將使用者勾選的星期數字組合成逗號分隔字串
    For Each item In selectedDays
        If dayList <> "" Then dayList = dayList & ","
        dayList = dayList & item
    Next item
    '清除欄位資料
    Dim sql As String
    sql = "UPDATE " & tbl.name & " SET [" & tbl.IsWeekend & "] = False;"
    Call m_Db.ExecuteNonQuery(sql)
    'Weekday(d) 默認星期日為第一天，回傳編碼
    sql = "UPDATE " & tbl.name & " " & vbCrLf & _
          "SET [" & tbl.IsWeekend & "] = True " & vbCrLf & _
          "WHERE Weekday([" & tbl.DateKey & "]) IN (" & dayList & ");"
    affectedCount = m_Db.ExecuteNonQuery(sql)
    Debug.Print "  - 已套用 " & selectedDays.Count & " 個週末設定 (共影響 " & affectedCount & " 筆記錄)"
End Sub

Public Sub MergeHolidays(rsHolidays As DAO.Recordset)
    '---------------------------------------------------------------------------
    ' 將使用者於工作表匯入的假期資料，合併至 DateDimension 資料表
    '---------------------------------------------------------------------------
    If rsHolidays Is Nothing Then Exit Sub
    If rsHolidays.EOF Then Exit Sub
    
    Dim totalCount As Long
    Dim affectedCount As Long
    '指定資料表綱要物件
    Dim tbl As TypeTableDateDimension
    tbl = m_Schema.DateDimension
    '取得總筆數
    rsHolidays.MoveFirst
    If rsHolidays.RecordCount = -1 Then rsHolidays.MoveLast
    totalCount = rsHolidays.RecordCount
    rsHolidays.MoveFirst
    
    Do While Not rsHolidays.EOF
        '預期 rsHolidays 有欄位 Date(DATE), Description(TEXT)
        Dim holidayDate As Variant
        Dim HolidayDesc As String
        holidayDate = rsHolidays.fields(0).value
        If rsHolidays.fields.Count > 1 And Not IsNull(rsHolidays.fields(1).value) Then
            HolidayDesc = rsHolidays.fields(1).value
        Else
            HolidayDesc = vbNullString
        End If
        If IsDate(holidayDate) Then
            Dim sql As String
            sql = "UPDATE " & tbl.name & " " & vbCrLf & _
                  "SET [" & tbl.IsHolidays & "] = True, " & vbCrLf & _
                  "    [" & tbl.HolidaysDesc & "] = '" & Replace(HolidayDesc, "'", "''") & "' " & vbCrLf & _
                  "WHERE [" & tbl.DateKey & "] = #" & Format(CDate(holidayDate), "mm/dd/yyyy") & "#;"
            affectedCount = affectedCount + m_Db.ExecuteNonQuery(sql)
        End If
        rsHolidays.MoveNext
    Loop
    rsHolidays.Close
    Debug.Print "  - 已套用 " & totalCount & " 筆假日資料 (共影響 " & affectedCount & " 筆記錄)"
End Sub

Public Sub MergeMakeupDays(rsMakeupDays As DAO.Recordset)
    '---------------------------------------------------------------------------
    ' 將使用者於工作表匯入的補班日資料，合併至 DateDimension 資料表
    '---------------------------------------------------------------------------
    If rsMakeupDays Is Nothing Then Exit Sub
    If rsMakeupDays.EOF Then Exit Sub
    
    Dim totalCount As Long
    Dim affectedCount As Long
    '指定資料表綱要物件
    Dim tbl As TypeTableDateDimension
    tbl = m_Schema.DateDimension
    '取得總筆數
    rsMakeupDays.MoveFirst
    If rsMakeupDays.RecordCount = -1 Then rsMakeupDays.MoveLast
    totalCount = rsMakeupDays.RecordCount
    rsMakeupDays.MoveFirst
    
    Do While Not rsMakeupDays.EOF
        ' 預期 rsMakeupDays 有欄位 Date(DATE), Description(TEXT)
        Dim makeupDate As Variant
        Dim makeupDesc As String
        makeupDate = rsMakeupDays.fields(0).value
        If rsMakeupDays.fields.Count > 1 And Not IsNull(rsMakeupDays.fields(1).value) Then
            makeupDesc = rsMakeupDays.fields(1).value
        Else
            makeupDesc = vbNullString
        End If
        If IsDate(makeupDate) Then
            Dim sql As String
            sql = "UPDATE " & tbl.name & " " & vbCrLf & _
                  "SET [" & tbl.IsMakeupDays & "] = True, " & vbCrLf & _
                  "    [" & tbl.MakeupDaysDesc & "] = '" & Replace(makeupDesc, "'", "''") & "' " & vbCrLf & _
                  "WHERE [" & tbl.DateKey & "] = #" & Format(CDate(makeupDate), "mm/dd/yyyy") & "#;"
            affectedCount = affectedCount + m_Db.ExecuteNonQuery(sql)
        End If
        rsMakeupDays.MoveNext
    Loop
    rsMakeupDays.Close
    Debug.Print "  - 已套用 " & totalCount & " 筆補班日資料 (共影響 " & affectedCount & " 筆記錄)"
End Sub

Public Sub CreateDateDimensionTable()
    '---------------------------------------------------------------------------
    ' 設定日期維度表的起始日及結束日
    '---------------------------------------------------------------------------
    Dim dateStart As Date
    Dim dateEnd As Date
    dateStart = m_Context.info.PeriodStart
    dateEnd = m_Context.info.PeriodEnd
    '指定資料表綱要物件
    Dim tbl As TypeTableDateDimension
    tbl = m_Schema.DateDimension
    '檢查日期邏輯
    If dateStart = 0 Or dateEnd = 0 Or dateStart > dateEnd Then Exit Sub
    '檢查資料表是否存在
    If m_Db.TableExists(tbl.name) Then
        Call m_Db.DropTable(tbl.name)
    End If
    '建立日期維度表
    Dim sql As String
    sql = "CREATE TABLE " & tbl.name & " ( " & vbCrLf & _
          "    [" & tbl.DateKey & "] DATE, " & vbCrLf & _
          "    [" & tbl.Year & "] SHORT, " & vbCrLf & _
          "    [" & tbl.Month & "] SHORT, " & vbCrLf & _
          "    [" & tbl.Day & "] SHORT, " & vbCrLf & _
          "    [" & tbl.DayOfWeek & "] SHORT, " & vbCrLf & _
          "    [" & tbl.IsWeekend & "] YESNO, " & vbCrLf & _
          "    [" & tbl.IsHolidays & "] YESNO," & vbCrLf & _
          "    [" & tbl.IsMakeupDays & "] YESNO," & vbCrLf & _
          "    [" & tbl.HolidaysDesc & "] TEXT(50)," & vbCrLf & _
          "    [" & tbl.MakeupDaysDesc & "] TEXT(50)," & vbCrLf & _
          "    CONSTRAINT PK_" & tbl.name & " PRIMARY KEY ([" & tbl.DateKey & "])" & vbCrLf & _
          ");"
    Call m_Db.ExecuteNonQuery(sql)
    '遍歷新增日期表
    Dim rs As DAO.Recordset
    Set rs = m_Db.Database.OpenRecordset(tbl.name, dbOpenDynaset)
    '將 currentDate 設為 dateStart 的前一年，擴充可查詢之日期範圍
    Dim currentDate As Date
    currentDate = DateSerial(Year(dateStart) - 1, Month(dateStart), Day(dateStart))
    '逐日產生日期記錄，直到期間結束日
    Do While currentDate <= dateEnd
        Dim dow As Integer
        dow = Weekday(currentDate, vbSunday)
        '新增記錄並填入欄位值
        rs.AddNew
        rs(tbl.DateKey).value = currentDate
        rs(tbl.Year).value = Year(currentDate)
        rs(tbl.Month).value = Month(currentDate)
        rs(tbl.Day).value = Day(currentDate)
        rs(tbl.DayOfWeek).value = dow
        rs(tbl.IsWeekend).value = (dow = vbSunday Or dow = vbSaturday)
        rs(tbl.IsHolidays).value = False
        rs(tbl.IsMakeupDays).value = False
        rs(tbl.HolidaysDesc).value = vbNullString
        rs(tbl.MakeupDaysDesc).value = vbNullString
        rs.Update
        '遞增下一日
        currentDate = DateAdd("d", 1, currentDate)
    Loop
    Debug.Print "已建立日期維度表 (範圍: " & dateStart & " ~ " & dateEnd & ")"
    rs.Close
    Set rs = Nothing
End Sub
