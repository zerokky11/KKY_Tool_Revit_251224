Option Explicit On
Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.IO
Imports System.Linq
Imports System.Windows.Forms
Imports Autodesk.Revit.UI
Imports KKY_Tool_Revit.Services

Namespace UI.Hub

    Partial Public Class UiBridgeExternalEvent

        Private _guidSummary As DataTable = Nothing
        Private _guidDetail As DataTable = Nothing
        Private _guidMode As Integer = 1

        ' -----------------------------
        ' 핸들러
        ' -----------------------------
        Private Sub HandleGuidAddFiles(app As UIApplication, payload As Object)
            Dim pd = ParsePayloadDict(payload)
            Dim pick As String = ""
            Try
                If pd IsNot Nothing AndAlso pd.ContainsKey("pick") Then
                    pick = Convert.ToString(pd("pick"))
                End If
            Catch
                pick = ""
            End Try

            If String.Equals(pick, "folder", StringComparison.OrdinalIgnoreCase) Then
                HandleGuidAddFolder()
                Return
            End If

            Using dlg As New OpenFileDialog()
                dlg.Filter = "Revit Project (*.rvt)|*.rvt"
                dlg.Multiselect = True
                dlg.Title = "검토할 RVT 파일 선택"
                dlg.RestoreDirectory = True
                If dlg.ShowDialog() <> DialogResult.OK Then Return

                Dim files As New List(Of String)()
                For Each p In dlg.FileNames
                    If Not String.IsNullOrWhiteSpace(p) Then files.Add(p)
                Next
                SendToWeb("guid:files", New With {.paths = files})
            End Using
        End Sub

        Private Sub HandleGuidRun(app As UIApplication, payload As Object)
            Dim pd = ParsePayloadDict(payload)
            Dim mode As Integer = SafeIntObj(GetProp(pd, "mode"), 1)
            If mode <> 1 AndAlso mode <> 2 Then mode = 1
            Dim rvtPaths = ParseStringList(pd, "rvtPaths")

            Try
                _guidSummary = Nothing
                _guidDetail = Nothing
                _guidMode = mode

                Dim res = GuidAuditService.Run(app, mode, rvtPaths, AddressOf ReportGuidProgress)
                _guidSummary = res.Summary
                _guidDetail = res.Detail

                Dim payloadSummary = ShapeTable(res.Summary, Nothing)
                Dim payloadDetail As Object = Nothing
                If mode = 2 AndAlso res.Detail IsNot Nothing Then
                    payloadDetail = ShapeTable(res.Detail, Nothing)
                End If

                SendToWeb("guid:done", New With {
                    .mode = mode,
                    .summary = payloadSummary,
                    .detail = payloadDetail
                })
            Catch ex As Exception
                SendToWeb("guid:error", New With {.message = ex.Message})
            Finally
                ReportGuidProgress(0, String.Empty)
            End Try
        End Sub

        Private Sub HandleGuidExport(app As UIApplication, payload As Object)
            Dim which As String = ""
            Try
                which = Convert.ToString(GetProp(payload, "which"))
            Catch
                which = ""
            End Try
            which = If(which, "").ToLowerInvariant()
            Dim excelMode As String = "fast"
            Try
                Dim em = Convert.ToString(GetProp(payload, "excelMode"))
                If Not String.IsNullOrWhiteSpace(em) Then excelMode = em
            Catch
            End Try

            Dim target As DataTable = Nothing
            Dim sheet As String = "Result"

            If which = "detail" Then
                If _guidMode <> 2 OrElse _guidDetail Is Nothing OrElse _guidDetail.Rows.Count = 0 Then
                    SendToWeb("guid:error", New With {.message = "저장할 상세 결과가 없습니다."})
                    Return
                End If

                ' Excel에는 RvtPath 제외
                target = CloneWithoutColumn(_guidDetail, "RvtPath")
                sheet = "FamilyParamDetail"
            Else
                If _guidSummary Is Nothing OrElse _guidSummary.Rows.Count = 0 Then
                    SendToWeb("guid:error", New With {.message = "저장할 결과가 없습니다."})
                    Return
                End If
                target = _guidSummary
                sheet = If(_guidMode = 1, "ProjectParams", "FamilySharedParams")
            End If

            Try
                Dim requestedAutoFit As Boolean = String.Equals(excelMode, "normal", StringComparison.OrdinalIgnoreCase)
                LogAutoFitDecision(requestedAutoFit, "GuidAuditExport")
                Dim saved = GuidAuditService.Export(target, sheet, excelMode, "guid:progress")
                If String.IsNullOrWhiteSpace(saved) Then
                    SendToWeb("guid:error", New With {.message = "엑셀 저장이 취소되었습니다."})
                    Return
                End If
                SendToWeb("guid:exported", New With {.path = saved, .which = which})
            Catch ex As Exception
                SendToWeb("guid:error", New With {.message = "엑셀 저장 실패: " & ex.Message})
            End Try
        End Sub

        ' -----------------------------
        ' 유틸
        ' -----------------------------
        Private Sub ReportGuidProgress(pct As Integer, text As String)
            SendToWeb("guid:progress", New With {.pct = pct, .text = text})
        End Sub

        Private Function ShapeTable(dt As DataTable, skipCols As HashSet(Of String)) As Object
            If dt Is Nothing Then Return New With {.columns = New List(Of String)(), .rows = New List(Of Object())()}

            Dim cols As New List(Of String)()
            For Each c As DataColumn In dt.Columns
                If skipCols IsNot Nothing AndAlso skipCols.Contains(c.ColumnName) Then Continue For
                cols.Add(c.ColumnName)
            Next

            Dim rows As New List(Of Object())()
            For Each r As DataRow In dt.Rows
                Dim arr(cols.Count - 1) As Object
                For i As Integer = 0 To cols.Count - 1
                    arr(i) = SafeStrGuid(r(cols(i)))
                Next
                rows.Add(arr)
            Next

            Return New With {.columns = cols, .rows = rows}
        End Function

        Private Function CloneWithoutColumn(dt As DataTable, columnName As String) As DataTable
            If dt Is Nothing Then Return Nothing
            Dim clone As DataTable = dt.Clone()
            If clone.Columns.Contains(columnName) Then clone.Columns.Remove(columnName)
            For Each r As DataRow In dt.Rows
                Dim nr = clone.NewRow()
                For Each c As DataColumn In clone.Columns
                    nr(c.ColumnName) = r(c.ColumnName)
                Next
                clone.Rows.Add(nr)
            Next
            Return clone
        End Function

        Private Shared Function SafeStrGuid(o As Object) As String
            If o Is Nothing OrElse o Is DBNull.Value Then Return String.Empty
            Return Convert.ToString(o)
        End Function

        Private Sub HandleGuidAddFolder()
            Using dlg As New FolderBrowserDialog()
                dlg.Description = "RVT 폴더 선택"
                dlg.ShowNewFolderButton = False

                If dlg.ShowDialog() <> DialogResult.OK Then
                    Return
                End If

                Dim root As String = dlg.SelectedPath
                Dim files As New List(Of String)()
                Const MaxFiles As Integer = 2000

                Try
                    If Directory.Exists(root) Then
                        Dim found = Directory.EnumerateFiles(root, "*.rvt", SearchOption.TopDirectoryOnly).
                            Select(Function(p) New With {.Path = p, .Name = TryCast(Path.GetFileName(p), String)}).
                            OrderBy(Function(x) x.Name, StringComparer.OrdinalIgnoreCase).
                            ToList()

                        For Each item In found
                            Dim fp As String = item.Path
                            If String.IsNullOrWhiteSpace(fp) Then Continue For
                            If files.Count >= MaxFiles Then Exit For
                            files.Add(fp)
                        Next

                        If found.Count > MaxFiles Then
                            SendToWeb("guid:warn", New With {.message = $"RVT 파일이 {found.Count:#,0}개 있습니다. 상위 {MaxFiles:#,0}개만 추가합니다."})
                        End If
                    End If
                Catch ex As Exception
                    SendToWeb("guid:warn", New With {.message = $"폴더를 읽는 중 오류가 발생했습니다: {ex.Message}"})
                End Try

                If files.Count = 0 Then
                    SendToWeb("guid:warn", New With {.message = "선택한 폴더에 RVT 파일이 없습니다."})
                    Return
                End If

                Dim unique As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                Dim deduped As New List(Of String)()
                For Each f In files
                    If unique.Add(f) Then deduped.Add(f)
                Next

                SendToWeb("guid:files", New With {.paths = deduped})
            End Using
        End Sub

    End Class

End Namespace
