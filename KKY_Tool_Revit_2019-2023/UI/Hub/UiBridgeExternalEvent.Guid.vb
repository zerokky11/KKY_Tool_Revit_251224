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
                LogAutoFitDecision(False, "GuidAuditExport")
                Dim saved = GuidAuditService.Export(target, sheet, excelMode)
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

    End Class

End Namespace
