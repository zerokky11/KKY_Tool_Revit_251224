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

        Private _guidProject As DataTable = Nothing
        Private _guidFamily As DataTable = Nothing
        Private _guidIncludeFamily As Boolean = False

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
            Dim includeFamily As Boolean = False
            Try
                Dim rawInclude = GetProp(pd, "includeFamily")
                If rawInclude IsNot Nothing Then
                    Dim s = Convert.ToString(rawInclude)
                    If Not String.IsNullOrWhiteSpace(s) Then
                        Dim lowered = s.Trim().ToLowerInvariant()
                        includeFamily = (lowered = "true" OrElse lowered = "1" OrElse lowered = "y" OrElse lowered = "yes")
                    Else
                        includeFamily = False
                    End If
                End If
            Catch
                includeFamily = False
            End Try
            If Not includeFamily AndAlso mode = 2 Then includeFamily = True
            Dim rvtPaths = ParseStringList(pd, "rvtPaths")

            Try
                _guidProject = Nothing
                _guidFamily = Nothing
                _guidIncludeFamily = includeFamily

                Dim res = GuidAuditService.Run(app, includeFamily, rvtPaths, AddressOf ReportGuidProgress,
                                               Sub(msg As String)
                                                   If Not String.IsNullOrWhiteSpace(msg) Then
                                                       SendToWeb("guid:warn", New With {.message = msg})
                                                   End If
                                               End Sub)
                _guidProject = res.Project
                _guidFamily = res.Family
                _guidIncludeFamily = res.IncludeFamily

                Dim payloadProject = ShapeTable(_guidProject, Nothing)
                Dim payloadFamily As Object = Nothing
                If _guidIncludeFamily AndAlso _guidFamily IsNot Nothing Then
                    payloadFamily = ShapeTable(_guidFamily, Nothing)
                End If

                SendToWeb("guid:done", New With {
                    .includeFamily = _guidIncludeFamily,
                    .project = payloadProject,
                    .family = payloadFamily
                })
            Catch ex As Exception
                SendToWeb("guid:error", New With {.message = ex.Message})
            Finally
                ReportGuidProgress(0, String.Empty)
            End Try
        End Sub

        Private Sub HandleGuidExport(app As UIApplication, payload As Object)
            Dim excelMode As String = "fast"
            Try
                Dim em = Convert.ToString(GetProp(payload, "excelMode"))
                If Not String.IsNullOrWhiteSpace(em) Then excelMode = em
            Catch
            End Try

            If _guidProject Is Nothing OrElse _guidProject.Rows.Count = 0 Then
                SendToWeb("guid:error", New With {.message = "저장할 결과가 없습니다."})
                Return
            End If

            Try
                Dim requestedAutoFit As Boolean = String.Equals(excelMode, "normal", StringComparison.OrdinalIgnoreCase)
                LogAutoFitDecision(requestedAutoFit, "GuidAuditExport")
                Dim saved = GuidAuditService.Export(_guidProject, _guidFamily, _guidIncludeFamily, excelMode, "guid:progress")
                If String.IsNullOrWhiteSpace(saved) Then
                    SendToWeb("guid:error", New With {.message = "엑셀 내보내기가 취소되었습니다."})
                    Return
                End If
                SendToWeb("guid:exported", New With {.path = saved})
            Catch ex As Exception
                SendToWeb("guid:error", New With {.message = "엑셀 내보내기 실패: " & ex.Message})
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
