Option Explicit On
Option Strict On

Imports System.Data
Imports System.IO
Imports System.Linq
Imports System.Globalization
Imports Autodesk.Revit.DB
Imports Autodesk.Revit.UI
Imports KKY_Tool_Revit.Infrastructure ' ExcelCore 사용




Namespace Services

    Public Class ExportPointsService

        Public Class Row
            Public Property [File] As String
            Public Property ProjectE As Double
            Public Property ProjectN As Double
            Public Property ProjectZ As Double
            Public Property SurveyE As Double
            Public Property SurveyN As Double
            Public Property SurveyZ As Double
            Public Property TrueNorth As Double
        End Class

        Public Shared Function Run(uiapp As UIApplication, files As Object) As IList(Of Row)
            Dim app = uiapp.Application
            Dim list As New List(Of Row)()

            Dim paths As New List(Of String)()
            If TypeOf files Is IEnumerable(Of Object) Then
                For Each o In CType(files, IEnumerable(Of Object))
                    Dim s = TryCast(o, String)
                    If Not String.IsNullOrWhiteSpace(s) AndAlso File.Exists(s) Then paths.Add(s)
                Next
            ElseIf TypeOf files Is String AndAlso File.Exists(CStr(files)) Then
                paths.Add(CStr(files))
            End If
            paths = paths.Distinct().ToList()

            If paths.Count = 0 Then Return list

            For Each p In paths
                Dim doc As Document = Nothing
                Try
                    Dim opt As OpenOptions = BuildOpenOptions(p)
                    Dim mp As ModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(p)
                    doc = app.OpenDocumentFile(mp, opt)

                    Dim row As New Row() With {.File = Path.GetFileName(p)}
                    Extract(doc, row)
                    list.Add(row)
                Catch
                    ' 개별 파일 실패는 무시하고 다음으로 진행
                Finally
                    If doc IsNot Nothing Then
                        Try
                            doc.Close(False)
                        Catch
                        End Try
                    End If
                End Try
            Next
            Return list
        End Function

        Public Shared Function ExportToExcel(uiapp As UIApplication, files As Object, Optional unit As String = "ft") As String
            Dim rows = Run(uiapp, files)

            Dim desktop As String = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            Dim outPath As String = Path.Combine(desktop, $"ExportPoints_{Date.Now:yyyyMMdd_HHmmss}.xlsx")

            Dim normalizedUnit As String = NormalizeUnit(unit)
            Dim headers = BuildHeaders(normalizedUnit)
            Dim data = rows.Select(Function(r) New Object() {
                r.File,
                RoundCoord(ToUnitValue(r.ProjectE, normalizedUnit)),
                RoundCoord(ToUnitValue(r.ProjectN, normalizedUnit)),
                RoundCoord(ToUnitValue(r.ProjectZ, normalizedUnit)),
                RoundCoord(ToUnitValue(r.SurveyE, normalizedUnit)),
                RoundCoord(ToUnitValue(r.SurveyN, normalizedUnit)),
                RoundCoord(ToUnitValue(r.SurveyZ, normalizedUnit)),
                Math.Round(r.TrueNorth, 3)
            })

            Dim dt As DataTable = BuildTable(headers, data)
            ExcelCore.SaveXlsx(outPath, "Points", dt)

            Return outPath
        End Function

        ' ───────────────────────── 내부 유틸 ─────────────────────────

        ''' <summary>
        ''' 헤더/데이터(열 배열) 시퀀스를 DataTable로 변환
        ''' </summary>
        Private Shared Function BuildTable(headers As IEnumerable(Of String),
                                           rows As IEnumerable(Of IEnumerable(Of Object))) As DataTable
            Dim dt As New DataTable("ExportedPoints")

            ' 헤더 보정 및 컬럼 생성
            Dim headArr = If(headers, Enumerable.Empty(Of String)()) _
                          .Select(Function(h, i) If(String.IsNullOrWhiteSpace(h), $"Col{i + 1}", h.Trim())) _
                          .ToArray()
            For Each h In headArr
                dt.Columns.Add(h)
            Next

            ' 데이터 행 추가
            If rows IsNot Nothing Then
                For Each r In rows
                    Dim vals = If(r, Enumerable.Empty(Of Object)()).ToArray()
                    Dim dr = dt.NewRow()
                    For i = 0 To Math.Min(vals.Length, dt.Columns.Count) - 1
                        dr(i) = If(vals(i), String.Empty).ToString()
                    Next
                    dt.Rows.Add(dr)
                Next
            End If

            Return dt
        End Function

        Private Shared Sub Extract(doc As Document, row As Row)
            Dim basePt As BasePoint = New FilteredElementCollector(doc).
                OfClass(GetType(BasePoint)).
                Cast(Of BasePoint)().
                FirstOrDefault(Function(bp) bp.IsShared = False)

            Dim surveyPt As BasePoint = New FilteredElementCollector(doc).
                OfClass(GetType(BasePoint)).
                Cast(Of BasePoint)().
                FirstOrDefault(Function(bp) bp.IsShared = True)

            Dim project As XYZ = If(basePt IsNot Nothing, basePt.Position, XYZ.Zero)
            Dim survey As XYZ = If(surveyPt IsNot Nothing, surveyPt.Position, XYZ.Zero)

            ' 내부(ft) 값을 그대로 유지하여 단위 변환을 나중에 적용
            row.ProjectE = project.X
            row.ProjectN = project.Y
            row.ProjectZ = project.Z

            row.SurveyE = survey.X
            row.SurveyN = survey.Y
            row.SurveyZ = survey.Z

            ' fix2와 동일: LookupParameter("Angle to True North") 우선, 없으면 ProjectLocation
            Dim deg As Double = 0.0
            Try
                If basePt IsNot Nothing Then
                    Dim p = basePt.LookupParameter("Angle to True North")
                    If p IsNot Nothing Then
                        deg = p.AsDouble() * (180.0 / Math.PI)
                    End If
                End If
            Catch
            End Try

            If deg = 0.0 Then
                Try
                    Dim pl As ProjectLocation = doc.ActiveProjectLocation
                    Dim pp As ProjectPosition = pl.GetProjectPosition(XYZ.Zero)
                    If pp IsNot Nothing Then
                        deg = pp.Angle * (180.0 / Math.PI)
                    End If
                Catch
                End Try
            End If

            row.TrueNorth = deg
        End Sub

        Private Shared Function BuildOpenOptions(path As String) As OpenOptions
            Dim info As BasicFileInfo = Nothing
            Try
                info = BasicFileInfo.Extract(path)
            Catch
            End Try

            Dim ws As New WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets)
            Dim opt As New OpenOptions() With {
                .Audit = False,
                .DetachFromCentralOption = If(info IsNot Nothing AndAlso info.IsCentral, DetachFromCentralOption.DetachAndPreserveWorksets, DetachFromCentralOption.DoNotDetach)
            }
            opt.SetOpenWorksetsConfiguration(ws)
            Return opt
        End Function

        Private Shared Function NormalizeUnit(unit As String) As String
            Dim u As String = If(unit, "").Trim().ToLowerInvariant()
            If u = "m" OrElse u = "meter" OrElse u = "meters" Then Return "m"
            If u = "mm" OrElse u = "millimeter" OrElse u = "millimeters" Then Return "mm"
            Return "ft"
        End Function

        Private Shared Function BuildHeaders(unit As String) As String()
            Dim suffix As String = "(ft)"
            If unit = "m" Then
                suffix = "(m)"
            ElseIf unit = "mm" Then
                suffix = "(mm)"
            End If
            Return New String() {
                "File",
                $"ProjectPoint_E{suffix}", $"ProjectPoint_N{suffix}", $"ProjectPoint_Z{suffix}",
                $"SurveyPoint_E{suffix}", $"SurveyPoint_N{suffix}", $"SurveyPoint_Z{suffix}",
                "TrueNorthAngle(deg)"
            }
        End Function

        Private Shared Function ToUnitValue(valueFt As Double, unit As String) As Double
            If unit = "m" Then Return valueFt * 0.3048
            If unit = "mm" Then Return valueFt * 304.8
            Return valueFt
        End Function

        Private Shared Function RoundCoord(v As Double) As Double
            Return Math.Round(v, 4)
        End Function

    End Class

End Namespace
