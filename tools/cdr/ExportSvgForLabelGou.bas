Attribute VB_Name = "ExportSvgForLabelGou"
'======================================================================
' LabelGou 配套宏：把当前打开的 CorelDRAW 文档批量导成矢量件（优先 SVG）
'
' 为什么要有这个宏：LabelGou 读不懂 .cdr 的矢量对象层（那是专有格式，
' 几何/填充/轮廓的编码没有公开的完整规范），但吃得下 SVG。所以保真底稿
' 请在 CorelDRAW 里「文件 -> 导出」成 SVG，再拿回 LabelGou「从底稿导入」。
' 一份一份点菜单太慢，就靠这个宏批量跑。
'
' 用法：
'   1. 菜单 工具 -> Visual Basic -> Visual Basic 编辑器（或按 Alt+F11）
'   2. 菜单 文件 -> 导入文件，选这个 .bas（或新建模块把内容整段粘进去）
'   3. 回到 CorelDRAW，菜单 工具 -> 宏 -> 运行宏，选 ExportSvgForLabelGou.ExportAll
'   4. 结果在 .cdr 同目录下的 <文档名>_LabelGou导出\ 里，另有一份 导出清单.txt
'
' 重要：本宏没在真机上验证过（开发机没装 CorelDRAW）。如果运行时报
' "编译错误: 常数未定义" 或 "类型不匹配"，见 tools\cdr\README.md 第 3 节
' —— 通常只需要改 FilterName 那几行的滤镜标识写法，其余不用动。
' 手动导出菜单的图文步骤也在 README 里，宏用不了时走手动路径效果一样。
'======================================================================
Option Explicit

' 导出格式候选：从左到右依次尝试，第一个成功的就用它，后面的不再试。
' 这样即使某个版本没有 SVG 导出滤镜（X3 及更早），也能退到 EMF/WMF/PDF。
Private Const FILTER_CHAIN As String = "svg|emf|wmf|pdf"

' 主入口：把所有打开的文档各导一份。只开了一个文档时也走这里。
Public Sub ExportAll()
    Dim doc As Document
    Dim count As Long

    On Error Resume Next
    For Each doc In App.Documents
        ExportOne doc, count
    Next doc
    On Error GoTo 0

    If count = 0 Then
        ' 有些版本的 App.Documents 取不到，退回当前文档再试一次
        Set doc = Nothing
        On Error Resume Next
        Set doc = ActiveDocument
        On Error GoTo 0
        If doc Is Nothing Then
            MsgBox "没有正在打开的文档。请先打开要导出的 .cdr，再运行本宏。", vbExclamation, "LabelGou 导出"
            Exit Sub
        End If
        ExportOne doc, count
    End If

    MsgBox "已导出 " & count & " 份矢量件。" & vbCrLf & _
           "位置在各 .cdr 同目录下的 *_LabelGou导出 文件夹里，" & vbCrLf & _
           "其中有一份 导出清单.txt，写明每份是什么格式、有没有失败。", _
           vbInformation, "LabelGou 导出"
End Sub

' 单个文档：算出输出目录 -> 按滤镜链依次试 -> 结果写进 导出清单.txt
Private Sub ExportOne(ByVal doc As Document, ByRef count As Long)
    Dim base As String
    Dim folder As String
    Dim kinds() As String
    Dim i As Long
    Dim target As String
    Dim done As Boolean
    Dim report As String
    Dim errText As String

    If doc Is Nothing Then Exit Sub

    base = doc.BaseName
    If Len(base) = 0 Then base = "未命名文档"

    folder = doc.FilePath
    If Len(folder) = 0 Then folder = Environ$("TEMP") & "\"   ' 还没存过盘的文档
    If Right$(folder, 1) = "\" Then folder = Left$(folder, Len(folder) - 1)
    folder = folder & "\" & base & "_LabelGou导出"

    On Error Resume Next
    MkDir folder
    On Error GoTo 0

    kinds = Split(FILTER_CHAIN, "|")
    report = "[" & base & "] " & doc.FullFileName & vbCrLf & _
             "  CorelDRAW " & App.Version & "　" & Format$(Now, "yyyy-mm-dd hh:nn") & vbCrLf
    done = False

    For i = LBound(kinds) To UBound(kinds)
        target = folder & "\" & base & "." & kinds(i)

        errText = vbNullString
        On Error Resume Next
        TryExport doc, target, kinds(i)
        If Err.Number <> 0 Then errText = Err.Description
        On Error GoTo 0

        If Len(errText) = 0 Then
            If Dir$(target) <> vbNullString Then
                done = True
                count = count + 1
                report = report & "  成功：" & kinds(i) & " -> " & target & vbCrLf
                Exit For
            End If
            errText = "调用没报错但没写出文件"
        End If
        report = report & "  试过 " & kinds(i) & "：" & errText & vbCrLf
    Next i

    If Not done Then
        report = report & _
                 "  结论：这台机器上的 CorelDRAW 没能用宏导出矢量件。" & vbCrLf & _
                 "  请改用手动「文件 -> 导出 -> 存成 SVG」，或按 README 第 3 节改滤镜标识写法。" & vbCrLf
    End If

    WriteLog folder & "\导出清单.txt", report & vbCrLf
    Debug.Print report
End Sub

' 三种调用形状依次试：不同版本的 ExportEx 参数表不一样，逐个试比赌一个准更省事
Private Sub TryExport(ByVal doc As Document, ByVal fileName As String, ByVal kind As String)
    Dim filter As String
    filter = FilterName(kind)

    ' 形状 A：ExportEx(文件名, 滤镜, 版本) —— X4/X5 常见
    doc.ExportEx fileName, filter, 0
    If Err.Number = 0 Then
        If Dir$(fileName) <> vbNullString Then Exit Sub
        Err.Clear
    End If
    Err.Clear

    ' 形状 B：老式 Export(文件名, 滤镜名) —— 早期版本的写法
    doc.Export fileName, filter
    If Err.Number = 0 Then
        If Dir$(fileName) <> vbNullString Then Exit Sub
        Err.Clear
    End If
    Err.Clear

    ' 形状 C：ExportEx 连颜色模式与分辨率一起给（有的版本要求参数给全）
    doc.ExportEx fileName, filter, 0, 0, 300
End Sub

' 各版本滤镜标识可能不同：这里给字符串名。若你的版本要数值常量或名字不同，
' 按 README 第 3 节改这几行即可（对象浏览器里搜 cdrSVGExportFilter 能看到真名）。
Private Function FilterName(ByVal kind As String) As String
    Select Case LCase$(kind)
        Case "svg" : FilterName = "cdrSVGExportFilter"
        Case "emf" : FilterName = "cdrEMFExportFilter"
        Case "wmf" : FilterName = "cdrWMFExportFilter"
        Case "pdf" : FilterName = "cdrPDFExportFilter"
        Case Else : FilterName = vbNullString
    End Select
End Function

' 清单是追加写的：批量跑多份文档时，一份文件能看完全部结果
Private Sub WriteLog(ByVal path As String, ByVal text As String)
    Dim handle As Integer
    On Error Resume Next
    handle = FreeFile
    Open path For Append As #handle
    Print #handle, text;
    Close #handle
End Sub
