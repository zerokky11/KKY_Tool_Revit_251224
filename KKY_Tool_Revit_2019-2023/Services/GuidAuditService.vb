Option Explicit On
Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports Autodesk.Revit.DB
Imports Autodesk.Revit.UI
Imports KKY_Tool_Revit.Infrastructure
Imports RvtDB = Autodesk.Revit.DB

Namespace Services

    ''' <summary>
    ''' GUID Audit 기능 포팅(Service 계층)
    '''  - 프로젝트 파라미터 vs 공유 파라미터 파일 GUID 비교 (항상 수행)
    '''  - 옵션: 로드 패밀리 파라미터 vs 공유 파라미터 파일 GUID 비교
    ''' </summary>
    Public NotInheritable Class GuidAuditService

        Private Sub New()
        End Sub

        Public Class RunResult
            Public Property IncludeFamily As Boolean
            Public Property Project As DataTable
            Public Property FamilyIndex As List(Of GuidFamilyIndexItem)
            Public Property FamilyLookup As Dictionary(Of String, DataTable)
        End Class

        Private Class TargetFile
            Public Property Path As String = String.Empty
            Public Property Name As String = String.Empty
        End Class

        ''' <summary>
        ''' GUID Audit 실행
        ''' </summary>
        Public Shared Function Run(app As UIApplication,
                                   includeFamily As Boolean,
                                   rvtPaths As IEnumerable(Of String),
                                   progress As Action(Of Integer, String),
                                   Optional warn As Action(Of String) = Nothing) As RunResult

            If app Is Nothing Then Throw New ArgumentNullException(NameOf(app))

            Dim defMap = SharedParamReader.ReadSharedParamNameGuidMap(app.Application)
            If defMap Is Nothing OrElse defMap.Count = 0 Then
                Throw New InvalidOperationException("공유 파라미터 파일이 설정되어 있지 않거나 읽을 수 없습니다. (Revit 옵션에서 Shared Parameter 파일 경로 확인)")
            End If

            Dim targets = BuildTargets(app, rvtPaths)
            If targets.Count = 0 Then
                Throw New InvalidOperationException("검토할 RVT 파일이 없습니다.")
            End If

            Dim total As Integer = targets.Count
            Dim projectTable As DataTable = Nothing
            Dim familyLookup As Dictionary(Of String, DataTable) = Nothing
            Dim familyIndex As List(Of GuidFamilyIndexItem) = Nothing

            For i As Integer = 0 To total - 1
                Dim target = targets(i)
                Dim openedByMe As Boolean = False
                Dim doc As Document = Nothing
                Dim openError As String = ""

                Try
                    ReportProgress(progress, total, i + 1, 0.02R, $"문서 여는 중... {i + 1}/{total} {target.Name}")
                    doc = ResolveOrOpenDocument(app, app.ActiveUIDocument?.Document, target.Path, openedByMe, openError)

                    If doc Is Nothing Then
                        Dim fail = Auditors.MakeProjectTable()
                        Dim note = BuildOpenFailNotes(openError, target.Path)
                        Dim shortReason = ShortenReason(note)
                        If warn IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(note) Then
                            warn(note)
                        End If
                        ReportProgress(progress, total, i + 1, 0.08R, $"문서 열기 실패: {target.Name} - {shortReason}")
                        Auditors.AddProjectRow(fail, target.Name, target.Path, "", "OPEN_FAIL", note, "Project")
                        projectTable = MergeTable(projectTable, fail)
                        Continue For
                    End If

                Dim rvtName As String = GetRvtName(doc, target.Path)
                Dim captureIndex As Integer = i
                Dim captureName As String = rvtName

                Dim proj = Auditors.RunProjectParameterAudit(doc, defMap, rvtName, target.Path,
                                                             Function(cur, tot) As Object
                                                                 Dim frac As Double = 0.1R + 0.8R * SafeRatio(cur, tot)
                                                                 ReportProgress(progress, total, captureIndex + 1, frac, $"[{captureName}] 프로젝트 파라미터 ({cur}/{tot})")
                                                                 Return Nothing
                                                             End Function)
                projectTable = MergeTable(projectTable, proj)

                If includeFamily Then
                    Dim famPack = Auditors.RunFamilyAudit(doc, defMap, rvtName, target.Path,
                                                          Function(cur, tot, famName) As Object
                                                              Dim frac As Double = 0.1R + 0.8R * SafeRatio(cur, tot)
                                                              ReportProgress(progress, total, captureIndex + 1, frac, $"[{captureName}] 패밀리 처리 중 ({cur}/{tot}) {famName}")
                                                              Return Nothing
                                                          End Function)
                    If famPack IsNot Nothing Then
                        familyLookup = MergeFamilyLookup(familyLookup, famPack.DetailByFamily)
                        familyIndex = MergeFamilyIndex(familyIndex, famPack.Index)
                    End If
                End If

                ReportProgress(progress, total, captureIndex + 1, 1.0R, $"완료: {captureIndex + 1}/{total} {captureName}")

                Catch ex As Exception
                    Dim fail = Auditors.MakeProjectTable()
                    Dim note = BuildExceptionNotes(ex, target.Path)
                    ReportProgress(progress, total, i + 1, 0.08R, $"문서 처리 실패: {target.Name} - {ShortenReason(note)}")
                    Auditors.AddProjectRow(fail, target.Name, target.Path, "", "ERROR", note, "Project")
                    projectTable = MergeTable(projectTable, fail)

                Finally
                    If openedByMe AndAlso doc IsNot Nothing Then
                        Try
                            doc.Close(False)
                        Catch
                        End Try
                    End If
                End Try
            Next

            Dim res As New RunResult() With {
                .IncludeFamily = includeFamily,
                .Project = If(projectTable, Auditors.MakeProjectTable()),
                .FamilyIndex = If(includeFamily, familyIndex, Nothing),
                .FamilyLookup = If(includeFamily, familyLookup, Nothing)
            }
            Return res
        End Function

        ''' <summary>엑셀 내보내기 (단일 워크북 2시트)</summary>
        Public Shared Function Export(projectTable As DataTable,
                                      familyLookup As Dictionary(Of String, DataTable),
                                      includeFamily As Boolean,
                                      Optional excelMode As String = "fast",
                                      Optional progressChannel As String = Nothing) As String
            If projectTable Is Nothing OrElse projectTable.Rows.Count = 0 Then Return String.Empty

            Dim totalRows As Integer = projectTable.Rows.Count
            If includeFamily AndAlso familyLookup IsNot Nothing Then
                For Each kv In familyLookup
                    If kv.Value IsNot Nothing Then totalRows += kv.Value.Rows.Count
                Next
            End If

            Dim doAutoFit As Boolean = False
            Try
                If String.Equals(excelMode, "normal", StringComparison.OrdinalIgnoreCase) AndAlso totalRows <= 30000 Then
                    doAutoFit = True
                End If
            Catch
                doAutoFit = False
            End Try

            Dim tables As New List(Of Tuple(Of String, DataTable))()
            tables.Add(Tuple.Create("RVT 검토결과", CloneWithoutColumn(projectTable, "RvtPath")))
            If includeFamily AndAlso familyLookup IsNot Nothing Then
                Dim famTable As DataTable = Nothing
                For Each kv In familyLookup
                    If kv.Value Is Nothing Then Continue For
                    famTable = MergeTable(famTable, kv.Value)
                Next
                If famTable IsNot Nothing AndAlso famTable.Rows.Count > 0 Then
                    tables.Add(Tuple.Create("Family(RFA) Parameter", CloneWithoutColumn(famTable, "RvtPath")))
                End If
            End If

            Dim defaultFileName As String = $"GUID_Audit_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            Return ExcelCore.PickAndSaveXlsx(tables, defaultFileName, doAutoFit, progressChannel)
        End Function

        Private Shared Function CloneWithoutColumn(dt As DataTable, columnName As String) As DataTable
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

        Private Shared Function BuildTargets(app As UIApplication, rvtPaths As IEnumerable(Of String)) As List(Of TargetFile)
            Dim list As New List(Of TargetFile)()
            Dim dedup As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            Dim requested As IEnumerable(Of String) = If(rvtPaths, Enumerable.Empty(Of String)())
            For Each p In requested
                If String.IsNullOrWhiteSpace(p) Then Continue For
                Dim full As String = p
                Try
                    If System.IO.Path.IsPathRooted(p) Then
                        full = System.IO.Path.GetFullPath(p)
                    Else
                        full = p.Trim()
                    End If
                Catch
                    full = p
                End Try
                If dedup.Add(full) Then
                    list.Add(New TargetFile() With {.Path = full, .Name = SafeFileName(full)})
                End If
            Next

            If list.Count = 0 Then
                Dim ap As String = ""
                Try : ap = app.ActiveUIDocument?.Document?.PathName : Catch : ap = "" : End Try
                list.Add(New TargetFile() With {.Path = ap, .Name = SafeFileName(ap)})
            End If

            Return list
        End Function

        Private Shared Function SafeFileName(p As String) As String
            If String.IsNullOrWhiteSpace(p) Then Return "(Active/Unsaved)"
            Try
                Return System.IO.Path.GetFileName(p)
            Catch
                Return p
            End Try
        End Function

        Private Shared Function GetRvtName(doc As Document, path As String) As String
            If Not String.IsNullOrWhiteSpace(path) Then
                Try
                    Return System.IO.Path.GetFileName(path)
                Catch
                End Try
            End If
            Try
                Return doc.Title
            Catch
                Return "(Doc)"
            End Try
        End Function

        Private Shared Function MergeTable(master As DataTable, part As DataTable) As DataTable
            If part Is Nothing Then Return master
            If master Is Nothing Then master = part.Clone()
            For Each r As DataRow In part.Rows
                master.ImportRow(r)
            Next
            Return master
        End Function

        Private Shared Function MergeFamilyLookup(master As Dictionary(Of String, DataTable),
                                                  incoming As Dictionary(Of String, DataTable)) As Dictionary(Of String, DataTable)
            If incoming Is Nothing OrElse incoming.Count = 0 Then Return master
            If master Is Nothing Then master = New Dictionary(Of String, DataTable)(StringComparer.OrdinalIgnoreCase)
            For Each kv In incoming
                Dim table = kv.Value
                If table Is Nothing OrElse table.Rows.Count = 0 Then Continue For
                If master.ContainsKey(kv.Key) Then
                    master(kv.Key) = MergeTable(master(kv.Key), table)
                Else
                    master(kv.Key) = table
                End If
            Next
            Return master
        End Function

        Private Shared Function MergeFamilyIndex(master As List(Of GuidFamilyIndexItem),
                                                 incoming As List(Of GuidFamilyIndexItem)) As List(Of GuidFamilyIndexItem)
            If incoming Is Nothing OrElse incoming.Count = 0 Then Return master
            If master Is Nothing Then master = New List(Of GuidFamilyIndexItem)()
            For Each item In incoming
                master.Add(item)
            Next
            Return master
        End Function

        Private Shared Function SafeRatio(cur As Integer, tot As Integer) As Double
            If tot <= 0 Then Return 0
            Return Math.Max(0, Math.Min(1.0R, CDbl(cur) / CDbl(tot)))
        End Function

        Private Shared Sub ReportProgress(cb As Action(Of Integer, String),
                                          totalFiles As Integer,
                                          fileIndex As Integer,
                                          docProgress As Double,
                                          text As String)
            If cb Is Nothing Then Return
            Dim safeTotal As Integer = Math.Max(1, totalFiles)
            Dim idx As Integer = Math.Max(0, fileIndex - 1)
            Dim ratio As Double = (idx + Math.Max(0.0R, Math.Min(1.0R, docProgress))) / safeTotal
            Dim pct As Integer = CInt(Math.Max(0, Math.Min(100, Math.Round(ratio * 100.0R))))
            cb(pct, text)
        End Sub

        Private Shared Function BuildOpenFailNotes(reason As String, inputPath As String) As String
            Dim trimmed = If(reason, "").Trim()
            Dim hasPathInReason As Boolean = False
            Try
                hasPathInReason = Not String.IsNullOrWhiteSpace(inputPath) AndAlso
                                  trimmed.IndexOf(inputPath, StringComparison.OrdinalIgnoreCase) >= 0
            Catch
                hasPathInReason = False
            End Try
            Dim pathPart = If(String.IsNullOrWhiteSpace(inputPath) OrElse hasPathInReason, "", $" [Path: {inputPath}]")
            If String.IsNullOrWhiteSpace(trimmed) Then
                Return $"문서 열기 실패{pathPart}"
            End If
            Return $"{trimmed}{pathPart}"
        End Function

        Private Shared Function BuildExceptionNotes(ex As Exception, inputPath As String) As String
            If ex Is Nothing Then Return BuildOpenFailNotes(String.Empty, inputPath)

            Dim hrPart As String = ""
            Try
                hrPart = $" (0x{ex.HResult:X8})"
            Catch
                hrPart = ""
            End Try

            Return BuildOpenFailNotes($"{ex.Message}{hrPart}", inputPath)
        End Function

        Private Shared Function ShortenReason(reason As String) As String
            If String.IsNullOrWhiteSpace(reason) Then Return String.Empty
            Dim firstLine As String = reason.Replace(ControlChars.Cr, " ").Replace(ControlChars.Lf, " ").Trim()
            If firstLine.Length > 120 Then
                Return firstLine.Substring(0, 117) & "..."
            End If
            Return firstLine
        End Function

        '=========================================================
        ' Central(Workshared) => Detach + CloseAllWorksets
        '=========================================================
        Private Shared Function ResolveOrOpenDocument(uiApp As UIApplication, activeDoc As Document, path As String, ByRef openedByMe As Boolean, ByRef failureReason As String) As Document
            openedByMe = False
            failureReason = String.Empty

            Dim requested As String = If(path, "").Trim()

            Dim isRooted As Boolean = False
            Try
                isRooted = System.IO.Path.IsPathRooted(requested)
            Catch
                isRooted = False
            End Try

            Dim allowNameMatch As Boolean = (Not isRooted) AndAlso requested.IndexOf(":"c) = -1 AndAlso requested.IndexOf("\"c) = -1

            If String.IsNullOrWhiteSpace(requested) Then
                Return activeDoc
            End If

            If IsMatchingDoc(activeDoc, requested, allowNameMatch) Then
                Return activeDoc
            End If

            Dim opened = FindOpenDocument(uiApp, requested, allowNameMatch)
            If opened IsNot Nothing Then Return opened

            If allowNameMatch Then
                failureReason = $"Invalid path: {requested}"
                Return Nothing
            End If

            If Not isRooted Then
                failureReason = $"Invalid path: {requested}"
                Return Nothing
            End If

            Try
                If System.IO.Path.IsPathRooted(requested) AndAlso Not File.Exists(requested) Then
                    failureReason = $"File not found: {requested}"
                    Return Nothing
                End If
            Catch ex As Exception
                failureReason = BuildExceptionNotes(ex, requested)
                Return Nothing
            End Try

            Dim mp As ModelPath = Nothing
            Try
                mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(requested)
            Catch ex As Exception
                failureReason = BuildExceptionNotes(ex, requested)
                mp = Nothing
            End Try
            If mp Is Nothing Then
                If String.IsNullOrWhiteSpace(failureReason) Then failureReason = $"경로 변환 실패 [Path: {requested}]"
                Return Nothing
            End If

            Dim preferDetach As Boolean = False
            Try
                Dim bfi = BasicFileInfo.Extract(requested)
                If bfi Is Nothing Then
                    preferDetach = True
                ElseIf bfi.IsWorkshared Then
                    preferDetach = True
                End If
            Catch
                preferDetach = True
            End Try

            Dim attempts As New List(Of OpenOptions)()
            If preferDetach Then attempts.Add(CreateDetachOptions())
            attempts.Add(New OpenOptions())

            Dim app = uiApp.Application
            For Each opt In attempts
                Try
                    Dim d = app.OpenDocumentFile(mp, opt)
                    openedByMe = True
                    failureReason = String.Empty
                    Return d
                Catch ex As Exception
                    failureReason = BuildExceptionNotes(ex, requested)
                End Try
            Next

            openedByMe = False
            Return Nothing
        End Function

        Private Shared Function CreateDetachOptions() As OpenOptions
            Dim opt As New OpenOptions()
            Try
                opt.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets
                Dim wc As New WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets)
                opt.SetOpenWorksetsConfiguration(wc)
            Catch
            End Try
            Return opt
        End Function

        Private Shared Function FindOpenDocument(uiApp As UIApplication, requested As String, allowNameMatch As Boolean) As Document
            If uiApp Is Nothing Then Return Nothing
            Try
                For Each d As Document In uiApp.Application.Documents
                    If IsMatchingDoc(d, requested, allowNameMatch) Then Return d
                Next
            Catch
            End Try
            Return Nothing
        End Function

        Private Shared Function IsMatchingDoc(doc As Document, requested As String, allowNameMatch As Boolean) As Boolean
            If doc Is Nothing Then Return False

            Dim dp As String = ""
            Try : dp = doc.PathName : Catch : dp = "" : End Try
            If Not String.IsNullOrWhiteSpace(dp) AndAlso String.Equals(dp, requested, StringComparison.OrdinalIgnoreCase) Then
                Return True
            End If

            If allowNameMatch Then
                Dim fileOnly As String = ""
                Try
                    fileOnly = Path.GetFileName(dp)
                Catch
                    fileOnly = ""
                End Try
                If Not String.IsNullOrWhiteSpace(fileOnly) AndAlso String.Equals(fileOnly, requested, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If

                Dim title As String = ""
                Try : title = doc.Title : Catch : title = "" : End Try
                If Not String.IsNullOrWhiteSpace(title) AndAlso String.Equals(title, requested, StringComparison.OrdinalIgnoreCase) Then
                    Return True
                End If
            End If

            Return False
        End Function

        '=========================================================
        ' 내부: Audit 로직 (기존 구현 이동)
        '=========================================================
        Private NotInheritable Class SharedParamReader

            Public Shared Function ReadSharedParamNameGuidMap(app As Autodesk.Revit.ApplicationServices.Application) As Dictionary(Of String, List(Of Guid))
                Dim defFile As DefinitionFile = Nothing
                Try
                    defFile = app.OpenSharedParameterFile()
                Catch
                    defFile = Nothing
                End Try

                If defFile Is Nothing Then Return Nothing

                Dim map As New Dictionary(Of String, List(Of Guid))(StringComparer.OrdinalIgnoreCase)

                For Each grp As DefinitionGroup In defFile.Groups
                    For Each d As Definition In grp.Definitions
                        Dim g As Guid = Guid.Empty
                        If Not TryGetDefinitionGuid(d, g) Then Continue For

                        Dim name = d.Name
                        If Not map.ContainsKey(name) Then map(name) = New List(Of Guid)()
                        map(name).Add(g)
                    Next
                Next

                Return map
            End Function

            Private Shared Function TryGetDefinitionGuid(d As Definition, ByRef g As Guid) As Boolean
                g = Guid.Empty
                If d Is Nothing Then Return False

                Dim t = d.GetType()
                Dim p = t.GetProperty("GUID", BindingFlags.Public Or BindingFlags.Instance)
                If p Is Nothing Then Return False

                Dim v = p.GetValue(d, Nothing)
                If v Is Nothing Then Return False

                If TypeOf v Is Guid Then
                    g = DirectCast(v, Guid)
                    Return g <> Guid.Empty
                End If

                Return False
            End Function

        End Class

        Private NotInheritable Class FamilyAuditPack
            Public Property Summary As DataTable
            Public Property DetailByFamily As Dictionary(Of String, DataTable)
            Public Property Index As List(Of GuidFamilyIndexItem)
        End Class

        Public Class GuidFamilyIndexItem
            Public Property RvtName As String
            Public Property RvtPath As String
            Public Property FamilyName As String
            Public Property FamilyCategory As String
        End Class

        Private NotInheritable Class Auditors

            Public Shared Function MakeProjectTable() As DataTable
                Dim dt As New DataTable("ProjectParams")
                dt.Columns.Add("RvtName", GetType(String))
                dt.Columns.Add("RvtPath", GetType(String))
                dt.Columns.Add("ParamName", GetType(String))
                dt.Columns.Add("ParamKind", GetType(String))
                dt.Columns.Add("RvtGuid", GetType(String))
                dt.Columns.Add("FileGuid", GetType(String))
                dt.Columns.Add("Result", GetType(String))
                dt.Columns.Add("Notes", GetType(String))
                Return dt
            End Function

            Public Shared Function BuildFamilyTable() As DataTable
                Dim dt As New DataTable("FamilyParamDetail")
                dt.Columns.Add("RvtName", GetType(String))
                dt.Columns.Add("RvtPath", GetType(String))
                dt.Columns.Add("FamilyName", GetType(String))
                dt.Columns.Add("FamilyCategory", GetType(String))
                dt.Columns.Add("ParamName", GetType(String))
                dt.Columns.Add("IsShared", GetType(String))
                dt.Columns.Add("FamilyGuid", GetType(String))
                dt.Columns.Add("FileGuid", GetType(String))
                dt.Columns.Add("Result", GetType(String))
                dt.Columns.Add("Notes", GetType(String))
                Return dt
            End Function

            Public Shared Sub AddProjectRow(dt As DataTable, rvtName As String, rvtPath As String, paramName As String, result As String, notes As String, paramKind As String, Optional rvtGuid As String = "", Optional fileGuid As String = "")
                Dim r = dt.NewRow()
                r("RvtName") = If(rvtName, "")
                r("RvtPath") = If(rvtPath, "")
                r("ParamName") = If(paramName, "")
                r("ParamKind") = If(paramKind, "")
                r("RvtGuid") = If(rvtGuid, "")
                r("FileGuid") = If(fileGuid, "")
                r("Result") = If(result, "")
                r("Notes") = If(notes, "")
                dt.Rows.Add(r)
            End Sub

            Public Shared Function RunProjectParameterAudit(doc As Document,
                                                            fileMap As Dictionary(Of String, List(Of Guid)),
                                                            rvtName As String,
                                                            rvtPath As String,
                                                            Optional progress As Action(Of Integer, Integer) = Nothing) As DataTable

                Dim dt As DataTable = MakeProjectTable()

                Dim bindings As BindingMap = doc.ParameterBindings
                Dim iter As DefinitionBindingMapIterator = bindings.ForwardIterator()
                iter.Reset()

                Dim idx As Integer = 0
                Dim total As Integer = 0
                Try
                    While iter.MoveNext()
                        total += 1
                    End While
                Catch
                    total = 0
                End Try

                Try
                    iter.Reset()
                Catch
                End Try

                While True
                    Dim moved As Boolean = False
                    Try
                        moved = iter.MoveNext()
                    Catch
                        Exit While
                    End Try
                    If Not moved Then Exit While

                    idx += 1
                    If progress IsNot Nothing AndAlso (idx = 1 OrElse idx = total OrElse idx Mod 120 = 0) Then
                        progress(idx, Math.Max(1, total))
                    End If

                    Dim def As Definition = Nothing
                    Dim binding As ElementBinding = Nothing
                    Try
                        def = iter.Key
                        binding = TryCast(iter.Current, ElementBinding)
                    Catch
                        def = Nothing
                        binding = Nothing
                    End Try

                    If def Is Nothing Then Continue While

                    Dim name As String = ""
                    Try : name = def.Name : Catch : name = "" : End Try

                    Dim kind As String = "Project"
                    Dim projGuid As String = ""
                    Dim fileGuid As String = ""
                    Dim result As String = ""
                    Dim notes As String = ""

                    Dim isShared As Boolean = TypeOf def Is ExternalDefinition
                    If isShared Then
                        kind = "Shared"
                        Dim gProj As Guid = Guid.Empty
                        Try
                            gProj = DirectCast(def, ExternalDefinition).GUID
                        Catch
                            gProj = Guid.Empty
                        End Try
                        projGuid = If(gProj = Guid.Empty, "", gProj.ToString())

                        Dim fileGuids As List(Of Guid) = Nothing
                        If fileMap IsNot Nothing AndAlso fileMap.TryGetValue(name, fileGuids) Then
                            fileGuid = String.Join("; ", fileGuids.Select(Function(x) x.ToString()).Distinct().ToArray())
                            If fileGuids.Count > 1 Then notes = "동일 이름 GUID 여러 개"

                            If gProj <> Guid.Empty AndAlso fileGuids.Any(Function(x) x = gProj) Then
                                result = "OK"
                            Else
                                result = "MISMATCH"
                            End If
                        Else
                            result = "NOT_FOUND_IN_FILE"
                        End If
                    Else
                        result = "PROJECT_PARAM"
                    End If

                    AddProjectRow(dt, rvtName, rvtPath, name, result, notes, kind, projGuid, fileGuid)
                End While

                Return dt
            End Function

            Public Shared Function RunFamilyAudit(doc As Document,
                                                  fileMap As Dictionary(Of String, List(Of Guid)),
                                                  rvtName As String,
                                                  rvtPath As String,
                                                  Optional progress As Action(Of Integer, Integer, String) = Nothing) As FamilyAuditPack

                Dim pack As New FamilyAuditPack()
                Dim detailLookup As New Dictionary(Of String, DataTable)(StringComparer.OrdinalIgnoreCase)
                Dim index As New List(Of GuidFamilyIndexItem)()

                Dim baseTable As DataTable = Auditors.BuildFamilyTable()

                Dim fams = New FilteredElementCollector(doc).
                    OfClass(GetType(Family)).
                    Cast(Of Family)().
                    OrderBy(Function(x) x.Name, StringComparer.OrdinalIgnoreCase).
                    ToList()

                Dim total As Integer = Math.Max(1, fams.Count)
                Dim idx As Integer = 0

                For Each fam As Family In fams
                    idx += 1

                    If progress IsNot Nothing AndAlso (idx = 1 OrElse idx = total OrElse idx Mod 40 = 0) Then
                        progress(idx, total, fam.Name)
                    End If

                    Dim famName = fam.Name
                    Dim famCat = ""
                    Try
                        If fam.FamilyCategory IsNot Nothing Then famCat = fam.FamilyCategory.Name
                    Catch
                        famCat = ""
                    End Try

                    Dim famDoc As Document = Nothing
                    Try
                        Dim isInPlace As Boolean = False
                        Try
                            isInPlace = fam.IsInPlace
                        Catch
                            isInPlace = False
                        End Try
                        If isInPlace Then Continue For

                        Try
                            famDoc = doc.EditFamily(fam)
                        Catch ex As InvalidOperationException
                            famDoc = Nothing
                            Continue For
                        End Try

                        If famDoc Is Nothing OrElse Not famDoc.IsFamilyDocument Then
                            Continue For
                        End If

                        Dim fm As FamilyManager = famDoc.FamilyManager
                        If fm Is Nothing Then
                            Continue For
                        End If

                        Dim famKey As String = BuildFamilyKey(rvtPath, famName)
                        Dim famTable As DataTable = Nothing
                        If Not detailLookup.TryGetValue(famKey, famTable) Then
                            famTable = baseTable.Clone()
                            detailLookup(famKey) = famTable
                        End If
                        index.Add(New GuidFamilyIndexItem() With {
                            .RvtPath = rvtPath,
                            .RvtName = rvtName,
                            .FamilyName = famName,
                            .FamilyCategory = famCat
                        })

                        For Each fp As FamilyParameter In fm.Parameters
                            If fp Is Nothing Then Continue For

                            Dim pName As String = ""
                            Try : pName = fp.Definition.Name : Catch : pName = "" : End Try

                            Dim isSharedBool As Boolean = False
                            Try : isSharedBool = fp.IsShared : Catch : isSharedBool = False : End Try

                            Dim famGuid As String = ""
                            Dim fileGuid As String = ""
                            Dim res As String = ""
                            Dim notes As String = ""

                            If isSharedBool Then
                                Dim gFam As Guid = Guid.Empty
                                If TryGetFamilyParameterGuid(fp, gFam) Then
                                    famGuid = gFam.ToString()

                                    Dim fileGuids As List(Of Guid) = Nothing
                                    If fileMap.TryGetValue(pName, fileGuids) Then
                                        fileGuid = String.Join("; ", fileGuids.Select(Function(x) x.ToString()).Distinct().ToArray())
                                        If fileGuids.Count > 1 Then notes = "동일 이름 GUID 여러 개"

                                        If fileGuids.Any(Function(x) x = gFam) Then
                                            res = "OK"
                                        Else
                                            res = "MISMATCH"
                                        End If
                                    Else
                                        res = "NOT_FOUND_IN_FILE"
                                    End If
                                Else
                                    res = "NOT_FOUND_IN_FILE"
                                    notes = "FamilyParameter GUID 추출 실패"
                                End If
                            Else
                                res = "FAMILY_PARAM"
                            End If

                            AddDetailRow(famTable, rvtName, rvtPath, famName, famCat, pName,
                                         If(isSharedBool, "Y", "N"),
                                         famGuid, fileGuid, res, notes)
                        Next

                    Catch ex As Exception
                        ' skip system/unopenable families

                    Finally
                        If famDoc IsNot Nothing Then
                            Try
                                famDoc.Close(False)
                            Catch
                            End Try
                        End If
                    End Try
                Next

                pack.Summary = Nothing
                pack.DetailByFamily = detailLookup
                pack.Index = index
                Return pack
            End Function

            Private Shared Sub AddDetailRow(dt As DataTable,
                                            rvtName As String,
                                            rvtPath As String,
                                            famName As String,
                                            famCat As String,
                                            pName As String,
                                            isShared As String,
                                            famGuid As String,
                                            fileGuid As String,
                                            res As String,
                                            notes As String)
                Dim r = dt.NewRow()
                r("RvtName") = If(rvtName, "")
                r("RvtPath") = If(rvtPath, "")
                r("FamilyName") = If(famName, "")
                r("FamilyCategory") = If(famCat, "")
                r("ParamName") = If(pName, "")
                r("IsShared") = If(isShared, "")
                r("FamilyGuid") = If(famGuid, "")
                r("FileGuid") = If(fileGuid, "")
                r("Result") = If(res, "")
                r("Notes") = If(notes, "")
                dt.Rows.Add(r)
            End Sub

            Public Shared Function BuildFamilyIndexItem(rvtName As String, rvtPath As String, famName As String, famCat As String) As GuidFamilyIndexItem
                Return New GuidFamilyIndexItem() With {
                    .RvtName = If(rvtName, ""),
                    .RvtPath = If(rvtPath, ""),
                    .FamilyName = If(famName, ""),
                    .FamilyCategory = If(famCat, "")
                }
            End Function

            Private Shared Function SafeParamElementName(pe As ParameterElement) As String
                Try
                    Return pe.Name
                Catch
                    Return ""
                End Try
            End Function

            Private Shared Function TryGetFamilyParameterGuid(fp As FamilyParameter, ByRef g As Guid) As Boolean
                g = Guid.Empty
                If fp Is Nothing Then Return False

                Dim t = fp.GetType()
                Dim p = t.GetProperty("GUID", BindingFlags.Public Or BindingFlags.Instance)
                If p Is Nothing Then Return False

                Dim v = p.GetValue(fp, Nothing)
                If v Is Nothing Then Return False

                If TypeOf v Is Guid Then
                    g = DirectCast(v, Guid)
                    Return g <> Guid.Empty
                End If

                Return False
            End Function

            Private Shared Function GetParamTypeName(def As Definition) As String
                If def Is Nothing Then
                    Return String.Empty
                End If

                Try
                    Dim p = def.GetType().GetProperty("ParameterType", BindingFlags.Public Or BindingFlags.Instance)
                    If p IsNot Nothing Then
                        Dim v = p.GetValue(def, Nothing)
                        If v IsNot Nothing Then Return v.ToString()
                    End If
                Catch
                End Try

                Try
                    Dim m = def.GetType().GetMethod("GetDataType", BindingFlags.Public Or BindingFlags.Instance)
                    If m IsNot Nothing Then
                        Dim v = m.Invoke(def, Nothing)
                        If v IsNot Nothing Then Return v.ToString()
                    End If
                Catch
                End Try

                Try
                    Dim p2 = def.GetType().GetProperty("DataType", BindingFlags.Public Or BindingFlags.Instance)
                    If p2 IsNot Nothing Then
                        Dim v = p2.GetValue(def, Nothing)
                        If v IsNot Nothing Then Return v.ToString()
                    End If
                Catch
                End Try

                Return String.Empty
            End Function

        End Class

    End Class

End Namespace
        Public Shared Function BuildFamilyKey(rvtPath As String, familyName As String) As String
            Dim pathPart As String = If(rvtPath, "")
            Dim famPart As String = If(familyName, "")
            Return $"{pathPart}|{famPart}"
        End Function
