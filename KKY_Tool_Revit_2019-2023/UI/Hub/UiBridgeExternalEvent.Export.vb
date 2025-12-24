Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.IO
Imports System.Diagnostics
Imports Autodesk.Revit.UI
Imports NPOI.SS.UserModel
Imports NPOI.XSSF.UserModel

Namespace UI.Hub
    Partial Public Class UiBridgeExternalEvent

        ' ========== Export: 폴더 선택 ==========
        Private Sub HandleExportBrowse()
            Using dlg As New System.Windows.Forms.FolderBrowserDialog()
                Dim r = dlg.ShowDialog()
                If r = System.Windows.Forms.DialogResult.OK Then
                    Dim files As String() = Directory.GetFiles(dlg.SelectedPath, "*.rvt", SearchOption.AllDirectories)
                    _host?.SendToWeb("export:files", New With {.files = files})
                End If
            End Using
        End Sub

        ' ========== Export: 미리보기 ==========
        Private Sub HandleExportPreview(app As UIApplication, payload As Dictionary(Of String, Object))
            Try
                Dim files = ExtractStringList(payload, "files")
                Dim rows = TryCallExportPointsService(app, files)
                If rows Is Nothing Then
                    _host?.SendToWeb("revit:error", New With {.message = "Export Points 서비스가 준비되지 않았습니다."})
                    _host?.SendToWeb("export:previewed", New With {.rows = New List(Of Dictionary(Of String, Object))()})
                    Return
                End If
                rows = rows.Select(Function(r) AdaptExportRow(r)).ToList()
                Export_LastExportRows = rows
                _host?.SendToWeb("export:previewed", New With {.rows = rows})
            Catch ex As Exception
                _host?.SendToWeb("revit:error", New With {.message = "미리보기 실패: " & ex.Message})
                _host?.SendToWeb("export:previewed", New With {.rows = New List(Of Dictionary(Of String, Object))()})
            End Try
        End Sub

        ' ========== Export: 엑셀 저장 ==========
        Private Sub HandleExportSaveExcel(payload As Dictionary(Of String, Object))
            Try
                Dim unit As String = ExtractUnit(payload)
                Dim rows = TryGetRowsFromPayload(payload)
                If rows Is Nothing OrElse rows.Count = 0 Then rows = Export_LastExportRows
                If rows Is Nothing Then rows = New List(Of Dictionary(Of String, Object))()
                Dim dt = BuildExportDataTableFromRows(rows, unit, True)
                Dim todayToken As String = Date.Now.ToString("yyMMdd")
                Dim defaultName As String = $"{todayToken}_좌표 추출 결과.xlsx"
                Dim savePath As String = SaveExcelWithDialog(dt, defaultName)

                If Not String.IsNullOrEmpty(savePath) Then
                    _host?.SendToWeb("export:saved", New With {.path = savePath})
                End If
            Catch ex As Exception
                _host?.SendToWeb("revit:error", New With {.message = "엑셀 저장 실패: " & ex.Message})
            End Try
        End Sub

        ' -------- 서비스 호출/어댑터/테이블 --------
        Private Function TryCallExportPointsService(app As UIApplication, files As List(Of String)) As List(Of Dictionary(Of String, Object))
            Dim names = {"KKY_Tool_Revit.Services.ExportPointsService", "Services.ExportPointsService"}
            For Each n In names
                Dim t = FindType(n, "ExportPointsService")
                If t Is Nothing Then Continue For
                Dim m = t.GetMethod("Run", Reflection.BindingFlags.Public Or Reflection.BindingFlags.Static Or Reflection.BindingFlags.Instance)
                If m Is Nothing Then Continue For
                Dim inst As Object = If(m.IsStatic, Nothing, Activator.CreateInstance(t))
                Dim result = m.Invoke(inst, New Object() {app, files})
                Return AnyToRows(result)
            Next
            Return Nothing
        End Function

        Private Function AdaptExportRow(r As Dictionary(Of String, Object)) As Dictionary(Of String, Object)
            If r Is Nothing Then Return New Dictionary(Of String, Object)(StringComparer.Ordinal)
            ' 컬럼 명세를 고정 (home.js/export.js 스키마와 일치)
            Dim d As New Dictionary(Of String, Object)(StringComparer.Ordinal)
            d("File") = If(r.ContainsKey("File"), r("File"), If(r.ContainsKey("file"), r("file"), ""))
            d("ProjectPoint_E(mm)") = FirstNonEmpty(r, {"ProjectPoint_E(mm)", "ProjectE", "ProjectPoint_E", "ProjectPoint_E_ft"})
            d("ProjectPoint_N(mm)") = FirstNonEmpty(r, {"ProjectPoint_N(mm)", "ProjectN", "ProjectPoint_N", "ProjectPoint_N_ft"})
            d("ProjectPoint_Z(mm)") = FirstNonEmpty(r, {"ProjectPoint_Z(mm)", "ProjectZ", "ProjectPoint_Z", "ProjectPoint_Z_ft"})
            d("SurveyPoint_E(mm)") = FirstNonEmpty(r, {"SurveyPoint_E(mm)", "SurveyE", "SurveyPoint_E", "SurveyPoint_E_ft"})
            d("SurveyPoint_N(mm)") = FirstNonEmpty(r, {"SurveyPoint_N(mm)", "SurveyN", "SurveyPoint_N", "SurveyPoint_N_ft"})
            d("SurveyPoint_Z(mm)") = FirstNonEmpty(r, {"SurveyPoint_Z(mm)", "SurveyZ", "SurveyPoint_Z", "SurveyPoint_Z_ft"})
            d("TrueNorthAngle(deg)") = FirstNonEmpty(r, {"TrueNorthAngle(deg)", "TrueNorth", "TrueNorthAngle", "TrueNorthAngle_deg"})
            Return d
        End Function

        Private Function BuildExportDataTableFromRows(rows As List(Of Dictionary(Of String, Object)), unit As String, applyConversion As Boolean) As DataTable
            Dim normalizedUnit As String = NormalizeUnit(unit)
            Dim suffix As String = "(ft)"
            If normalizedUnit = "m" Then
                suffix = "(m)"
            ElseIf normalizedUnit = "mm" Then
                suffix = "(mm)"
            End If

            Dim dt As New DataTable("Export")
            Dim headers = {
                "File",
                $"ProjectPoint_E{suffix}", $"ProjectPoint_N{suffix}", $"ProjectPoint_Z{suffix}",
                $"SurveyPoint_E{suffix}", $"SurveyPoint_N{suffix}", $"SurveyPoint_Z{suffix}",
                "TrueNorthAngle(deg)"
            }
            For Each h In headers : dt.Columns.Add(h) : Next
            For Each r In rows
                Dim dr = dt.NewRow()
                dr(0) = SafeToString(r, "File")
                dr(1) = FormatCoordForUnit(r, {"ProjectPoint_E(mm)", "ProjectPoint_E(ft)", "ProjectPoint_E(m)", "ProjectE", "ProjectPoint_E"}, normalizedUnit, applyConversion)
                dr(2) = FormatCoordForUnit(r, {"ProjectPoint_N(mm)", "ProjectPoint_N(ft)", "ProjectPoint_N(m)", "ProjectN", "ProjectPoint_N"}, normalizedUnit, applyConversion)
                dr(3) = FormatCoordForUnit(r, {"ProjectPoint_Z(mm)", "ProjectPoint_Z(ft)", "ProjectPoint_Z(m)", "ProjectZ", "ProjectPoint_Z"}, normalizedUnit, applyConversion)
                dr(4) = FormatCoordForUnit(r, {"SurveyPoint_E(mm)", "SurveyPoint_E(ft)", "SurveyPoint_E(m)", "SurveyE", "SurveyPoint_E"}, normalizedUnit, applyConversion)
                dr(5) = FormatCoordForUnit(r, {"SurveyPoint_N(mm)", "SurveyPoint_N(ft)", "SurveyPoint_N(m)", "SurveyN", "SurveyPoint_N"}, normalizedUnit, applyConversion)
                dr(6) = FormatCoordForUnit(r, {"SurveyPoint_Z(mm)", "SurveyPoint_Z(ft)", "SurveyPoint_Z(m)", "SurveyZ", "SurveyPoint_Z"}, normalizedUnit, applyConversion)
                dr(7) = FormatAngleValue(r, "TrueNorthAngle(deg)")
                dt.Rows.Add(dr)
            Next
            Return dt
        End Function

        ' ==================================================================
        ' Export local helpers (self-contained; no cross-module dependency)
        ' ==================================================================

        ' 마지막 미리보기 결과(엑셀 저장 시 payload 없을 때 사용)
        Private Shared Export_LastExportRows As List(Of Dictionary(Of String, Object)) _
            = New List(Of Dictionary(Of String, Object))()

        ' payload에서 string 리스트 추출(e.g., files[])
        Private Shared Function ExtractStringList(payload As Dictionary(Of String, Object), key As String) As List(Of String)
            Dim res As New List(Of String)()
            If payload Is Nothing OrElse Not payload.ContainsKey(key) OrElse payload(key) Is Nothing Then Return res
            Dim v = payload(key)
            Dim arr = TryCast(v, System.Collections.IEnumerable)
            If arr Is Nothing Then
                Dim s As String = TryCast(v, String)
                If Not String.IsNullOrEmpty(s) Then res.Add(s)
                Return res
            End If
            For Each o In arr
                If o Is Nothing Then Continue For
                Dim s = o.ToString()
                If Not String.IsNullOrWhiteSpace(s) Then res.Add(s)
            Next
            Return res
        End Function

        ' 다양한 반환값 → 표준 rows
        Private Shared Function AnyToRows(any As Object) As List(Of Dictionary(Of String, Object))
            Dim result As New List(Of Dictionary(Of String, Object))()
            If any Is Nothing Then Return result

            If TypeOf any Is List(Of Dictionary(Of String, Object)) Then
                Return DirectCast(any, List(Of Dictionary(Of String, Object)))
            End If

            Dim dt As DataTable = TryCast(any, DataTable)
            If dt IsNot Nothing Then
                Return DataTableToRows(dt)
            End If

            Dim ie = TryCast(any, System.Collections.IEnumerable)
            If ie IsNot Nothing Then
                For Each item In ie
                    Dim d As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
                    Dim dict = TryCast(item, System.Collections.IDictionary)
                    If dict IsNot Nothing Then
                        For Each k In dict.Keys
                            d(k.ToString()) = dict(k)
                        Next
                    Else
                        Dim t = item.GetType()
                        For Each p In t.GetProperties()
                            d(p.Name) = p.GetValue(item, Nothing)
                        Next
                    End If
                    result.Add(d)
                Next
            End If
            Return result
        End Function

        ' 행 딕셔너리에서 컬럼값 안전 추출
        Private Shared Function SafeToString(row As Dictionary(Of String, Object), col As String) As String
            If row Is Nothing Then Return String.Empty
            Dim v As Object = Nothing
            If row.TryGetValue(col, v) AndAlso v IsNot Nothing Then
                Return Convert.ToString(v, Globalization.CultureInfo.InvariantCulture)
            End If
            Return String.Empty
        End Function

        Private Shared Function SafeToDouble(row As Dictionary(Of String, Object), cols As IEnumerable(Of String)) As Double?
            If row Is Nothing OrElse cols Is Nothing Then Return Nothing
            For Each k In cols
                Dim s = SafeToString(row, k)
                Dim d As Double
                If Double.TryParse(s, Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture, d) Then
                    Return d
                End If
            Next
            Return Nothing
        End Function

        ' 여러 키 중 첫 번째로 값이 존재하는 항목을 문자열로 반환
        Private Shared Function FirstNonEmpty(row As Dictionary(Of String, Object), keys As IEnumerable(Of String)) As String
            If row Is Nothing OrElse keys Is Nothing Then Return String.Empty
            For Each k In keys
                Dim s = SafeToString(row, k)
                If Not String.IsNullOrEmpty(s) Then Return s
            Next
            Return String.Empty
        End Function

        Private Shared Function NormalizeUnit(unit As String) As String
            Dim u As String = If(unit, "").Trim().ToLowerInvariant()
            If u = "m" OrElse u = "meter" OrElse u = "meters" Then Return "m"
            If u = "mm" OrElse u = "millimeter" OrElse u = "millimeters" Then Return "mm"
            Return "ft"
        End Function

        Private Shared Function UnitFactor(unit As String) As Double
            If unit = "m" Then Return 0.3048
            If unit = "mm" Then Return 304.8
            Return 1.0
        End Function

        Private Shared Function FormatCoordForUnit(row As Dictionary(Of String, Object), keys As IEnumerable(Of String), unit As String, applyConversion As Boolean) As String
            Dim val As Double? = SafeToDouble(row, keys)
            If Not val.HasValue Then Return String.Empty
            Dim v As Double = val.Value
            If applyConversion Then
                v = v * UnitFactor(unit)
            End If
            Return v.ToString("0.####", Globalization.CultureInfo.InvariantCulture)
        End Function

        Private Shared Function FormatAngleValue(row As Dictionary(Of String, Object), key As String) As String
            Dim ang As Double?
            Dim s = SafeToString(row, key)
            Dim d As Double
            If Double.TryParse(s, Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture, d) Then
                ang = d
            End If
            If ang.HasValue Then
                Return ang.Value.ToString("0.###", Globalization.CultureInfo.InvariantCulture)
            End If
            Return s
        End Function

        Private Shared Function ExtractUnit(payload As Dictionary(Of String, Object)) As String
            If payload Is Nothing Then Return "ft"
            Dim v As Object = Nothing
            If payload.TryGetValue("unit", v) AndAlso v IsNot Nothing Then
                Return NormalizeUnit(Convert.ToString(v, Globalization.CultureInfo.InvariantCulture))
            End If
            Return "ft"
        End Function

        ' DataTable → rows
        Private Shared Function DataTableToRows(dt As DataTable) As List(Of Dictionary(Of String, Object))
            Dim list As New List(Of Dictionary(Of String, Object))()
            If dt Is Nothing Then Return list
            For Each r As DataRow In dt.Rows
                Dim d As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
                For Each c As DataColumn In dt.Columns
                    d(c.ColumnName) = If(r.IsNull(c), Nothing, r(c))
                Next
                list.Add(d)
            Next
            Return list
        End Function

        ' 로딩된 어셈블리들에서 타입 찾기(정식명/간단명)
        Private Shared Function FindType(fullOrSimple As String, Optional simpleMatch As String = Nothing) As Type
            ' 직접 시도
            Dim t = Type.GetType(fullOrSimple, False)
            If t IsNot Nothing Then Return t
            ' 로드된 어셈블리 순회
            For Each asm In AppDomain.CurrentDomain.GetAssemblies()
                Try
                    t = asm.GetType(fullOrSimple, False)
                    If t IsNot Nothing Then Return t
                    If Not String.IsNullOrEmpty(simpleMatch) Then
                        For Each ti In asm.GetTypes()
                            If String.Equals(ti.Name, simpleMatch, StringComparison.OrdinalIgnoreCase) Then
                                Return ti
                            End If
                        Next
                    End If
                Catch
                End Try
            Next
            Return Nothing
        End Function

        ' payload에서 rows 추출(있으면) — 없으면 빈 리스트
        Private Shared Function TryGetRowsFromPayload(payload As Dictionary(Of String, Object)) As List(Of Dictionary(Of String, Object))
            If payload Is Nothing Then Return New List(Of Dictionary(Of String, Object))()
            If payload.ContainsKey("rows") AndAlso payload("rows") IsNot Nothing Then
                Return AnyToRows(payload("rows"))
            End If
            If payload.ContainsKey("data") AndAlso payload("data") IsNot Nothing Then
                Return AnyToRows(payload("data"))
            End If
            Dim ie = TryCast(payload, System.Collections.IEnumerable)
            If ie IsNot Nothing Then
                Return AnyToRows(ie)
            End If
            Return New List(Of Dictionary(Of String, Object))()
        End Function

        ' DataTable을 저장 대화상자로 엑셀로 저장하고 경로 반환(취소 시 "")
        Private Shared Function SaveExcelWithDialog(dt As DataTable, Optional defaultName As String = "export.xlsx") As String
            If dt Is Nothing OrElse dt.Columns.Count = 0 Then Return String.Empty
            Dim dlg As New Microsoft.Win32.SaveFileDialog() With {
                .Filter = "Excel (*.xlsx)|*.xlsx",
                .FileName = defaultName
            }
            Dim ok = dlg.ShowDialog()
            If ok <> True Then Return String.Empty
            Dim path = dlg.FileName
            Try
                Dim wb As IWorkbook = New XSSFWorkbook()
                Dim sh = wb.CreateSheet("Export")
                Dim xssf = TryCast(wb, XSSFWorkbook)
                Dim baseStyle As ICellStyle = If(xssf IsNot Nothing, CreateBorderedStyle(xssf), Nothing)
                Dim headerStyle As ICellStyle = If(xssf IsNot Nothing, CreateHeaderStyle(xssf, baseStyle), Nothing)

                ' 헤더
                Dim hr = sh.CreateRow(0)
                For c = 0 To dt.Columns.Count - 1
                    Dim cell = hr.CreateCell(c)
                    cell.SetCellValue(dt.Columns(c).ColumnName)
                    If headerStyle IsNot Nothing Then cell.CellStyle = headerStyle
                Next
                ' 데이터
                Dim rIndex = 1
                For Each dr As DataRow In dt.Rows
                    Dim rr = sh.CreateRow(rIndex) : rIndex += 1
                    For c = 0 To dt.Columns.Count - 1
                        Dim v = If(dr.IsNull(c), "", Convert.ToString(dr(c), Globalization.CultureInfo.InvariantCulture))
                        Dim cell = rr.CreateCell(c)
                        cell.SetCellValue(v)
                        If baseStyle IsNot Nothing Then cell.CellStyle = baseStyle
                    Next
                Next
                ' 자동 너비
                For c = 0 To dt.Columns.Count - 1 : sh.AutoSizeColumn(c) : Next
                Using fs As New FileStream(path, FileMode.Create, FileAccess.Write)
                    wb.Write(fs)
                End Using
                Return path
            Catch ex As Exception
                _host?.SendToWeb("host:error", New With {.message = "엑셀 저장 실패: " & ex.Message})
                Return String.Empty
            End Try
        End Function

    End Class
End Namespace
