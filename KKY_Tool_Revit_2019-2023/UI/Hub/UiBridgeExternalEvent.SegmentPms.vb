Option Explicit On
Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.IO
Imports System.Text
Imports System.Windows.Forms
Imports Autodesk.Revit.UI
Imports KKY_Tool_Revit.Services
Imports NPOI.SS.UserModel
Imports NPOI.XSSF.UserModel

Namespace UI.Hub

    Partial Public Class UiBridgeExternalEvent

        Private _extractData As DataSet
        Private _pmsRows As List(Of SegmentPmsCheckService.PmsRow)
        Private _pmsUnitPref As String = "mm"
        Private _lastExtractPath As String = String.Empty

        Private Shared Function ParsePayloadDict(payload As Object) As Dictionary(Of String, Object)
            Dim dict = TryCast(payload, Dictionary(Of String, Object))
            If dict IsNot Nothing Then
                Return dict
            End If

            Dim result As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
            If payload Is Nothing Then
                Return result
            End If

            Dim t = payload.GetType()
            For Each p In t.GetProperties()
                Try
                    result(p.Name) = p.GetValue(payload, Nothing)
                Catch
                End Try
            Next
            Return result
        End Function

        Private Shared Function ParseStringList(payload As Dictionary(Of String, Object), key As String) As List(Of String)
            Dim list As New List(Of String)()
            If payload Is Nothing OrElse Not payload.ContainsKey(key) Then
                Return list
            End If

            Dim raw = payload(key)
            Dim enumerable = TryCast(raw, System.Collections.IEnumerable)
            If enumerable IsNot Nothing AndAlso Not (TypeOf raw Is String) Then
                For Each o As Object In enumerable
                    Dim s = If(o, String.Empty).ToString()
                    If Not String.IsNullOrWhiteSpace(s) AndAlso Not list.Contains(s) Then
                        list.Add(s)
                    End If
                Next
            Else
                Dim s = If(raw, String.Empty).ToString()
                If Not String.IsNullOrWhiteSpace(s) Then
                    list.Add(s)
                End If
            End If
            Return list
        End Function

        Private Shared Function ParseExtractOptions(payload As Dictionary(Of String, Object)) As SegmentPmsCheckService.ExtractOptions
            Dim opts As New SegmentPmsCheckService.ExtractOptions()
            If payload Is Nothing Then
                Return opts
            End If

            Dim optionsObj As Object = Nothing
            If payload.TryGetValue("options", optionsObj) Then
                Dim dict = ParsePayloadDict(optionsObj)
                If dict.ContainsKey("ndRound") Then
                    Dim v = dict("ndRound")
                    Dim iv As Integer
                    If Integer.TryParse(If(v, 3).ToString(), iv) Then
                        opts.NdRound = iv
                    End If
                End If
                If dict.ContainsKey("tolMm") Then
                    Dim v = dict("tolMm")
                    Dim dv As Double
                    If Double.TryParse(If(v, opts.ToleranceMm).ToString(), dv) Then
                        opts.ToleranceMm = dv
                    End If
                End If
            End If
            If payload.ContainsKey("ndRound") Then
                Dim v = payload("ndRound")
                Dim iv As Integer
                If Integer.TryParse(If(v, opts.NdRound).ToString(), iv) Then
                    opts.NdRound = iv
                End If
            End If
            If payload.ContainsKey("tolMm") Then
                Dim v = payload("tolMm")
                Dim dv As Double
                If Double.TryParse(If(v, opts.ToleranceMm).ToString(), dv) Then
                    opts.ToleranceMm = dv
                End If
            End If
            Return opts
        End Function

        Private Shared Function GetDictValue(dict As Dictionary(Of String, Object), key As String) As Object
            If dict Is Nothing Then
                Return Nothing
            End If
            Dim val As Object = Nothing
            If dict.TryGetValue(key, val) Then
                Return val
            End If
            Return Nothing
        End Function

        Private Shared Function ParseCompareOptions(payload As Dictionary(Of String, Object)) As SegmentPmsCheckService.CompareOptions
            Dim opts As New SegmentPmsCheckService.CompareOptions()
            If payload Is Nothing Then
                Return opts
            End If

            If payload.ContainsKey("ndRound") Then
                Dim v = payload("ndRound")
                Dim iv As Integer
                If Integer.TryParse(If(v, 3).ToString(), iv) Then
                    opts.NdRound = iv
                End If
            End If
            If payload.ContainsKey("tolMm") Then
                Dim v = payload("tolMm")
                Dim dv As Double
                If Double.TryParse(If(v, 0.01R).ToString(), dv) Then
                    opts.TolMm = dv
                End If
            End If
            Return opts
        End Function

        Private Shared Function ParseMappings(payload As Dictionary(Of String, Object)) As List(Of SegmentPmsCheckService.MappingSelection)
            Dim res As New List(Of SegmentPmsCheckService.MappingSelection)()
            If payload Is Nothing OrElse Not payload.ContainsKey("mappings") Then
                Return res
            End If

            Dim raw = payload("mappings")
            Dim arr = TryCast(raw, System.Collections.IEnumerable)
            If arr Is Nothing OrElse TypeOf raw Is String Then
                Return res
            End If

            For Each o In arr
                Dim d = ParsePayloadDict(o)
                Dim item As New SegmentPmsCheckService.MappingSelection()
                If d.ContainsKey("file") Then
                    item.File = If(d("file"), String.Empty).ToString()
                End If
                If d.ContainsKey("pipeType") Then
                    item.PipeTypeName = If(d("pipeType"), String.Empty).ToString()
                End If
                If d.ContainsKey("ruleIndex") Then
                    Dim iv As Integer
                    If Integer.TryParse(If(d("ruleIndex"), 0).ToString(), iv) Then
                        item.RuleIndex = iv
                    End If
                End If
                If d.ContainsKey("segmentId") Then
                    Dim iv As Integer
                    If Integer.TryParse(If(d("segmentId"), 0).ToString(), iv) Then
                        item.SegmentId = iv
                    End If
                End If
                If d.ContainsKey("segmentKey") Then
                    item.SegmentKey = If(d("segmentKey"), String.Empty).ToString()
                End If
                If d.ContainsKey("cls") Then
                    item.SelectedClass = If(d("cls"), String.Empty).ToString()
                End If
                If d.ContainsKey("segment") Then
                    item.SelectedPmsSegment = If(d("segment"), String.Empty).ToString()
                End If
                If d.ContainsKey("source") Then
                    item.MappingSource = If(d("source"), String.Empty).ToString()
                End If
                res.Add(item)
            Next
            Return res
        End Function

        Private Shared Function ParseGroupSelections(payload As Dictionary(Of String, Object)) As List(Of SegmentPmsCheckService.GroupSelection)
            Dim res As New List(Of SegmentPmsCheckService.GroupSelection)()
            If payload Is Nothing OrElse Not payload.ContainsKey("groups") Then
                Return res
            End If
            Dim raw = payload("groups")
            Dim arr = TryCast(raw, System.Collections.IEnumerable)
            If arr Is Nothing OrElse TypeOf raw Is String Then
                Return res
            End If
            For Each o In arr
                Dim d = ParsePayloadDict(o)
                Dim g As New SegmentPmsCheckService.GroupSelection()
                If d.ContainsKey("groupKey") Then
                    g.GroupKey = If(d("groupKey"), String.Empty).ToString()
                End If
                If d.ContainsKey("cls") Then
                    g.SelectedClass = If(d("cls"), String.Empty).ToString()
                End If
                If d.ContainsKey("segment") Then
                    g.SelectedPmsSegment = If(d("segment"), String.Empty).ToString()
                End If
                If d.ContainsKey("source") Then
                    g.SelectionSource = If(d("source"), String.Empty).ToString()
                End If
                res.Add(g)
            Next
            Return res
        End Function

        Private Shared Function SafeIntObj(o As Object, Optional def As Integer = 0) As Integer
            If o Is Nothing Then
                Return def
            End If
            Dim v As Integer
            If Integer.TryParse(o.ToString(), v) Then
                Return v
            End If
            Dim dv As Double
            If Double.TryParse(o.ToString(), Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture, dv) Then
                Return CInt(Math.Truncate(dv))
            End If
            Return def
        End Function

        Private Sub HandleSegmentPmsRvtPickFiles(app As UIApplication, payload As Object)
            Using dlg As New OpenFileDialog()
                dlg.Filter = "Revit Files (*.rvt)|*.rvt"
                dlg.Multiselect = True
                dlg.RestoreDirectory = True
                dlg.Title = "RVT 파일 선택"
                If dlg.ShowDialog() <> DialogResult.OK Then
                    Return
                End If
                Dim files As New List(Of String)()
                For Each f As String In dlg.FileNames
                    files.Add(f)
                Next
                SendToWeb("segmentpms:rvt-picked-files", New With {.paths = files})
            End Using
        End Sub

        Private Sub HandleSegmentPmsRvtPickFolder(app As UIApplication, payload As Object)
            Using dlg As New FolderBrowserDialog()
                dlg.Description = "RVT가 있는 폴더를 선택하세요."
                If dlg.ShowDialog() <> DialogResult.OK Then
                    Return
                End If

                Dim files As New List(Of String)()
                Try
                    For Each f As String In Directory.GetFiles(dlg.SelectedPath, "*.rvt", SearchOption.TopDirectoryOnly)
                        files.Add(f)
                    Next
                Catch
                End Try
                SendToWeb("segmentpms:rvt-picked-folder", New With {.paths = files})
            End Using
        End Sub

        Private Sub HandleSegmentPmsExtractStart(app As UIApplication, payload As Object)
            Dim pd = ParsePayloadDict(payload)
            Dim files = ParseStringList(pd, "files")
            Dim opts = ParseExtractOptions(pd)
            If files.Count = 0 Then
                SendToWeb("segmentpms:error", New With {.message = "추출할 RVT 파일을 선택하세요."})
                Return
            End If

            Using dlg As New SaveFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.FileName = "SegmentPmsExtract.xlsx"
                dlg.AddExtension = True
                dlg.RestoreDirectory = True
                If dlg.ShowDialog() <> DialogResult.OK Then
                    Return
                End If

                Try
                    _extractData = SegmentPmsCheckService.ExtractToDataSet(app, files, opts)
                    _lastExtractPath = dlg.FileName
                    SegmentPmsCheckService.SaveDataSetToXlsx(_extractData, dlg.FileName)
                    Dim summary = BuildExtractSummary(_extractData)
                    SendToWeb("segmentpms:extract-saved", New With {.path = dlg.FileName, .summary = summary})
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Sub HandleSegmentPmsSaveExtract(app As UIApplication, payload As Object)
            If _extractData Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "저장할 추출 데이터가 없습니다."})
                Return
            End If
            Using dlg As New SaveFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.FileName = If(String.IsNullOrWhiteSpace(_lastExtractPath), "SegmentPmsExtract.xlsx", Path.GetFileName(_lastExtractPath))
                dlg.AddExtension = True
                dlg.RestoreDirectory = True
                If dlg.ShowDialog() <> DialogResult.OK Then
                    Return
                End If
                Try
                    SegmentPmsCheckService.SaveDataSetToXlsx(_extractData, dlg.FileName)
                    _lastExtractPath = dlg.FileName
                    Dim summary = BuildExtractSummary(_extractData)
                    SendToWeb("segmentpms:extract-saved", New With {.path = dlg.FileName, .summary = summary})
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Sub HandleSegmentPmsLoadExtract(app As UIApplication, payload As Object)
            Using dlg As New OpenFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.RestoreDirectory = True
                dlg.Title = "추출 Excel 불러오기"
                If dlg.ShowDialog() <> DialogResult.OK Then
                    Return
                End If

                Try
                    _extractData = SegmentPmsCheckService.LoadExtractFromXlsx(dlg.FileName)
                    _lastExtractPath = dlg.FileName
                    Dim summary = BuildExtractSummary(_extractData)
                    Dim groups = SegmentPmsCheckService.BuildGroups(_extractData)
                    Dim suggest = SegmentPmsCheckService.SuggestGroupMappings(groups, _pmsRows)
                    Dim pmsOpts = BuildPmsOptions()
                    Dim groupPayload = BuildGroupPayload(groups)
                    SendToWeb("segmentpms:extract-loaded", New With {
                        .summary = summary,
                        .groups = groupPayload,
                        .suggestions = suggest,
                        .pms = pmsOpts,
                        .path = dlg.FileName
                    })
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Sub HandleSegmentPmsRegisterPms(app As UIApplication, payload As Object)
            Dim unitPref As String = "mm"
            Dim pd = ParsePayloadDict(payload)
            If pd.ContainsKey("unit") Then
                unitPref = If(pd("unit"), "mm").ToString()
            End If
            Using dlg As New OpenFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.Title = "PMS Excel 선택"
                dlg.RestoreDirectory = True
                If dlg.ShowDialog() <> DialogResult.OK Then
                    Return
                End If

                Try
                    Dim loaded = SegmentPmsCheckService.LoadPmsExcel(dlg.FileName, unitPref)
                    _pmsRows = loaded.Rows
                    _pmsUnitPref = unitPref
                    If loaded.Errors IsNot Nothing AndAlso loaded.Errors.Count > 0 Then
                        SendToWeb("segmentpms:error", New With {.message = String.Join(";", loaded.Errors)})
                        Return
                    End If
                    Dim pmsOpts = BuildPmsOptions()
                    Dim suggestList As List(Of SegmentPmsCheckService.SuggestedMapping) = Nothing
                    Dim groupPayload As List(Of Object) = Nothing
                    If _extractData IsNot Nothing Then
                        Dim groups = SegmentPmsCheckService.BuildGroups(_extractData)
                        suggestList = SegmentPmsCheckService.SuggestGroupMappings(groups, _pmsRows)
                        groupPayload = BuildGroupPayload(groups)
                    End If
                    SendToWeb("segmentpms:pms-registered", New With {.path = dlg.FileName, .options = pmsOpts, .suggestions = suggestList, .groups = groupPayload})
                Catch ex As Exception
                    SendToWeb("segmentpms:error", New With {.message = ex.Message})
                End Try
            End Using
        End Sub

        Private Sub HandleSegmentPmsPrepareMapping(app As UIApplication, payload As Object)
            If _extractData Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "추출 데이터를 먼저 불러오세요."})
                Return
            End If
            Dim groups = SegmentPmsCheckService.BuildGroups(_extractData)
            Dim suggestions As List(Of SegmentPmsCheckService.SuggestedMapping) = Nothing
            If _pmsRows IsNot Nothing Then
                suggestions = SegmentPmsCheckService.SuggestGroupMappings(groups, _pmsRows)
            End If
            Dim pmsOpts = BuildPmsOptions()
            Dim groupPayload = BuildGroupPayload(groups)
            SendToWeb("segmentpms:mapping-ready", New With {.groups = groupPayload, .pms = pmsOpts, .suggestions = suggestions})
        End Sub

        Private Sub HandleSegmentPmsRun(app As UIApplication, payload As Object)
            If _extractData Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "추출 데이터를 먼저 불러오세요."})
                Return
            End If
            If _pmsRows Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "PMS Excel을 등록하세요."})
                Return
            End If

            Dim pd = ParsePayloadDict(payload)
            Dim maps = ParseMappings(pd)
            Dim groupSelections = ParseGroupSelections(pd)
            Dim opts = ParseCompareOptions(pd)
            If (maps Is Nothing OrElse maps.Count = 0) AndAlso groupSelections IsNot Nothing AndAlso groupSelections.Count > 0 Then
                Dim groups = SegmentPmsCheckService.BuildGroups(_extractData)
                maps = SegmentPmsCheckService.ExpandGroupSelections(groups, groupSelections)
            End If

            Try
                Dim run = SegmentPmsCheckService.RunCompare(_extractData, _pmsRows, maps, opts)
                Dim compare = DataTableToObjects(run.CompareTable)
                Dim map = DataTableToObjects(run.MapTable)
                Dim revitRaw = DataTableToObjects(run.RevitSizeTable)
                Dim pmsRaw = DataTableToObjects(run.PmsSizeTable)
                Dim err = DataTableToObjects(run.ErrorTable)
                Dim summary = DataTableToObjects(run.SummaryTable)
                SendToWeb("segmentpms:result", New With {
                    .compare = compare,
                    .map = map,
                    .revitRaw = revitRaw,
                    .pmsRaw = pmsRaw,
                    .summary = summary,
                    .errors = err
                })
            Catch ex As Exception
                SendToWeb("segmentpms:error", New With {.message = ex.Message})
            End Try
        End Sub

        Private Sub HandleSegmentPmsSaveResult(app As UIApplication, payload As Object)
            Dim pd = ParsePayloadDict(payload)
            Dim compare = TryCast(GetDictValue(pd, "compare"), IEnumerable(Of Object))
            If compare Is Nothing Then
                SendToWeb("segmentpms:error", New With {.message = "저장할 결과가 없습니다."})
                Return
            End If
            Dim map = TryCast(GetDictValue(pd, "map"), IEnumerable(Of Object))
            Dim revitRaw = TryCast(GetDictValue(pd, "revitRaw"), IEnumerable(Of Object))
            Dim pmsRaw = TryCast(GetDictValue(pd, "pmsRaw"), IEnumerable(Of Object))
            Dim errors = TryCast(GetDictValue(pd, "errors"), IEnumerable(Of Object))
            Dim summary = TryCast(GetDictValue(pd, "summary"), IEnumerable(Of Object))

            Using dlg As New SaveFileDialog()
                dlg.Filter = "Excel (*.xlsx)|*.xlsx"
                dlg.FileName = "SegmentPmsResult.xlsx"
                dlg.AddExtension = True
                If dlg.ShowDialog() <> DialogResult.OK Then
                    Return
                End If

                Try
                    Dim wb As IWorkbook = New XSSFWorkbook()
                    AddSheet(wb, "Compare", compare)
                    AddSheet(wb, "PipeTypeSegmentMap", map)
                    AddSheet(wb, "SegmentSizeRaw_Revit", revitRaw)
                    AddSheet(wb, "SegmentSizeRaw_PMS", pmsRaw)
                    AddSheet(wb, "Summary", summary)
                    AddSheet(wb, "Error", errors)
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
            If ds Is Nothing OrElse Not ds.Tables.Contains(SegmentPmsCheckService.TableRules) Then
                Return String.Empty
            End If
            Dim rules = ds.Tables(SegmentPmsCheckService.TableRules)
            Dim sizes As DataTable = Nothing
            If ds.Tables.Contains(SegmentPmsCheckService.TableSizes) Then
                sizes = ds.Tables(SegmentPmsCheckService.TableSizes)
            End If
            Dim fileSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim pipeSet As New HashSet(Of Tuple(Of String, String))(TupleComparer())
            For Each r As DataRow In rules.Rows
                fileSet.Add(NormalizePath(SafeStr(r("File"))))
                pipeSet.Add(Tuple.Create(SafeStr(r("File")), SafeStr(r("PipeTypeName"))))
            Next
            Dim sizeCount As Integer = 0
            If sizes IsNot Nothing Then
                sizeCount = sizes.Rows.Count
            End If
            Return $"파일 {fileSet.Count}개, PipeType {pipeSet.Count}, Segment 후보 {rules.Rows.Count}, 사이즈 {sizeCount}"
        End Function

        Private Function BuildGroupPayload(groups As List(Of SegmentPmsCheckService.MappingGroup)) As List(Of Object)
            Dim list As New List(Of Object)()
            If groups Is Nothing Then
                Return list
            End If
            For Each g In groups
                Dim usages As New List(Of Object)()
                For Each u In g.Usages
                    usages.Add(New With {
                        .file = u.File,
                        .pipeType = u.PipeTypeName,
                        .ruleIndex = u.RuleIndex,
                        .segmentId = u.SegmentId,
                        .segmentKey = u.SegmentKey
                    })
                Next
                list.Add(New With {
                    .groupKey = g.GroupKey,
                    .displayKey = g.DisplayKey,
                    .normalizedKey = g.NormalizedKey,
                    .usageSummary = g.UsageSummary,
                    .fileCount = g.FileCount,
                    .pipeTypeCount = g.PipeTypeCount,
                    .usages = usages,
                    .suggestedClass = g.SuggestedClass,
                    .suggestedSegmentKey = g.SuggestedSegmentKey
                })
            Next
            Return list
        End Function

        Private Function BuildPmsOptions() As List(Of Object)
            Dim list As New List(Of Object)()
            If _pmsRows Is Nothing Then
                Return list
            End If
            For Each r In _pmsRows
                list.Add(New With {.label = $"{r.Class} | {r.SegmentKey}", .cls = r.Class, .segment = r.SegmentKey})
            Next
            Return list
        End Function

        Private Shared Function DataTableToObjects(t As DataTable) As List(Of Dictionary(Of String, Object))
            Dim list As New List(Of Dictionary(Of String, Object))()
            If t Is Nothing Then
                Return list
            End If
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
            If rows Is Nothing Then
                Return
            End If
            Dim data As New List(Of IDictionary(Of String, Object))()
            For Each obj In rows
                Dim d = TryCast(obj, IDictionary(Of String, Object))
                If d IsNot Nothing Then
                    data.Add(d)
                End If
            Next
            If data.Count = 0 Then
                Return
            End If

            Dim headRow = sh.CreateRow(0)
            Dim cols = New List(Of String)(data(0).Keys)
            For ci As Integer = 0 To cols.Count - 1
                headRow.CreateCell(ci).SetCellValue(cols(ci))
            Next

            Dim rIndex As Integer = 1
            For Each item In data
                Dim row = sh.CreateRow(rIndex)
                For ci As Integer = 0 To cols.Count - 1
                    Dim key = cols(ci)
                    Dim v As Object = Nothing
                    item.TryGetValue(key, v)
                    Dim cellText As String = String.Empty
                    If v IsNot Nothing AndAlso Not TypeOf v Is DBNull Then
                        cellText = v.ToString()
                    End If
                    row.CreateCell(ci).SetCellValue(cellText)
                Next
                rIndex += 1
            Next
        End Sub

        Private Shared Function NormalizePath(p As String) As String
            If String.IsNullOrWhiteSpace(p) Then
                Return String.Empty
            End If
            Try
                Return Path.GetFullPath(p)
            Catch
                Return p
            End Try
        End Function

        Private Shared Function SafeStr(o As Object) As String
            If o Is Nothing Then
                Return String.Empty
            End If
            Return o.ToString()
        End Function

        Private Shared Function TupleComparer() As IEqualityComparer(Of Tuple(Of String, String))
            Return New TupleComparerImpl()
        End Function

        Private Class TupleComparerImpl
            Implements IEqualityComparer(Of Tuple(Of String, String))

            Public Overloads Function Equals(x As Tuple(Of String, String), y As Tuple(Of String, String)) As Boolean Implements IEqualityComparer(Of Tuple(Of String, String)).Equals
                If x Is y Then
                    Return True
                End If
                If x Is Nothing OrElse y Is Nothing Then
                    Return False
                End If
                Return String.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) AndAlso String.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase)
            End Function

            Public Overloads Function GetHashCode(obj As Tuple(Of String, String)) As Integer Implements IEqualityComparer(Of Tuple(Of String, String)).GetHashCode
                If obj Is Nothing Then
                    Return 0
                End If
                Return (If(obj.Item1, String.Empty).ToLowerInvariant().GetHashCode() Xor (If(obj.Item2, String.Empty).ToLowerInvariant().GetHashCode() << 3))
            End Function
        End Class

    End Class

End Namespace
