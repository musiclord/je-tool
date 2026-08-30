Attribute VB_Name = "Util"
Option Explicit
'===============================================================================
' Layer:    Shared Utility
' Purpose:  保留 1120 服務使用的 SQL 輔助函式，並提供唯一啟動入口。
'===============================================================================

Public Const TBL_TEMP As String = "TEMP_DATA"

Public Sub Launch()
    App.Main
End Sub

Public Function Nz( _
    ByVal fieldName As String, _
    Optional ByVal defaultValue As String = "0" _
) As String
    fieldName = Trim$(fieldName)
    fieldName = "[" & fieldName & "]"
    Nz = "IIF(ISNULL(" & fieldName & ")," & defaultValue & "," & fieldName & ")"
End Function

Public Function SanitizeNumericField(ByVal fieldName As String) As String
    SanitizeNumericField = _
        "CDbl(IIf(" & vbCrLf & _
        "    [" & fieldName & "] IS NULL " & vbCrLf & _
        "        OR Trim([" & fieldName & "]) = '' " & vbCrLf & _
        "        OR Trim([" & fieldName & "]) = '-', " & vbCrLf & _
        "    0, [" & fieldName & "]))"
End Function

Public Function CheckDate(ByVal value As Variant) As Boolean
    CheckDate = IsDate(value)
End Function

Public Function CheckDouble(ByVal value As Variant) As Boolean
    CheckDouble = IsNumeric(value)
End Function

Public Function CheckText(ByVal value As Variant) As Boolean
    CheckText = (VarType(value) = vbString)
End Function
