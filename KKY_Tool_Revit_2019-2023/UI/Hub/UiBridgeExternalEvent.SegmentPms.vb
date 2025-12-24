Option Explicit On
Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Windows.Forms
Imports Autodesk.Revit.UI
Imports KKY_Tool_Revit.Services
Imports NPOI.SS.UserModel
Imports NPOI.XSSF.UserModel

Namespace UI.Hub

    Partial Public Class UiBridgeExternalEvent

        Private _pmsPath As String
        Private _pmsTable As DataTable
        Private _pmsRows As List(Of SegmentPmsCheckService.PmsRow)
        Private _defaultMap As Dictionary(Of String, List(Of String))
        Private _extractData As DataSet
        Private _lastNdRound As Integer = 3

        Private Shared Function PmsFolder() As String
            Dim root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KKY_Tool_Revit", "PMS")
            Directory.CreateDirectory(root)
            Return root
        End Function

        Private Shared Function PmsMetaPath() As String
            Return Path.Combine(PmsFolder(), "pms_meta.txt")
        End Function

        Private Sub SavePmsMeta(path As String, unitPref As String)
            Try
                Dim line = (If(path, String.Empty)).Replace(vbCr, String.Empty).Replace(vbLf, String.Empty)
                Dim u = If(unitPref, String.Empty).Replace(vbCr, String.Empty).Replace(vbLf, String.Empty)
                File.WriteAllText(PmsMetaPath(), line & "|" & u, Encoding.UTF8)
            Catch
            End Try
        End Sub

        Private Sub TryLoadPmsFromMeta()
            If _pmsTable IsNot Nothing Then Return
            Dim meta = PmsMetaPath()
            If Not File.Exists(meta) Then Return
            Try
                Dim txt = File.ReadAllText(meta, Encoding.UTF8)
                Dim parts = txt.Split("|"c)
                Dim path = If(parts.Length > 0, parts(0), String.Empty)
                Dim unitPref = If(parts.Length > 1, parts(1), "mm")
                If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return
                _pmsPath = path
                Dim loaded = SegmentPmsCheckService.LoadPmsExcel(path, unitPref)
                _pmsTable = loaded.Table
                _pmsRows = loaded.Rows
                SendToWeb("segmentpms:pms-registered", New With {.ok = True, .path = path, .options = BuildPmsOptions()})
            Catch
            End Try
        End Sub

        Private Sub HandleSegmentPmsRegister(app As UIApplication, payload As Object)
            Using dlg As New OpenFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.Title = "PMS Excel 선택"
                dlg.RestoreDirectory = True
                If dlg.ShowDialog() <> DialogResult.OK Then Return

                Try
                    Dim destDir = PmsFolder()
                    Dim dest = Path.Combine(destDir, "pms_latest.xlsx")
                    File.Copy(dlg.FileName, dest, True)
                    _pmsPath = dest

                    Dim unitPref As String = TryCast(GetProp(payload, "unit"), String)
                    Dim loaded = SegmentPmsCheckService.LoadPmsExcel(dest, unitPref)
                    _pmsTable = loaded.Table
                    _pmsRows = loaded.Rows
                    SavePmsMeta(dest, unitPref)

                    If loaded.Errors IsNot Nothing AndAlso loaded.Errors.Count > 0 Then
                        SendToWeb("segmentpms:error", New With {.message = String.Join(";", loaded.Errors)})
                    Else
                        SendToWeb("segmentpms:pms-registered", New With {.ok = True, .path = dest, .options = BuildPmsOptions()})
                        SendToWeb("toast:info", New With {.message = "PMS 파일을 등록했습니다."})
                    End If
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Sub HandleSegmentPmsLoadDefault(payload As Object)
            Using dlg As New OpenFileDialog()
                dlg.Filter = "Text (*.txt)|*.txt|All Files (*.*)|*.*"
                dlg.RestoreDirectory = True
                dlg.Title = "기본 매핑 TXT 불러오기"
                If dlg.ShowDialog() <> DialogResult.OK Then Return

                Try
                    Dim lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8)
                    _defaultMap = New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)
                    For Each line In lines
                        Dim ln = line.Trim()
                        If String.IsNullOrEmpty(ln) Then Continue For
                        Dim parts = ln.Split({ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                        If parts.Length < 2 Then parts = ln.Split({","c}, StringSplitOptions.RemoveEmptyEntries)
                        If parts.Length >= 2 Then
                            Dim rev = parts(0).Trim()
                            Dim pms = parts(1).Trim()
                            If Not String.IsNullOrEmpty(rev) AndAlso Not String.IsNullOrEmpty(pms) Then
                                If Not _defaultMap.ContainsKey(rev) Then _defaultMap(rev) = New List(Of String)()
                                If Not _defaultMap(rev).Any(Function(x) String.Equals(x, pms, StringComparison.OrdinalIgnoreCase)) Then _defaultMap(rev).Add(pms)
                            End If
                        End If
                    Next
                    Dim arr = _defaultMap.Select(Function(kv) New With {.revit = kv.Key, .pmsList = kv.Value}).ToList()
                    SendToWeb("segmentpms:defaultmap-loaded", New With {.ok = True, .items = arr})
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Sub HandleSegmentPmsExtract(app As UIApplication, payload As Object)
            TryLoadPmsFromMeta()
            Dim files = ParseStringList(payload, "files")
            Dim ndRound As Integer = 3
            Try
                Dim v = GetProp(payload, "ndRound")
                If v IsNot Nothing Then ndRound = Convert.ToInt32(v)
            Catch
            End Try
            _lastNdRound = ndRound

            If files.Count = 0 Then
                SendToWeb("segmentpms:error", New With {.message = "등록된 RVT 파일이 없습니다. 먼저 파일을 등록하세요."})
                Return
            End If

            Try
                Dim ds = SegmentPmsCheckService.ExtractToDataSet(app, files, ndRound)
                _extractData = ds
                UpdateLastNdRoundFromExtract(ds)
                Dim summary = BuildExtractSummary(ds)
                Dim pipes = BuildPipePayload(ds)
                SendToWeb("segmentpms:extracted", New With {.summary = summary, .pipes = pipes, .pms = BuildPmsOptions()})
            Catch ex As Exception
                SendToWeb("segmentpms:error", New With {.message = ex.Message})
            End Try
        End Sub

        Private Sub HandleSegmentPmsSaveExtract(payload As Object)
            If _extractData Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "추출 데이터가 없습니다."})
                Return
            End If

            Using dlg As New SaveFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.FileName = "SegmentPmsExtract.xlsx"
                dlg.AddExtension = True
                If dlg.ShowDialog() <> DialogResult.OK Then Return

                Try
                    SegmentPmsCheckService.SaveExtractXlsx(_extractData, dlg.FileName)
                    SendToWeb("segmentpms:extract-saved", New With {.path = dlg.FileName})
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Sub HandleSegmentPmsOpenExtract(payload As Object)
            TryLoadPmsFromMeta()
            Using dlg As New OpenFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.RestoreDirectory = True
                dlg.Title = "추출 XLSX 선택"
                If dlg.ShowDialog() <> DialogResult.OK Then Return

                Try
                    Dim ds = SegmentPmsCheckService.LoadExtractXlsx(dlg.FileName)
                    _extractData = ds
                    UpdateLastNdRoundFromExtract(ds)
                    Dim summary = BuildExtractSummary(ds)
                    SendToWeb("segmentpms:extract-opened", New With {.summary = summary, .pipes = BuildPipePayload(ds), .pms = BuildPmsOptions()})
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Sub HandleSegmentPmsPrepare(app As UIApplication, payload As Object)
            HandleSegmentPmsExtract(app, payload)
        End Sub

        Private Sub HandleSegmentPmsRun(app As UIApplication, payload As Object)
            If _extractData Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "추출 데이터를 먼저 준비하세요."})
                Return
            End If
            TryLoadPmsFromMeta()
            If _pmsTable Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "PMS 데이터를 등록하세요."})
                Return
            End If

            Dim ndRound As Integer = _lastNdRound
            Dim tolMm As Double = 0.01R
            Try
                Dim v = GetProp(payload, "ndRound")
                If v IsNot Nothing Then ndRound = Convert.ToInt32(v)
            Catch
            End Try
            Try
                Dim v = GetProp(payload, "tolMm")
                If v IsNot Nothing Then tolMm = Convert.ToDouble(v)
            Catch
            End Try

            Dim mappings As New List(Of SegmentPmsCheckService.MappingRequest)()
            Dim arr = TryCast(GetProp(payload, "maps"), IEnumerable(Of Object))
            If arr IsNot Nothing Then
                Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                For Each o In arr
                    Dim key = SegPmsSafeStr(GetProp(o, "file")) & "|" & SegPmsSafeStr(GetProp(o, "pipeType"))
                    If seen.Contains(key) Then Continue For
                    seen.Add(key)
                    mappings.Add(New SegmentPmsCheckService.MappingRequest With {
                        .File = SegPmsSafeStr(GetProp(o, "file")),
                        .PipeTypeName = SegPmsSafeStr(GetProp(o, "pipeType")),
                        .RuleIndex = SegPmsSafeInt(GetProp(o, "ruleIndex")),
                        .SegmentId = SegPmsSafeInt(GetProp(o, "segmentId")),
                        .SegmentKey = SegPmsSafeStr(GetProp(o, "segmentKey")),
                        .SelectedClass = SegPmsSafeStr(GetProp(o, "cls")),
                        .SelectedPmsSegment = SegPmsSafeStr(GetProp(o, "segment")),
                        .MappingSource = SegPmsSafeStr(GetProp(o, "source"))
                    })
                Next
            End If

            Dim res = SegmentPmsCheckService.RunUsingExtract(_extractData, _pmsTable, mappings, tolMm, ndRound)
            Dim compare = DataTableToObjects(res.CompareTable)
            Dim map = DataTableToObjects(res.MapTable)
            Dim revitRaw = DataTableToObjects(res.RevitSizeTable)
            Dim pmsRaw = DataTableToObjects(res.PmsSizeTable)
            Dim errors = DataTableToObjects(res.ErrorTable)

            SendToWeb("segmentpms:result", New With {
                .map = map,
                .revitRaw = revitRaw,
                .pmsRaw = pmsRaw,
                .compare = compare,
                .errors = errors
            })
        End Sub

        Private Sub HandleSegmentPmsSaveExcel(app As UIApplication, payload As Object)
            If payload Is Nothing Then Return
            Dim compare = TryCast(GetProp(payload, "compare"), IEnumerable(Of Object))
            If compare Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "저장할 결과가 없습니다."})
                Return
            End If

            Dim map = TryCast(GetProp(payload, "map"), IEnumerable(Of Object))
            Dim revitRaw = TryCast(GetProp(payload, "revitRaw"), IEnumerable(Of Object))
            Dim pmsRaw = TryCast(GetProp(payload, "pmsRaw"), IEnumerable(Of Object))
            Dim err = TryCast(GetProp(payload, "errors"), IEnumerable(Of Object))

            Using dlg As New SaveFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.FileName = "SegmentPmsCheck.xlsx"
                dlg.AddExtension = True
                If dlg.ShowDialog() <> DialogResult.OK Then Return

                Try
                    Dim wb As IWorkbook = New XSSFWorkbook()
                    AddSheet(wb, "PipeTypeSegmentMap", map)
                    AddSheet(wb, "SegmentSizeRaw_Revit", revitRaw)
                    AddSheet(wb, "SegmentSizeRaw_PMS", pmsRaw)
                    AddSheet(wb, "SizeCompare", compare)
                    AddSheet(wb, "Error", err)
                    Using fs As New FileStream(dlg.FileName, FileMode.Create, FileAccess.Write)
                        wb.Write(fs)
                    End Using
                    SendToWeb("segmentpms:saved", New With {.path = dlg.FileName})
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Function BuildExtractSummary(ds As DataSet) As String
            If ds Is Nothing Then Return String.Empty
            Dim rules = ds.Tables(SegmentPmsCheckService.TableRules)
            Dim sizes = ds.Tables(SegmentPmsCheckService.TableSizes)
            Dim fileCount As Integer = 0
            Dim pipeCount As Integer = 0
            Dim candCount As Integer = If(rules Is Nothing, 0, rules.Rows.Count)
            If rules IsNot Nothing Then
                Dim fileSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                Dim pipeSet As New HashSet(Of Tuple(Of String, String))(TupleComparer())
                For Each r As DataRow In rules.Rows
                    fileSet.Add(NormalizePath(SegPmsSafeStr(r("File"))))
                    pipeSet.Add(Tuple.Create(SegPmsSafeStr(r("File")), SegPmsSafeStr(r("PipeTypeName"))))
                Next
                fileCount = fileSet.Count
                pipeCount = pipeSet.Count
            End If
            Dim sizeCount As Integer = If(sizes Is Nothing, 0, sizes.Rows.Count)
            Return $"파일 {fileCount}개, PipeType {pipeCount}, Segment 후보 {candCount}, 사이즈 {sizeCount}"
        End Function

        Private Function BuildPipePayload(ds As DataSet) As Object
            If ds Is Nothing OrElse Not ds.Tables.Contains(SegmentPmsCheckService.TableRules) Then Return New Object() {}
            Dim t = ds.Tables(SegmentPmsCheckService.TableRules)
            Dim buckets As New Dictionary(Of Tuple(Of String, String), List(Of Dictionary(Of String, Object)))(TupleComparer())
            For Each r As DataRow In t.Rows
                Dim key = Tuple.Create(SegPmsSafeStr(r("File")), SegPmsSafeStr(r("PipeTypeName")))
                If Not buckets.ContainsKey(key) Then buckets(key) = New List(Of Dictionary(Of String, Object))()
                Dim cand As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
                cand("ruleIndex") = SegPmsSafeInt(r("RuleIndex"))
                cand("segmentId") = SegPmsSafeInt(r("SegmentId"))
                cand("segmentKey") = SegPmsSafeStr(r("SegmentKey"))
                cand("segmentName") = SegPmsSafeStr(r("SegmentKey"))
                buckets(key).Add(cand)
            Next

            Dim list As New List(Of Object)()
            For Each kvp In buckets
                Dim candidates = kvp.Value.OrderBy(Function(x) Convert.ToInt32(x("ruleIndex"))).ToList()
                Dim defaultRule As Integer = 0
                If candidates.Count > 0 Then
                    defaultRule = Convert.ToInt32(candidates(0)("ruleIndex"))
                End If
                Dim defaultList As List(Of String) = Nothing
                If _defaultMap IsNot Nothing AndAlso candidates.Count > 0 Then
                    Dim segKey As String = TryCast(candidates(0)("segmentKey"), String)
                    If Not String.IsNullOrEmpty(segKey) Then _defaultMap.TryGetValue(segKey, defaultList)
                End If
                list.Add(New With {
                    .file = kvp.Key.Item1,
                    .pipeType = kvp.Key.Item2,
                    .candidates = candidates,
                    .defaultRuleIndex = defaultRule,
                    .preselects = defaultList
                })
            Next

            Return list
        End Function

        Private Function BuildPmsOptions() As Object
            If _pmsRows Is Nothing Then Return New Object() {}
            Return _pmsRows.Select(Function(r) New With {.label = $"[{r.[Class]}] {r.SegmentKey}", .cls = r.[Class], .segment = r.SegmentKey}).ToList()
        End Function

        Private Function ParseStringList(payload As Object, name As String) As List(Of String)
            Dim res As New List(Of String)()
            Dim raw = GetProp(payload, name)
            If raw Is Nothing Then Return res

            Dim enumerable = TryCast(raw, System.Collections.IEnumerable)
            If enumerable IsNot Nothing AndAlso Not (TypeOf raw Is String) Then
                For Each o As Object In enumerable
                    Dim s = SegPmsSafeStr(o)
                    If Not String.IsNullOrWhiteSpace(s) AndAlso Not res.Any(Function(x) x.Equals(s, StringComparison.OrdinalIgnoreCase)) Then res.Add(s)
                Next
            Else
                Dim s = SegPmsSafeStr(raw)
                If Not String.IsNullOrWhiteSpace(s) AndAlso Not res.Any(Function(x) x.Equals(s, StringComparison.OrdinalIgnoreCase)) Then res.Add(s)
            End If
            Return res
        End Function

        Private Shared Function DataTableToObjects(t As DataTable) As List(Of Dictionary(Of String, Object))
            Dim list As New List(Of Dictionary(Of String, Object))()
            If t Is Nothing Then Return list
            For Each r As DataRow In t.Rows
                Dim d As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
                For Each c As DataColumn In t.Columns
                    d(c.ColumnName) = r(c)
                Next
                list.Add(d)
            Next
            Return list
        End Function

        Private Shared Sub AddSheet(wb As IWorkbook, name As String, rows As IEnumerable(Of Object))
            Dim sh = wb.CreateSheet(name)
            If rows Is Nothing Then Return
            Dim data = rows.ToList()
            If data.Count = 0 Then Return

            Dim first As IDictionary(Of String, Object) = TryCast(data(0), IDictionary(Of String, Object))
            If first Is Nothing Then Return
            Dim cols = first.Keys.ToList()
            Dim head = sh.CreateRow(0)
            For ci = 0 To cols.Count - 1
                head.CreateCell(ci).SetCellValue(cols(ci))
            Next

            Dim r As Integer = 1
            For Each item As IDictionary(Of String, Object) In data
                Dim row = sh.CreateRow(r)
                For ci = 0 To cols.Count - 1
                    Dim v = If(item(cols(ci)), String.Empty).ToString()
                    row.CreateCell(ci).SetCellValue(v)
                Next
                r += 1
            Next
        End Sub

        Private Shared Function NormalizePath(p As String) As String
            If String.IsNullOrWhiteSpace(p) Then Return String.Empty
            Try
                Return Path.GetFullPath(p)
            Catch
                Return p
            End Try
        End Function

        Private Sub UpdateLastNdRoundFromExtract(ds As DataSet)
            If ds Is Nothing Then Return
            If ds.Tables.Contains(SegmentPmsCheckService.TableMeta) Then
                Dim t = ds.Tables(SegmentPmsCheckService.TableMeta)
                If t.Rows.Count > 0 Then
                    Dim val = SegPmsSafeInt(t.Rows(0)("NdRound"))
                    If val > 0 Then _lastNdRound = val
                End If
            End If
        End Sub

        Private Shared Function SegPmsSafeStr(o As Object) As String
            If o Is Nothing Then Return String.Empty
            Try
                Return o.ToString()
            Catch
                Return String.Empty
            End Try
        End Function

        Private Shared Function SegPmsSafeInt(o As Object) As Integer
            If o Is Nothing Then Return 0
            Try
                Return Convert.ToInt32(o)
            Catch
                Return 0
            End Try
        End Function

        Private Shared Function TupleComparer() As IEqualityComparer(Of Tuple(Of String, String))
            Return New TupleComparerImpl()
        End Function

        Private Class TupleComparerImpl
            Implements IEqualityComparer(Of Tuple(Of String, String))

            Public Overloads Function Equals(x As Tuple(Of String, String), y As Tuple(Of String, String)) As Boolean Implements IEqualityComparer(Of Tuple(Of String, String)).Equals
                If x Is y Then Return True
                If x Is Nothing OrElse y Is Nothing Then Return False
                Return String.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) AndAlso String.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase)
            End Function

            Public Overloads Function GetHashCode(obj As Tuple(Of String, String)) As Integer Implements IEqualityComparer(Of Tuple(Of String, String)).GetHashCode
                If obj Is Nothing Then Return 0
                Return (If(obj.Item1, String.Empty).ToLowerInvariant().GetHashCode() Xor (If(obj.Item2, String.Empty).ToLowerInvariant().GetHashCode() << 3))
            End Function
        End Class

    End Class

End Namespace
