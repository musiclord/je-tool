VERSION 5.00
Begin {C62A69F0-16DC-11CE-9E98-00AA00574A4F} ViewImportMapTb 
   Caption         =   "處理 TB 欄位映射"
   ClientHeight    =   7020
   ClientLeft      =   105
   ClientTop       =   405
   ClientWidth     =   8520.001
   OleObjectBlob   =   "ViewImportMapTb.frx":0000
   StartUpPosition =   1  '所屬視窗中央
End
Attribute VB_Name = "ViewImportMapTb"
Attribute VB_GlobalNameSpace = False
Attribute VB_Creatable = False
Attribute VB_PredeclaredId = True
Attribute VB_Exposed = False
Option Explicit
'===============================================================================
' Form:     ViewImportMapTb
' Purpose:
' Methods:
'===============================================================================
Public Event ApplyField(ByVal dict As Dictionary, ByVal method As Long)
Private m_Method As Long

Public Sub Initialize(ByRef db As DbAccess)
    Dim fields As Collection
    Set fields = db.GetTableFields("TB")
    Call UpdateFields(fields)
    Call DisableControls
    Call btnMethod1_Click
End Sub

'===============================================================================
'===============================================================================
'--公開方法供外部調用(用於測試)
Private Sub btnTestDefaults_Click()
    '//WARNING: ONLY FOR DEBUGGING
    Call btnMethod3_Click
    Me.AccountName.value = FindField(Me.AccountName, "項目名稱")
    Me.AccountNumber.value = FindField(Me.AccountNumber, "會計項目")
    Me.DebitAmount.value = FindField(Me.DebitAmount, "借方金額")
    Me.CreditAmount.value = FindField(Me.CreditAmount, "貸方金額")
End Sub
Public Sub ApplyTestDefaults()
    '設定測試參數
    Call btnTestDefaults_Click
    '應用測試參數
    Call btnApplyField_Click
End Sub
'===============================================================================
'===============================================================================

Private Sub btnApplyField_Click()
    Dim dict As New Dictionary
    '金額欄位
    dict("ChangeAmount") = Me.ChangeAmount.value
    dict("OpeningBalance") = Me.OpeningBalance.value
    dict("OpeningDebitBalance") = Me.OpeningDebitBalance.value
    dict("OpeningCreditBalance") = Me.OpeningCreditBalance.value
    dict("ClosingBalance") = Me.ClosingBalance.value
    dict("ClosingDebitBalance") = Me.ClosingDebitBalance.value
    dict("ClosingCreditBalance") = Me.ClosingCreditBalance.value
    dict("DebitAmount") = Me.DebitAmount.value
    dict("CreditAmount") = Me.CreditAmount.value
    '必選欄位
    dict("AccountNumber") = Me.AccountNumber.value
    dict("AccountName") = Me.AccountName.value
    '傳回
    RaiseEvent ApplyField(dict, m_Method)
End Sub

Private Sub btnMethod1_Click()
    '設年度變動金額
    Call DisableControls
    Me.lblChangeAmount.ForeColor = RGB(0, 0, 0)
    Me.ChangeAmount.Enabled = True
    m_Method = 1
End Sub

Private Sub btnMethod2_Click()
    '期初期末金額
    Call DisableControls
    Dim n As Variant
    For Each n In Array("OpeningBalance", "ClosingBalance")
        Me.Controls("lbl" & n).ForeColor = RGB(0, 0, 0)
        Me.Controls(n).Enabled = True
    Next n
    m_Method = 2
End Sub

Private Sub btnMethod3_Click()
    '借方貸方金額
    Call DisableControls
    Dim n As Variant
    For Each n In Array("DebitAmount", "CreditAmount")
        Me.Controls("lbl" & n).ForeColor = RGB(0, 0, 0)
        Me.Controls(n).Enabled = True
    Next n
    m_Method = 3
End Sub

Private Sub btnMethod4_Click()
    '借貸之期初期末金額
    Call DisableControls
    Dim n As Variant
    For Each n In Array("OpeningDebitBalance", "ClosingDebitBalance", "OpeningCreditBalance", "ClosingCreditBalance")
        Me.Controls("lbl" & n).ForeColor = RGB(0, 0, 0)
        Me.Controls(n).Enabled = True
    Next n
    m_Method = 4
End Sub

Private Sub btnExit_Click()
    '檢查必填欄位
    Dim errors As Collection
    Set errors = New Collection
    If Trim(Me.AccountNumber.value & "") = "" Then
        errors.Add "請選擇會計科目編號"
    End If
    If Trim(Me.AccountName.value & "") = "" Then
        errors.Add "請選擇會計科目名稱"
    End If
    '顯示錯誤訊息(若有)
    If errors.Count > 0 Then
        Dim errMsg As String
        Dim i As Long
        errMsg = "請修正以下問題:" & vbCrLf & vbCrLf
        For i = 1 To errors.Count
            errMsg = errMsg & i & ". " & errors(i) & vbCrLf
        Next i
        MsgBox errMsg, vbExclamation, "欄位映射失敗"
    End If
    Me.Hide
End Sub

'--自訂方法
Private Sub UpdateFields(ByVal fields As Collection)
    '更新欄位
    Dim ctrl As MSForms.Control
    Dim cbo As MSForms.ComboBox
    Dim i As Long
    If fields Is Nothing Then Exit Sub
    '遍歷控制項
    For Each ctrl In Me.Controls
        If TypeOf ctrl Is MSForms.ComboBox Then
            Set cbo = ctrl
            cbo.Clear
            For i = 1 To fields.Count
                cbo.AddItem fields.item(i)
            Next i
        End If
    Next ctrl
End Sub

Private Sub DisableControls()
    '關閉金額欄位處理之控制項
    Dim ctrls As Variant, n As Variant
    ctrls = Array( _
            "ChangeAmount", "OpeningBalance", "ClosingBalance", _
            "DebitAmount", "OpeningDebitBalance", "ClosingDebitBalance", _
            "CreditAmount", "OpeningCreditBalance", "ClosingCreditBalance")
    For Each n In ctrls
        Me.Controls("lbl" & n).ForeColor = RGB(128, 128, 128)
        Me.Controls(n).Enabled = False
    Next n
End Sub

Private Function FindField(ByVal cbo As MSForms.ComboBox, ByVal keyword As String) As String
    '在 ComboBox 中尋找包含關鍵字的項目
    Dim i As Long
    For i = 0 To cbo.ListCount - 1
        If InStr(1, cbo.List(i), keyword, vbTextCompare) > 0 Then
            FindField = cbo.List(i)
            Exit Function
        End If
    Next i
    '如果找不到，回傳空字串
    FindField = ""
End Function
