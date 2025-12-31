Option Explicit On
Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.Globalization
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports Autodesk.Revit.DB
Imports Autodesk.Revit.DB.Plumbing
Imports Autodesk.Revit.UI
Imports NPOI.SS.UserModel
Imports NPOI.XSSF.UserModel
Imports RvtDB = Autodesk.Revit.DB
Imports NpoiCellType = NPOI.SS.UserModel.CellType

Namespace Services

    Public Class SegmentPmsCheckService

        Public Class PmsRow
            Public Property [Class] As String = String.Empty
            Public Property SegmentKey As String = String.Empty
            Public Property NdMm As Double
            Public Property IdMm As Double
            Public Property OdMm As Double
        End Class

        Public Class MappingSelection
            Public Property [File] As String = String.Empty
            Public Property PipeTypeName As String = String.Empty
            Public Property RuleIndex As Integer
            Public Property SegmentId As Integer
            Public Property SegmentKey As String = String.Empty
            Public Property SelectedClass As String = String.Empty
            Public Property SelectedPmsSegment As String = String.Empty
            Public Property MappingSource As String = String.Empty
        End Class

        Public Class GroupSelection
            Public Property GroupKey As String = String.Empty
            Public Property SelectedClass As String = String.Empty
            Public Property SelectedPmsSegment As String = String.Empty
            Public Property SelectionSource As String = String.Empty
        End Class

        Public Class ExtractOptions
            Public Property NdRound As Integer = 3
            Public Property DetachFromCentral As Boolean = True
            Public Property OpenReadOnly As Boolean = True
            Public Property ToleranceMm As Double = 0.01R
        End Class

        Public Class CompareOptions
            Public Property NdRound As Integer = 3
            Public Property TolMm As Double = 0.01R
            Public Property ClassMatch As Boolean = False
        End Class

        Public Class SuggestedMapping
            Public Property [File] As String = String.Empty
            Public Property PipeTypeName As String = String.Empty
            Public Property RuleIndex As Integer
            Public Property SegmentId As Integer
            Public Property SegmentKey As String = String.Empty
            Public Property PmsClass As String = String.Empty
            Public Property PmsSegmentKey As String = String.Empty
            Public Property Score As Double
        End Class

        Public Class MappingGroup
            Public Property GroupKey As String = String.Empty
            Public Property DisplayKey As String = String.Empty
            Public Property NormalizedKey As String = String.Empty
            Public Property Usages As List(Of MappingUsage)
            Public Property SuggestedClass As String = String.Empty
            Public Property SuggestedSegmentKey As String = String.Empty
            Public Property FileCount As Integer
            Public Property PipeTypeCount As Integer
            Public Property UsageSummary As String = String.Empty
        End Class

        Public Class MappingUsage
            Public Property [File] As String = String.Empty
            Public Property PipeTypeName As String = String.Empty
            Public Property RuleIndex As Integer
            Public Property SegmentId As Integer
            Public Property SegmentKey As String = String.Empty
        End Class

        Public Class RunResult
            Public Property MapTable As DataTable
            Public Property RevitSizeTable As DataTable
            Public Property PmsSizeTable As DataTable
            Public Property CompareTable As DataTable
            Public Property ErrorTable As DataTable
            Public Property SummaryTable As DataTable
        End Class

        Public Class LoadPmsResult
            Public Property Table As DataTable
            Public Property Rows As List(Of PmsRow)
            Public Property Errors As List(Of String)
        End Class

        Public Const TableMeta As String = "Extract_Meta"
        Public Const TableFiles As String = "Extract_Files"
        Public Const TableRules As String = "Extract_Rules"
        Public Const TableSizes As String = "Extract_Sizes"
        Public Const TableRouting As String = "Extract_Routing"
        Private Const FeetToMm As Double = 304.8R
        Private Const ToolVersion As String = "SegmentPms 2.0"

        ' ---------------------------
        ' Extract stage
        ' ---------------------------
        Public Shared Function ExtractToDataSet(app As UIApplication, files As IEnumerable(Of String), options As ExtractOptions) As DataSet
            Dim ds As New DataSet()
            Dim meta = BuildMetaTable()
            Dim fileTable = BuildFileTable()
            Dim rules = BuildRuleTable()
            Dim sizes = BuildSizeTable()
            Dim routing = BuildRoutingTable()
            ds.Tables.Add(meta)
            ds.Tables.Add(fileTable)
            ds.Tables.Add(rules)
            ds.Tables.Add(sizes)
            ds.Tables.Add(routing)

            Dim metaRow = meta.NewRow()
            metaRow("NdRound") = options.NdRound
            metaRow("Tolerance") = options.ToleranceMm
            metaRow("CreatedAt") = DateTime.Now.ToString("s", CultureInfo.InvariantCulture)
            metaRow("ToolVersion") = ToolVersion
            meta.Rows.Add(metaRow)

            Dim valid As New List(Of String)()
            If files IsNot Nothing Then
                For Each f As String In files
                    If Not String.IsNullOrWhiteSpace(f) AndAlso File.Exists(f) Then
                        Dim already As Boolean = False
                        For Each v As String In valid
                            If v.Equals(f, StringComparison.OrdinalIgnoreCase) Then
                                already = True
                                Exit For
                            End If
                        Next
                        If Not already Then
                            valid.Add(f)
                        End If
                    End If
                Next
            End If

            If valid.Count = 0 Then
                Return ds
            End If

            Dim appObj = app.Application
            For Each p As String In valid
                Dim doc As RvtDB.Document = Nothing
                Try
                    Dim opt = BuildOpenOptions(options, p)
                    Dim mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(p)
                    doc = appObj.OpenDocumentFile(mp, opt)

                    Dim fileRow = fileTable.NewRow()
                    fileRow("File") = p
                    fileRow("FileName") = Path.GetFileName(p)
                    fileRow("ExtractedAt") = DateTime.Now.ToString("s", CultureInfo.InvariantCulture)
                    fileTable.Rows.Add(fileRow)

                    Dim routingInfos = CollectRouting(doc, p)
                    For Each info In routingInfos
                        Dim row = routing.NewRow()
                        row("File") = info.File
                        row("PipeTypeName") = info.PipeTypeName
                        row("RuleGroup") = info.RuleGroup
                        row("RuleIndex") = info.RuleIndex
                        row("RuleType") = info.RuleType
                        row("PartId") = info.PartId
                        row("PartName") = info.PartName
                        routing.Rows.Add(row)
                    Next

                    Dim pipeInfos = CollectPipeTypeSegmentCandidates(doc, p)
                    For Each pi In pipeInfos
                        For Each cand In pi.Candidates
                            Dim rr = rules.NewRow()
                            rr("File") = p
                            rr("PipeTypeName") = pi.PipeTypeName
                            rr("RuleIndex") = cand.RuleIndex
                            rr("SegmentId") = cand.SegmentId
                            rr("SegmentKey") = cand.SegmentKey
                            rules.Rows.Add(rr)
                        Next
                    Next

                    Dim segIds As New HashSet(Of Integer)()
                    For Each pi In pipeInfos
                        For Each c In pi.Candidates
                            segIds.Add(c.SegmentId)
                        Next
                    Next

                    Dim sizeRows = CollectSegmentSizes(doc, segIds, p, options.NdRound)
                    For Each s In sizeRows
                        Dim sr = sizes.NewRow()
                        sr("File") = s.File
                        sr("SegmentId") = s.SegmentId
                        sr("SegmentKey") = s.SegmentKey
                        sr("ND_mm") = s.NdMm
                        sr("ID_mm") = s.IdMm
                        sr("OD_mm") = s.OdMm
                        sizes.Rows.Add(sr)
                    Next
                Catch
                    ' 개별 파일 오류는 누적하지 않고 건너뜀
                Finally
                    If doc IsNot Nothing Then
                        Try
                            doc.Close(False)
                        Catch
                        End Try
                    End If
                End Try
            Next

            Return ds
        End Function

        Public Shared Sub SaveDataSetToXlsx(ds As DataSet, path As String)
            If ds Is Nothing Then Return
            Dim wb As IWorkbook = New XSSFWorkbook()
            If ds.Tables.Contains(TableMeta) Then WriteSheet(wb, TableMeta, ds.Tables(TableMeta))
            If ds.Tables.Contains(TableFiles) Then WriteSheet(wb, TableFiles, ds.Tables(TableFiles))
            If ds.Tables.Contains(TableRules) Then WriteSheet(wb, TableRules, ds.Tables(TableRules))
            If ds.Tables.Contains(TableSizes) Then WriteSheet(wb, TableSizes, ds.Tables(TableSizes))
            If ds.Tables.Contains(TableRouting) Then WriteSheet(wb, TableRouting, ds.Tables(TableRouting))
            Using fs As New FileStream(path, FileMode.Create, FileAccess.Write)
                wb.Write(fs)
            End Using
        End Sub

        Public Shared Function LoadExtractFromXlsx(path As String) As DataSet
            Dim ds As New DataSet()
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then
                Return ds
            End If

            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Dim wb As IWorkbook = New XSSFWorkbook(fs)
                For i As Integer = 0 To wb.NumberOfSheets - 1
                    Dim sh As ISheet = wb.GetSheetAt(i)
                    If sh Is Nothing Then
                        Continue For
                    End If
                    Dim t As New DataTable(sh.SheetName)
                    Dim head As IRow = sh.GetRow(0)
                    If head Is Nothing Then
                        Continue For
                    End If
                    For ci As Integer = 0 To head.LastCellNum - 1
                        t.Columns.Add(CellStr(head, ci), GetType(String))
                    Next
                    For r As Integer = 1 To sh.LastRowNum
                        Dim row As IRow = sh.GetRow(r)
                        If row Is Nothing Then
                            Continue For
                        End If
                        Dim dr = t.NewRow()
                        For ci As Integer = 0 To t.Columns.Count - 1
                            dr(ci) = CellStr(row, ci)
                        Next
                        t.Rows.Add(dr)
                    Next
                    ds.Tables.Add(t)
                Next
            End Using

            EnsureSchema(ds)
            Return ds
        End Function

        ' ---------------------------
        ' PMS
        ' ---------------------------
        Public Shared Function LoadPmsExcel(xlsxPath As String, preferredUnit As String) As LoadPmsResult
            Dim res As New LoadPmsResult With {.Table = BuildPmsTableSkeleton(), .Rows = New List(Of PmsRow)(), .Errors = New List(Of String)()}
            If String.IsNullOrWhiteSpace(xlsxPath) OrElse Not File.Exists(xlsxPath) Then
                res.Errors.Add("PMS 파일이 존재하지 않습니다.")
                Return res
            End If

            Dim wb As IWorkbook = Nothing
            Using fs As New FileStream(xlsxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                wb = New XSSFWorkbook(fs)
            End Using
            If wb.NumberOfSheets <= 0 Then
                res.Errors.Add("PMS 파일에 시트가 없습니다.")
                Return res
            End If

            Dim sh As ISheet = wb.GetSheetAt(0)
            If sh Is Nothing Then
                res.Errors.Add("PMS 시트를 읽을 수 없습니다.")
                Return res
            End If

            Dim headerMap = DetectHeader(sh)
            If Not headerMap.ContainsKey("class") Then res.Errors.Add("CLASS 헤더를 찾을 수 없습니다.")
            If Not headerMap.ContainsKey("segment") Then res.Errors.Add("Segment 헤더를 찾을 수 없습니다.")
            If Not headerMap.ContainsKey("nd") Then res.Errors.Add("ND 헤더를 찾을 수 없습니다.")
            If Not headerMap.ContainsKey("id") Then res.Errors.Add("ID 헤더를 찾을 수 없습니다.")
            If Not headerMap.ContainsKey("od") Then res.Errors.Add("OD 헤더를 찾을 수 없습니다.")
            If res.Errors.Count > 0 Then
                Return res
            End If

            Dim unitLabel As String = If(String.IsNullOrWhiteSpace(preferredUnit), "mm", preferredUnit).ToLowerInvariant()

            Dim lastRow As Integer = sh.LastRowNum
            For i As Integer = 1 To lastRow
                Dim row As IRow = sh.GetRow(i)
                If row Is Nothing Then
                    Continue For
                End If
                Dim cls As String = CellStr(row, headerMap("class"))
                Dim seg As String = CellStr(row, headerMap("segment"))
                Dim nd As Double = CellDbl(row, headerMap("nd"), 0)
                Dim id As Double = CellDbl(row, headerMap("id"), 0)
                Dim od As Double = CellDbl(row, headerMap("od"), 0)

                If String.IsNullOrWhiteSpace(seg) Then
                    Continue For
                End If

                Dim ndMm As Double = nd
                Dim idMm As Double = id
                Dim odMm As Double = od

                If unitLabel.IndexOf("in", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    ndMm = nd * 25.4R
                    idMm = id * 25.4R
                    odMm = od * 25.4R
                End If

                Dim dataRow = res.Table.NewRow()
                dataRow("CLASS") = cls
                dataRow("PMS_SegmentKey") = seg
                dataRow("ND_mm") = ndMm.ToString("0.###", CultureInfo.InvariantCulture)
                dataRow("ID_mm") = idMm.ToString("0.###", CultureInfo.InvariantCulture)
                dataRow("OD_mm") = odMm.ToString("0.###", CultureInfo.InvariantCulture)
                res.Table.Rows.Add(dataRow)

                res.Rows.Add(New PmsRow With {
                    .Class = cls,
                    .SegmentKey = seg,
                    .NdMm = ndMm,
                    .IdMm = idMm,
                    .OdMm = odMm
                })
            Next

            Return res
        End Function

        ' ---------------------------
        ' Suggestion / Compare
        ' ---------------------------
        Public Shared Function SuggestMappings(extractData As DataSet, pmsData As List(Of PmsRow)) As List(Of SuggestedMapping)
            Dim result As New List(Of SuggestedMapping)()
            If extractData Is Nothing OrElse pmsData Is Nothing Then
                Return result
            End If
            If Not extractData.Tables.Contains(TableRules) Then
                Return result
            End If
            Dim rules = extractData.Tables(TableRules)

            For Each r As DataRow In rules.Rows
                Dim filePath = SafeStr(r("File"))
                Dim pipeName = SafeStr(r("PipeTypeName"))
                Dim ruleIdx = SafeIntObj(r("RuleIndex"))
                Dim segId = SafeIntObj(r("SegmentId"))
                Dim segKey = SafeStr(r("SegmentKey"))
                Dim normSeg = NormalizeKey(segKey)

                Dim bestScore As Double = -1
                Dim bestClass As String = String.Empty
                Dim bestSeg As String = String.Empty

                For Each p In pmsData
                    Dim normP = NormalizeKey(p.SegmentKey)
                    Dim sim = SimilarityScore(normSeg, normP)
                    If sim > bestScore Then
                        bestScore = sim
                        bestClass = p.Class
                        bestSeg = p.SegmentKey
                    End If
                Next

                If bestScore < 0.4R Then
                    bestScore = -1
                    bestClass = String.Empty
                    bestSeg = String.Empty
                End If

                result.Add(New SuggestedMapping With {
                    .File = filePath,
                    .PipeTypeName = pipeName,
                    .RuleIndex = ruleIdx,
                    .SegmentId = segId,
                    .SegmentKey = segKey,
                    .PmsClass = bestClass,
                    .PmsSegmentKey = bestSeg,
                    .Score = bestScore
                })
            Next

            Return result
        End Function

        Public Shared Function BuildGroups(extractData As DataSet) As List(Of MappingGroup)
            Dim groups As New Dictionary(Of String, MappingGroup)(StringComparer.OrdinalIgnoreCase)
            If extractData Is Nothing OrElse Not extractData.Tables.Contains(TableRules) Then
                Return New List(Of MappingGroup)()
            End If
            Dim rules = extractData.Tables(TableRules)
            For Each r As DataRow In rules.Rows
                Dim segKey = SafeStr(r("SegmentKey"))
                Dim norm = NormalizeKey(segKey)
                Dim groupKey = If(String.IsNullOrWhiteSpace(norm), segKey, norm)
                If Not groups.ContainsKey(groupKey) Then
                    groups(groupKey) = New MappingGroup With {
                        .GroupKey = groupKey,
                        .DisplayKey = segKey,
                        .NormalizedKey = norm,
                        .Usages = New List(Of MappingUsage)(),
                        .FileCount = 0,
                        .PipeTypeCount = 0,
                        .UsageSummary = String.Empty
                    }
                End If
                Dim g = groups(groupKey)
                If String.IsNullOrWhiteSpace(g.DisplayKey) Then
                    g.DisplayKey = segKey
                End If
                g.Usages.Add(New MappingUsage With {
                    .File = SafeStr(r("File")),
                    .PipeTypeName = SafeStr(r("PipeTypeName")),
                    .RuleIndex = SafeIntObj(r("RuleIndex")),
                    .SegmentId = SafeIntObj(r("SegmentId")),
                    .SegmentKey = segKey
                })
            Next

            For Each kv In groups
                Dim g = kv.Value
                Dim fileSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                Dim pipeSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                For Each u In g.Usages
                    fileSet.Add(NormalizePath(u.File))
                    pipeSet.Add(u.File & "|" & u.PipeTypeName)
                Next
                g.FileCount = fileSet.Count
                g.PipeTypeCount = pipeSet.Count
                Dim sb As New StringBuilder()
                sb.Append("Used in: ")
                Dim fileInfo As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
                For Each u In g.Usages
                    Dim key = NormalizePath(u.File)
                    If Not fileInfo.ContainsKey(key) Then
                        fileInfo(key) = 0
                    End If
                    fileInfo(key) += 1
                Next
                Dim first As Boolean = True
                For Each kvp In fileInfo
                    If Not first Then
                        sb.Append(", ")
                    End If
                    first = False
                    sb.Append(Path.GetFileName(kvp.Key))
                    sb.Append("("c)
                    sb.Append(kvp.Value.ToString(CultureInfo.InvariantCulture))
                    sb.Append(" PipeTypes)")
                Next
                g.UsageSummary = sb.ToString()
            Next

            Return New List(Of MappingGroup)(groups.Values)
        End Function

        Public Shared Function SuggestGroupMappings(groups As List(Of MappingGroup), pmsData As List(Of PmsRow)) As List(Of SuggestedMapping)
            Dim result As New List(Of SuggestedMapping)()
            If groups Is Nothing OrElse pmsData Is Nothing Then
                Return result
            End If
            For Each g In groups
                Dim bestScore As Double = -1
                Dim bestClass As String = String.Empty
                Dim bestSeg As String = String.Empty
                For Each p In pmsData
                    Dim normP = NormalizeKey(p.SegmentKey)
                    Dim sim = SimilarityScore(g.NormalizedKey, normP)
                    If sim > bestScore Then
                        bestScore = sim
                        bestClass = p.Class
                        bestSeg = p.SegmentKey
                    End If
                Next
                If bestScore >= 0.4R Then
                    g.SuggestedClass = bestClass
                    g.SuggestedSegmentKey = bestSeg
                    result.Add(New SuggestedMapping With {
                        .File = g.GroupKey,
                        .PipeTypeName = g.DisplayKey,
                        .RuleIndex = 0,
                        .SegmentId = 0,
                        .SegmentKey = g.GroupKey,
                        .PmsClass = bestClass,
                        .PmsSegmentKey = bestSeg,
                        .Score = bestScore
                    })
                End If
            Next
            Return result
        End Function

        Public Shared Function ExpandGroupSelections(groups As List(Of MappingGroup), selections As List(Of GroupSelection)) As List(Of MappingSelection)
            Dim result As New List(Of MappingSelection)()
            If groups Is Nothing OrElse selections Is Nothing Then
                Return result
            End If
            Dim groupDict As New Dictionary(Of String, MappingGroup)(StringComparer.OrdinalIgnoreCase)
            For Each g In groups
                groupDict(g.GroupKey) = g
            Next
            For Each sel In selections
                Dim g As MappingGroup = Nothing
                If Not groupDict.TryGetValue(sel.GroupKey, g) Then
                    Continue For
                End If
                For Each u In g.Usages
                    result.Add(New MappingSelection With {
                        .File = u.File,
                        .PipeTypeName = u.PipeTypeName,
                        .RuleIndex = u.RuleIndex,
                        .SegmentId = u.SegmentId,
                        .SegmentKey = u.SegmentKey,
                        .SelectedClass = sel.SelectedClass,
                        .SelectedPmsSegment = sel.SelectedPmsSegment,
                        .MappingSource = If(String.IsNullOrWhiteSpace(sel.SelectionSource), "Manual", sel.SelectionSource)
                    })
                Next
            Next
            Return result
        End Function

        Public Shared Function RunCompare(extractData As DataSet,
                                          pmsData As List(Of PmsRow),
                                          mappings As List(Of MappingSelection),
                                          options As CompareOptions) As RunResult
            Dim res As New RunResult With {
                .MapTable = BuildMapTable(),
                .RevitSizeTable = BuildRevitSizeTable(),
                .PmsSizeTable = BuildPmsTableSkeleton(),
                .CompareTable = BuildCompareTable(),
                .ErrorTable = BuildErrorTable(),
                .SummaryTable = BuildSummaryTable()
            }

            If extractData Is Nothing OrElse Not extractData.Tables.Contains(TableRules) OrElse Not extractData.Tables.Contains(TableSizes) Then
                AddError(res.ErrorTable, "extract", "추출 데이터가 없습니다.")
                Return res
            End If

            If options Is Nothing Then
                options = New CompareOptions()
            End If

            Dim ndRound As Integer = options.NdRound
            Dim tol As Double = options.TolMm
            Dim doClassMatch As Boolean = options.ClassMatch

            Dim meta = extractData.Tables(TableMeta)
            If meta IsNot Nothing AndAlso meta.Rows.Count > 0 Then
                Dim ndVal = SafeDouble(meta.Rows(0)("NdRound"))
                If ndVal > 0 Then
                    ndRound = CInt(Math.Truncate(ndVal))
                End If
                Dim tolVal = SafeDouble(meta.Rows(0)("Tolerance"))
                If tolVal > 0 Then
                    tol = tolVal
                End If
            End If

            Dim pipeClassMap = BuildPipeTypeClassMap(extractData)
            Dim segmentClassMap = BuildSegmentClassMap(extractData)
            Dim routingClassMap = BuildRoutingClassMap(extractData)

            Dim sizeRows As New List(Of ExtractSizeRow)()
            Dim sizeTable As DataTable = extractData.Tables(TableSizes)
            For Each r As DataRow In sizeTable.Rows
                sizeRows.Add(New ExtractSizeRow With {
                    .File = SafeStr(r("File")),
                    .SegmentId = SafeIntObj(r("SegmentId")),
                    .SegmentKey = SafeStr(r("SegmentKey")),
                    .NdMm = SafeDouble(r("ND_mm")),
                    .IdMm = SafeDouble(r("ID_mm")),
                    .OdMm = SafeDouble(r("OD_mm"))
                })
            Next

            For Each s In sizeRows
                s.NdKey = Math.Round(s.NdMm, ndRound)
            Next

            Dim pmsRows As New List(Of PmsRow)()
            If pmsData IsNot Nothing Then
                For Each p In pmsData
                    pmsRows.Add(New PmsRow With {
                        .Class = p.Class,
                        .SegmentKey = p.SegmentKey,
                        .NdMm = p.NdMm,
                        .IdMm = p.IdMm,
                        .OdMm = p.OdMm
                    })
                Next
            End If

            Dim sizeByKey As New Dictionary(Of Tuple(Of String, String), List(Of ExtractSizeRow))(TupleComparer())
            For Each s In sizeRows
                Dim key = Tuple.Create(NormalizePath(s.File), s.SegmentKey)
                If Not sizeByKey.ContainsKey(key) Then
                    sizeByKey(key) = New List(Of ExtractSizeRow)()
                End If
                sizeByKey(key).Add(s)
            Next

            Dim pmsDict As New Dictionary(Of Tuple(Of String, String), List(Of PmsRow))(TupleComparer())
            For Each p In pmsRows
                Dim key = Tuple.Create(p.Class, p.SegmentKey)
                If Not pmsDict.ContainsKey(key) Then
                    pmsDict(key) = New List(Of PmsRow)()
                End If
                pmsDict(key).Add(p)
            Next

            For Each m In mappings
                Dim pipeKey = Tuple.Create(NormalizePath(m.File), m.PipeTypeName)
                Dim pipeClassRaw As String = GetDictValue(pipeClassMap, pipeKey)
                Dim segmentClassRaw As String = ExtractClassToken(If(GetDictValue(segmentClassMap, m.SegmentKey), m.SegmentKey))
                Dim routingSet As List(Of String) = Nothing
                Dim routingKey = Tuple.Create(NormalizePath(m.File), m.PipeTypeName)
                routingClassMap.TryGetValue(routingKey, routingSet)
                Dim classCheck = EvaluateClassMatch(doClassMatch, pipeClassRaw, segmentClassRaw, routingSet)
                If Not doClassMatch Then
                    pipeClassRaw = String.Empty
                    segmentClassRaw = String.Empty
                    routingSet = Nothing
                End If
                Dim routingSetStr As String = If(doClassMatch, String.Join("|", If(routingSet, New List(Of String)())), String.Empty)

                Dim mapRow = res.MapTable.NewRow()
                mapRow("File") = m.File
                mapRow("PipeTypeName") = m.PipeTypeName
                mapRow("SegmentRuleIndex") = m.RuleIndex
                mapRow("RevitSegmentKey") = m.SegmentKey
                mapRow("Selected_CLASS") = m.SelectedClass
                mapRow("Selected_PMS_SegmentKey") = m.SelectedPmsSegment
                mapRow("MappingSource") = If(String.IsNullOrWhiteSpace(m.MappingSource), "Manual", m.MappingSource)
                res.MapTable.Rows.Add(mapRow)

                Dim revKey = Tuple.Create(NormalizePath(m.File), m.SegmentKey)
                Dim revSizes As List(Of ExtractSizeRow) = Nothing
                sizeByKey.TryGetValue(revKey, revSizes)

                Dim pmsKey = Tuple.Create(m.SelectedClass, m.SelectedPmsSegment)
                Dim pmsSizes As List(Of PmsRow) = Nothing
                pmsDict.TryGetValue(pmsKey, pmsSizes)

                If String.IsNullOrWhiteSpace(m.SelectedPmsSegment) Then
                    AddMissingMappingRows(res.CompareTable, revSizes, m, ndRound, pipeClassRaw, segmentClassRaw, routingSetStr, classCheck.Status, classCheck.Note)
                    Continue For
                End If

                If revSizes Is Nothing OrElse revSizes.Count = 0 Then
                    AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  0, 0, 0, 0, 0, "MissingRevitRow", pipeClassRaw, segmentClassRaw, routingSetStr, classCheck.Status, classCheck.Note)
                    Continue For
                End If

                Dim revByNd As New Dictionary(Of Double, ExtractSizeRow)()
                For Each r In revSizes
                    Dim k = Math.Round(r.NdMm, ndRound)
                    If Not revByNd.ContainsKey(k) Then
                        revByNd(k) = r
                    End If
                Next

                Dim pmsByNd As New Dictionary(Of Double, PmsRow)()
                If pmsSizes IsNot Nothing Then
                    For Each p In pmsSizes
                        Dim k = Math.Round(p.NdMm, ndRound)
                        If Not pmsByNd.ContainsKey(k) Then
                            pmsByNd(k) = p
                        End If
                    Next
                End If

                Dim ndKeys As New HashSet(Of Double)()
                For Each k As Double In revByNd.Keys
                    ndKeys.Add(k)
                Next
                For Each k As Double In pmsByNd.Keys
                    ndKeys.Add(k)
                Next

                If ndKeys.Count = 0 Then
                    AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  0, 0, 0, 0, 0, If(pmsSizes Is Nothing OrElse pmsSizes.Count = 0, "MissingPmsRow", "MissingRevitRow"), pipeClassRaw, segmentClassRaw, routingSetStr, classCheck.Status, classCheck.Note)
                    Continue For
                End If

                For Each k In New List(Of Double)(ndKeys)
                    Dim r As ExtractSizeRow = Nothing
                    revByNd.TryGetValue(k, r)
                    Dim p As PmsRow = Nothing
                    pmsByNd.TryGetValue(k, p)

                    If r Is Nothing AndAlso p IsNot Nothing Then
                        AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                      p.NdMm, 0, 0, p.IdMm, p.OdMm, "MissingRevitRow", pipeClassRaw, segmentClassRaw, routingSetStr, classCheck.Status, classCheck.Note)
                        Continue For
                    End If

                    If r IsNot Nothing AndAlso p Is Nothing Then
                        AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                      r.NdMm, r.IdMm, r.OdMm, 0, 0, "MissingPmsRow", pipeClassRaw, segmentClassRaw, routingSetStr, classCheck.Status, classCheck.Note)
                        Continue For
                    End If

                    Dim idDiff = Math.Abs(r.IdMm - p.IdMm)
                    Dim odDiff = Math.Abs(r.OdMm - p.OdMm)
                    Dim status As String = "OK"
                    If idDiff > tol AndAlso odDiff > tol Then
                        status = "Mismatch"
                    ElseIf idDiff > tol Then
                        status = "MismatchID"
                    ElseIf odDiff > tol Then
                        status = "MismatchOD"
                    End If

                    AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  r.NdMm, r.IdMm, r.OdMm, p.IdMm, p.OdMm, status, pipeClassRaw, segmentClassRaw, routingSetStr, classCheck.Status, classCheck.Note)
                Next
            Next

            For Each s In sizeRows
                Dim row = res.RevitSizeTable.NewRow()
                row("File") = s.File
                row("SegmentId") = s.SegmentId
                row("RevitSegmentKey") = s.SegmentKey
                row("ND_mm") = s.NdMm.ToString("0.###", CultureInfo.InvariantCulture)
                row("ID_mm") = s.IdMm.ToString("0.###", CultureInfo.InvariantCulture)
                row("OD_mm") = s.OdMm.ToString("0.###", CultureInfo.InvariantCulture)
                res.RevitSizeTable.Rows.Add(row)
            Next

            For Each p In pmsRows
                Dim row = res.PmsSizeTable.NewRow()
                row("CLASS") = p.Class
                row("PMS_SegmentKey") = p.SegmentKey
                row("ND_mm") = p.NdMm.ToString("0.###", CultureInfo.InvariantCulture)
                row("ID_mm") = p.IdMm.ToString("0.###", CultureInfo.InvariantCulture)
                row("OD_mm") = p.OdMm.ToString("0.###", CultureInfo.InvariantCulture)
                res.PmsSizeTable.Rows.Add(row)
            Next

            Dim summaryRow = res.SummaryTable.NewRow()
            Dim total As Integer = res.CompareTable.Rows.Count
            summaryRow("Total") = total
            summaryRow("OK") = CountStatus(res.CompareTable, "OK")
            summaryRow("Mismatch") = CountStatus(res.CompareTable, "Mismatch")
            summaryRow("MismatchID") = CountStatus(res.CompareTable, "MismatchID")
            summaryRow("MismatchOD") = CountStatus(res.CompareTable, "MismatchOD")
            summaryRow("MissingMapping") = CountStatus(res.CompareTable, "MissingMapping")
            summaryRow("MissingRevitRow") = CountStatus(res.CompareTable, "MissingRevitRow")
            summaryRow("MissingPmsRow") = CountStatus(res.CompareTable, "MissingPmsRow")
            res.SummaryTable.Rows.Add(summaryRow)

            Return res
        End Function

        ' ---------------------------
        ' Helpers
        ' ---------------------------
        Private Class ClassMatchResult
            Public Property Status As String = String.Empty
            Public Property Note As String = String.Empty
        End Class

        Private Shared Function BuildPipeTypeClassMap(extractData As DataSet) As Dictionary(Of Tuple(Of String, String), String)
            Dim map As New Dictionary(Of Tuple(Of String, String), String)(TupleComparer())
            If extractData Is Nothing OrElse Not extractData.Tables.Contains(TableRules) Then
                Return map
            End If
            Dim rules = extractData.Tables(TableRules)
            For Each r As DataRow In rules.Rows
                Dim key = Tuple.Create(NormalizePath(SafeStr(r("File"))), SafeStr(r("PipeTypeName")))
                If Not map.ContainsKey(key) Then
                    map(key) = ExtractClassToken(SafeStr(r("PipeTypeName")))
                End If
            Next
            Return map
        End Function

        Private Shared Function BuildSegmentClassMap(extractData As DataSet) As Dictionary(Of String, String)
            Dim map As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If extractData Is Nothing OrElse Not extractData.Tables.Contains(TableRules) Then
                Return map
            End If
            Dim rules = extractData.Tables(TableRules)
            For Each r As DataRow In rules.Rows
                Dim segKey = SafeStr(r("SegmentKey"))
                If Not map.ContainsKey(segKey) Then
                    map(segKey) = ExtractClassToken(segKey)
                End If
            Next
            Return map
        End Function

        Private Shared Function BuildRoutingClassMap(extractData As DataSet) As Dictionary(Of Tuple(Of String, String), List(Of String))
            Dim map As New Dictionary(Of Tuple(Of String, String), List(Of String))(TupleComparer())
            If extractData Is Nothing OrElse Not extractData.Tables.Contains(TableRouting) Then
                Return map
            End If
            Dim routing = extractData.Tables(TableRouting)
            For Each r As DataRow In routing.Rows
                Dim key = Tuple.Create(NormalizePath(SafeStr(r("File"))), SafeStr(r("PipeTypeName")))
                Dim rawPart = SafeStr(r("PartName"))
                Dim parts = rawPart.Split(New String() {"|"}, StringSplitOptions.None)
                For Each p In parts
                    Dim cls = ExtractClassToken(p)
                    Dim norm = NormalizeClassToken(cls)
                    If String.IsNullOrWhiteSpace(norm) Then
                        Continue For
                    End If
                    If Not map.ContainsKey(key) Then
                        map(key) = New List(Of String)()
                    End If
                    Dim existingNorms As New HashSet(Of String)(map(key).ConvertAll(Function(x) NormalizeClassToken(x)), StringComparer.OrdinalIgnoreCase)
                    If Not existingNorms.Contains(norm) Then
                        map(key).Add(cls)
                    End If
                Next
            Next
            Return map
        End Function

        Private Shared Function EvaluateClassMatch(doCheck As Boolean, pipeTypeClass As String, segmentClass As String, routingSet As List(Of String)) As ClassMatchResult
            Dim res As New ClassMatchResult()
            If Not doCheck Then
                Return res
            End If

            Dim pipeNorm = NormalizeClassToken(pipeTypeClass)
            Dim segNorm = NormalizeClassToken(segmentClass)
            Dim routingNorms As New List(Of String)()
            Dim routingRaw As New List(Of String)()
            If routingSet IsNot Nothing Then
                For Each r In routingSet
                    Dim norm = NormalizeClassToken(r)
                    If Not String.IsNullOrWhiteSpace(norm) Then
                        routingNorms.Add(norm)
                        routingRaw.Add(r)
                    End If
                Next
            End If

            If String.IsNullOrWhiteSpace(pipeNorm) AndAlso String.IsNullOrWhiteSpace(segNorm) AndAlso routingNorms.Count = 0 Then
                res.Status = "N/A"
                Return res
            End If

            Dim expected = If(Not String.IsNullOrWhiteSpace(segNorm), segNorm, pipeNorm)
            Dim noteParts As New List(Of String)()

            If Not String.IsNullOrWhiteSpace(pipeNorm) AndAlso Not String.IsNullOrWhiteSpace(segNorm) AndAlso Not pipeNorm.Equals(segNorm, StringComparison.OrdinalIgnoreCase) Then
                noteParts.Add(String.Format("PipeType:{0} vs Segment:{1}", pipeTypeClass, segmentClass))
            End If

            If routingNorms.Count > 0 AndAlso Not String.IsNullOrWhiteSpace(expected) Then
                Dim expectedRaw As String = If(String.IsNullOrWhiteSpace(segmentClass), pipeTypeClass, segmentClass)
                For i As Integer = 0 To routingNorms.Count - 1
                    If Not routingNorms(i).Equals(expected, StringComparison.OrdinalIgnoreCase) Then
                        Dim rawVal As String = routingRaw(i)
                        noteParts.Add(String.Format("Routing:{0} vs {1}", rawVal, expectedRaw))
                    End If
                Next
            End If

            If noteParts.Count > 0 Then
                res.Status = "Mismatch"
                res.Note = String.Join("; ", noteParts)
            Else
                res.Status = If(String.IsNullOrWhiteSpace(expected) AndAlso routingNorms.Count = 0, "N/A", "OK")
                res.Note = String.Empty
            End If

            Return res
        End Function

        Private Shared Function ExtractClassToken(text As String) As String
            If String.IsNullOrWhiteSpace(text) Then
                Return String.Empty
            End If

            Dim firstPass As String = String.Empty
            Dim parts = text.Split(","c)
            For Each p In parts
                Dim trimmed = p.Trim()
                If trimmed.IndexOf("("c) >= 0 AndAlso trimmed.IndexOf(")"c) > trimmed.IndexOf("("c) Then
                    firstPass = trimmed
                    Exit For
                End If
            Next

            If String.IsNullOrWhiteSpace(firstPass) Then
                Dim m = Regex.Match(text, "^\s*([A-Za-z0-9]+\(.*?\))")
                If m.Success AndAlso m.Groups.Count > 1 Then
                    firstPass = m.Groups(1).Value.Trim()
                End If
            End If

            If String.IsNullOrWhiteSpace(firstPass) AndAlso parts.Length > 0 Then
                firstPass = parts(0).Trim()
            End If

            Return firstPass
        End Function

        Private Shared Function NormalizeClassToken(token As String) As String
            If String.IsNullOrWhiteSpace(token) Then
                Return String.Empty
            End If
            Dim t = token.Trim().ToUpperInvariant()
            t = t.Replace(" - REF.", String.Empty).Replace("- REF.", String.Empty).Replace("-REF.", String.Empty).Replace(" -REF.", String.Empty)
            While t.Contains("  ")
                t = t.Replace("  ", " ")
            End While
            Return t.Trim()
        End Function

        Private Shared Function GetDictValue(Of TKey, TValue)(dict As Dictionary(Of TKey, TValue), key As TKey) As TValue
            Dim val As TValue = Nothing
            If dict IsNot Nothing AndAlso dict.TryGetValue(key, val) Then
                Return val
            End If
            Return Nothing
        End Function

        Private Shared Function BuildOpenOptions(opts As ExtractOptions, filePath As String) As OpenOptions
            Dim opt As New OpenOptions()
            opt.Audit = False
            opt.AllowOpeningLocalByWrongUser = True
            opt.DetachFromCentralOption = DetachFromCentralOption.DoNotDetach

            If opts IsNot Nothing Then
                opt.DetachFromCentralOption = If(opts.DetachFromCentral, DetachFromCentralOption.DetachAndPreserveWorksets, DetachFromCentralOption.DoNotDetach)
            End If

            ApplyWorksetConfiguration(opt, filePath)

            Return opt
        End Function

        Private Shared Sub ApplyWorksetConfiguration(opt As OpenOptions, filePath As String)
            If opt Is Nothing Then
                Return
            End If

            If String.IsNullOrWhiteSpace(filePath) Then
                Return
            End If

            Try
                Dim fileInfo = BasicFileInfo.Extract(filePath)
                If fileInfo IsNot Nothing AndAlso fileInfo.IsWorkshared Then
                    Dim wsConfig As New WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets)
                    opt.SetOpenWorksetsConfiguration(wsConfig)
                End If
            Catch
                ' 워크셰어링 여부 확인 실패는 무시하고 기본 옵션으로 계속 진행
            End Try
        End Sub

        Private Shared Function BuildMetaTable() As DataTable
            Dim t As New DataTable(TableMeta)
            t.Columns.Add("NdRound", GetType(Integer))
            t.Columns.Add("CreatedAt", GetType(String))
            t.Columns.Add("Tolerance", GetType(Double))
            t.Columns.Add("ToolVersion", GetType(String))
            Return t
        End Function

        Private Shared Function BuildFileTable() As DataTable
            Dim t As New DataTable(TableFiles)
            t.Columns.Add("File", GetType(String))
            t.Columns.Add("FileName", GetType(String))
            t.Columns.Add("ExtractedAt", GetType(String))
            Return t
        End Function

        Private Shared Function BuildRuleTable() As DataTable
            Dim t As New DataTable(TableRules)
            t.Columns.Add("File", GetType(String))
            t.Columns.Add("PipeTypeName", GetType(String))
            t.Columns.Add("RuleIndex", GetType(Integer))
            t.Columns.Add("SegmentId", GetType(Integer))
            t.Columns.Add("SegmentKey", GetType(String))
            Return t
        End Function

        Private Shared Function BuildSizeTable() As DataTable
            Dim t As New DataTable(TableSizes)
            t.Columns.Add("File", GetType(String))
            t.Columns.Add("SegmentId", GetType(Integer))
            t.Columns.Add("SegmentKey", GetType(String))
            t.Columns.Add("ND_mm", GetType(Double))
            t.Columns.Add("ID_mm", GetType(Double))
            t.Columns.Add("OD_mm", GetType(Double))
            Return t
        End Function

        Private Shared Function BuildRoutingTable() As DataTable
            Dim t As New DataTable(TableRouting)
            t.Columns.Add("File", GetType(String))
            t.Columns.Add("PipeTypeName", GetType(String))
            t.Columns.Add("RuleGroup", GetType(String))
            t.Columns.Add("RuleIndex", GetType(Integer))
            t.Columns.Add("RuleType", GetType(String))
            t.Columns.Add("PartId", GetType(Integer))
            t.Columns.Add("PartName", GetType(String))
            Return t
        End Function

        Private Shared Function BuildMapTable() As DataTable
            Dim t As New DataTable("PipeTypeSegmentMap")
            t.Columns.Add("File", GetType(String))
            t.Columns.Add("PipeTypeName", GetType(String))
            t.Columns.Add("SegmentRuleIndex", GetType(Integer))
            t.Columns.Add("RevitSegmentKey", GetType(String))
            t.Columns.Add("Selected_CLASS", GetType(String))
            t.Columns.Add("Selected_PMS_SegmentKey", GetType(String))
            t.Columns.Add("MappingSource", GetType(String))
            Return t
        End Function

        Private Shared Function BuildRevitSizeTable() As DataTable
            Dim t As New DataTable("SegmentSizeRaw_Revit")
            t.Columns.Add("File", GetType(String))
            t.Columns.Add("SegmentId", GetType(Integer))
            t.Columns.Add("RevitSegmentKey", GetType(String))
            t.Columns.Add("ND_mm", GetType(String))
            t.Columns.Add("ID_mm", GetType(String))
            t.Columns.Add("OD_mm", GetType(String))
            Return t
        End Function

        Private Shared Function BuildPmsTableSkeleton() As DataTable
            Dim t As New DataTable("SegmentSizeRaw_PMS")
            t.Columns.Add("CLASS", GetType(String))
            t.Columns.Add("PMS_SegmentKey", GetType(String))
            t.Columns.Add("ND_mm", GetType(String))
            t.Columns.Add("ID_mm", GetType(String))
            t.Columns.Add("OD_mm", GetType(String))
            Return t
        End Function

        Private Shared Function BuildCompareTable() As DataTable
            Dim t As New DataTable("SizeCompare")
            t.Columns.Add("File", GetType(String))
            t.Columns.Add("PipeTypeName", GetType(String))
            t.Columns.Add("SegmentRuleIndex", GetType(Integer))
            t.Columns.Add("RevitSegmentKey", GetType(String))
            t.Columns.Add("CLASS", GetType(String))
            t.Columns.Add("PMS_SegmentKey", GetType(String))
            t.Columns.Add("ND_mm", GetType(String))
            t.Columns.Add("Revit_ID", GetType(String))
            t.Columns.Add("Revit_OD", GetType(String))
            t.Columns.Add("PMS_ID", GetType(String))
            t.Columns.Add("PMS_OD", GetType(String))
            t.Columns.Add("Diff_ID", GetType(String))
            t.Columns.Add("Diff_OD", GetType(String))
            t.Columns.Add("Status", GetType(String))
            t.Columns.Add("PipeTypeClass", GetType(String))
            t.Columns.Add("SegmentClass", GetType(String))
            t.Columns.Add("RoutingClassSet", GetType(String))
            t.Columns.Add("ClassMatchStatus", GetType(String))
            t.Columns.Add("ClassMatchNote", GetType(String))
            Return t
        End Function

        Private Shared Function BuildErrorTable() As DataTable
            Dim t As New DataTable("Error")
            t.Columns.Add("Stage", GetType(String))
            t.Columns.Add("Message", GetType(String))
            t.Columns.Add("ExceptionSummary", GetType(String))
            Return t
        End Function

        Private Shared Function BuildSummaryTable() As DataTable
            Dim t As New DataTable("Summary")
            t.Columns.Add("Total", GetType(Integer))
            t.Columns.Add("OK", GetType(Integer))
            t.Columns.Add("Mismatch", GetType(Integer))
            t.Columns.Add("MismatchID", GetType(Integer))
            t.Columns.Add("MismatchOD", GetType(Integer))
            t.Columns.Add("MissingMapping", GetType(Integer))
            t.Columns.Add("MissingRevitRow", GetType(Integer))
            t.Columns.Add("MissingPmsRow", GetType(Integer))
            Return t
        End Function

        Private Shared Sub AddCompareRow(table As DataTable,
                                         file As String,
                                         pipeType As String,
                                         ruleIdx As Integer,
                                         revSeg As String,
                                         cls As String,
                                         pmsSeg As String,
                                         nd As Double,
                                         revId As Double,
                                         revOd As Double,
                                         pmsId As Double,
                                         pmsOd As Double,
                                         status As String,
                                         Optional pipeTypeClass As String = "",
                                         Optional segmentClass As String = "",
                                         Optional routingClassSet As String = "",
                                         Optional classMatchStatus As String = "",
                                         Optional classMatchNote As String = "")
            Dim row = table.NewRow()
            row("File") = file
            row("PipeTypeName") = pipeType
            row("SegmentRuleIndex") = ruleIdx
            row("RevitSegmentKey") = revSeg
            row("CLASS") = cls
            row("PMS_SegmentKey") = pmsSeg
            row("ND_mm") = nd.ToString("0.###", CultureInfo.InvariantCulture)
            row("Revit_ID") = revId.ToString("0.###", CultureInfo.InvariantCulture)
            row("Revit_OD") = revOd.ToString("0.###", CultureInfo.InvariantCulture)
            row("PMS_ID") = pmsId.ToString("0.###", CultureInfo.InvariantCulture)
            row("PMS_OD") = pmsOd.ToString("0.###", CultureInfo.InvariantCulture)
            row("Diff_ID") = (revId - pmsId).ToString("0.###", CultureInfo.InvariantCulture)
            row("Diff_OD") = (revOd - pmsOd).ToString("0.###", CultureInfo.InvariantCulture)
            row("Status") = status
            row("PipeTypeClass") = pipeTypeClass
            row("SegmentClass") = segmentClass
            row("RoutingClassSet") = routingClassSet
            row("ClassMatchStatus") = classMatchStatus
            row("ClassMatchNote") = classMatchNote
            table.Rows.Add(row)
        End Sub

        Private Shared Sub AddMissingMappingRows(table As DataTable,
                                                 revSizes As List(Of ExtractSizeRow),
                                                 m As MappingSelection,
                                                 ndRound As Integer,
                                                 Optional pipeTypeClass As String = "",
                                                 Optional segmentClass As String = "",
                                                 Optional routingClassSet As String = "",
                                                 Optional classMatchStatus As String = "",
                                                 Optional classMatchNote As String = "")
            If revSizes IsNot Nothing AndAlso revSizes.Count > 0 Then
                For Each r In revSizes
                    AddCompareRow(table, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  Math.Round(r.NdMm, ndRound), r.IdMm, r.OdMm, 0, 0, "MissingMapping", pipeTypeClass, segmentClass, routingClassSet, classMatchStatus, classMatchNote)
                Next
            Else
                AddCompareRow(table, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                              0, 0, 0, 0, 0, "MissingMapping", pipeTypeClass, segmentClass, routingClassSet, classMatchStatus, classMatchNote)
            End If
        End Sub

        Private Shared Function DetectHeader(sh As ISheet) As Dictionary(Of String, Integer)
            Dim map As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Dim head As IRow = sh.GetRow(0)
            If head Is Nothing Then
                Return map
            End If
            For i As Integer = 0 To head.LastCellNum - 1
                Dim name = CellStr(head, i).Trim()
                Dim key = NormalizeHeader(name)
                If Not String.IsNullOrEmpty(key) AndAlso Not map.ContainsKey(key) Then
                    map(key) = i
                End If
            Next
            Return map
        End Function

        Private Shared Function NormalizeHeader(name As String) As String
            Dim n = (If(name, String.Empty)).Trim()
            If String.IsNullOrEmpty(n) Then
                Return String.Empty
            End If
            Select Case n.ToLowerInvariant()
                Case "class", "discipline", "trade"
                    Return "class"
                Case "segment", "segmentkey", "segmentname", "segment key", "segment_name", "pms_segment", "seg_pms", "seg", "segkey"
                    Return "segment"
                Case "nd", "nominaldiameter", "nominal", "nominal diameter", "nd_mm", "nd_in"
                    Return "nd"
                Case "id", "innerdiameter", "inner", "inner diameter", "id_mm", "id_in"
                    Return "id"
                Case "od", "outerdiameter", "outer", "outer diameter", "od_mm", "od_in"
                    Return "od"
                Case Else
                    Return String.Empty
            End Select
        End Function

        Private Shared Function CollectPipeTypeSegmentCandidates(doc As RvtDB.Document, filePath As String) As List(Of PreparePipeInfo)
            Dim result As New List(Of PreparePipeInfo)()
            If doc Is Nothing Then
                Return result
            End If

            Dim typesCol As New FilteredElementCollector(doc)
            typesCol.OfClass(GetType(PipeType))
            Dim byPipe As New Dictionary(Of String, List(Of PrepareRow))(StringComparer.OrdinalIgnoreCase)

            For Each el As RvtDB.Element In typesCol
                Dim pt As PipeType = TryCast(el, PipeType)
                If pt Is Nothing Then
                    Continue For
                End If
                Dim rpm = pt.RoutingPreferenceManager
                If rpm Is Nothing Then
                    Continue For
                End If

                Dim count = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments)
                For idx As Integer = 0 To count - 1
                    Dim rule = rpm.GetRule(RoutingPreferenceRuleGroupType.Segments, idx)
                    If rule Is Nothing Then
                        Continue For
                    End If
                    Dim segId = rule.MEPPartId
                    Dim segName = ToSegmentKey(doc, segId)
                    Dim key = pt.Name
                    If Not byPipe.ContainsKey(key) Then
                        byPipe(key) = New List(Of PrepareRow)()
                    End If
                    byPipe(key).Add(New PrepareRow With {
                        .File = filePath,
                        .PipeTypeName = pt.Name,
                        .RuleIndex = idx,
                        .SegmentId = segId.IntegerValue,
                        .SegmentName = segName,
                        .SegmentKey = segName
                    })
                Next
            Next

            For Each kv In byPipe
                Dim list = kv.Value
                list.Sort(Function(a, b) a.RuleIndex.CompareTo(b.RuleIndex))
                Dim defaultIdx As Integer = If(list.Count > 0, list(0).RuleIndex, 0)
                result.Add(New PreparePipeInfo With {
                    .File = filePath,
                    .PipeTypeName = kv.Key,
                    .Candidates = list,
                    .DefaultRuleIndex = defaultIdx
                })
            Next

            Return result
        End Function

        Private Shared Function CollectSegmentSizes(doc As RvtDB.Document, segIds As IEnumerable(Of Integer), filePath As String, ndRound As Integer) As List(Of ExtractSizeRow)
            Dim res As New List(Of ExtractSizeRow)()
            If doc Is Nothing Then
                Return res
            End If
            Dim cache As New Dictionary(Of Integer, PipeSegment)()

            For Each id In segIds
                Dim eid As New ElementId(id)
                Dim seg As PipeSegment = Nothing
                If Not cache.TryGetValue(id, seg) Then
                    seg = TryCast(doc.GetElement(eid), PipeSegment)
                    cache(id) = seg
                End If
                If seg Is Nothing Then
                    Continue For
                End If

                Dim sizes = seg.GetSizes()
                If sizes Is Nothing Then
                    Continue For
                End If

                For Each sz In sizes
                    Dim ndMm = sz.NominalDiameter * FeetToMm
                    Dim idMm = sz.InnerDiameter * FeetToMm
                    Dim odMm = sz.OuterDiameter * FeetToMm
                    res.Add(New ExtractSizeRow With {
                        .File = filePath,
                        .SegmentId = id,
                        .SegmentKey = ToSegmentKey(doc, eid),
                        .NdMm = ndMm,
                        .IdMm = idMm,
                        .OdMm = odMm,
                        .NdKey = Math.Round(ndMm, ndRound)
                    })
                Next
            Next
            Return res
        End Function

        Private Shared Function CollectRouting(doc As RvtDB.Document, filePath As String) As List(Of RoutingRow)
            Dim res As New List(Of RoutingRow)()
            If doc Is Nothing Then
                Return res
            End If

            Dim col As New FilteredElementCollector(doc)
            col.OfClass(GetType(PipeType))
            For Each el As RvtDB.Element In col
                Dim pt As PipeType = TryCast(el, PipeType)
                If pt Is Nothing Then
                    Continue For
                End If
                Dim rpm = pt.RoutingPreferenceManager
                If rpm Is Nothing Then
                    Continue For
                End If

                For Each obj As Object In [Enum].GetValues(GetType(RoutingPreferenceRuleGroupType))
                    Dim group = CType(obj, RoutingPreferenceRuleGroupType)
                    Dim count = rpm.GetNumberOfRules(group)
                    For i As Integer = 0 To count - 1
                        Dim rule = rpm.GetRule(group, i)
                        If rule Is Nothing Then
                            Continue For
                        End If
                        Dim partId = rule.MEPPartId
                        Dim partName = ToSegmentKey(doc, partId)
                        res.Add(New RoutingRow With {
                            .File = filePath,
                            .PipeTypeName = pt.Name,
                            .RuleGroup = group.ToString(),
                            .RuleIndex = i,
                            .RuleType = rule.GetType().Name,
                            .PartId = partId.IntegerValue,
                            .PartName = partName
                        })
                    Next
                Next
            Next

            Return res
        End Function

        Private Class RoutingRow
            Public Property [File] As String = String.Empty
            Public Property PipeTypeName As String = String.Empty
            Public Property RuleGroup As String = String.Empty
            Public Property RuleIndex As Integer
            Public Property RuleType As String = String.Empty
            Public Property PartId As Integer
            Public Property PartName As String = String.Empty
        End Class

        Private Shared Function ToSegmentKey(doc As RvtDB.Document, segId As RvtDB.ElementId) As String
            If doc Is Nothing Then
                Return String.Empty
            End If
            Try
                Dim el As RvtDB.Element = doc.GetElement(segId)
                If el Is Nothing Then
                    Return segId.IntegerValue.ToString(CultureInfo.InvariantCulture)
                End If
                Dim fam As String = String.Empty
                Dim typ As String = String.Empty
                Try
                    Dim revEl As RvtDB.Element = el
                    Dim famParam As RvtDB.Parameter = revEl.LookupParameter("Family")
                    If famParam Is Nothing Then
                        famParam = revEl.Parameter(RvtDB.BuiltInParameter.ALL_MODEL_FAMILY_NAME)
                    End If
                    If famParam IsNot Nothing Then
                        fam = famParam.AsString()
                    End If
                Catch
                End Try
                Try
                    Dim revEl As RvtDB.Element = el
                    Dim typeParam As RvtDB.Parameter = revEl.LookupParameter("Type")
                    If typeParam Is Nothing Then
                        typeParam = revEl.Parameter(RvtDB.BuiltInParameter.ALL_MODEL_TYPE_NAME)
                    End If
                    If typeParam IsNot Nothing Then
                        typ = typeParam.AsString()
                    End If
                Catch
                End Try
                Dim name As String = el.Name
                Dim parts As New List(Of String)()
                If Not String.IsNullOrWhiteSpace(name) Then
                    parts.Add(name)
                End If
                If Not String.IsNullOrWhiteSpace(fam) Then
                    parts.Add(fam)
                End If
                If Not String.IsNullOrWhiteSpace(typ) Then
                    parts.Add(typ)
                End If
                Dim joined = String.Join(" | ", parts)
                If String.IsNullOrWhiteSpace(joined) Then
                    joined = segId.IntegerValue.ToString(CultureInfo.InvariantCulture)
                End If
                Return joined
            Catch
                Return segId.IntegerValue.ToString(CultureInfo.InvariantCulture)
            End Try
        End Function

        Private Shared Function NormalizeKey(key As String) As String
            If String.IsNullOrWhiteSpace(key) Then
                Return String.Empty
            End If
            Dim sb As New StringBuilder()
            Dim upper = key.ToUpperInvariant()
            For Each ch In upper
                If ch = " "c OrElse ch = "-"c OrElse ch = "_"c OrElse ch = ControlChars.Tab OrElse ch = vbLf OrElse ch = vbCr Then
                    Continue For
                End If
                If ch = "("c Then
                    Exit For
                End If
                If Not Char.IsWhiteSpace(ch) Then
                    sb.Append(ch)
                End If
            Next
            Dim compact = sb.ToString().Trim()
            Dim doubleSpaceIdx As Integer = compact.IndexOf("  ", StringComparison.Ordinal)
            While doubleSpaceIdx >= 0
                compact = compact.Replace("  ", " ")
                doubleSpaceIdx = compact.IndexOf("  ", StringComparison.Ordinal)
            End While
            Return compact
        End Function

        Private Shared Function SimilarityScore(a As String, b As String) As Double
            If String.IsNullOrEmpty(a) OrElse String.IsNullOrEmpty(b) Then
                Return 0
            End If
            If String.Equals(a, b, StringComparison.OrdinalIgnoreCase) Then
                Return 1.2R
            End If
            Dim prefixBonus As Double = 0
            Dim minLen As Integer = Math.Min(a.Length, b.Length)
            Dim commonPrefix As Integer = 0
            For i As Integer = 0 To minLen - 1
                If Char.ToUpperInvariant(a(i)) = Char.ToUpperInvariant(b(i)) Then
                    commonPrefix += 1
                Else
                    Exit For
                End If
            Next
            If commonPrefix >= 4 Then
                prefixBonus = 0.3R
            End If
            Dim dist = LevenshteinDistance(a, b)
            Dim maxLen = Math.Max(a.Length, b.Length)
            If maxLen = 0 Then
                Return 0
            End If
            Dim baseScore = 1.0R - (CDbl(dist) / CDbl(maxLen))
            Return baseScore + prefixBonus
        End Function

        Private Shared Function LevenshteinDistance(a As String, b As String) As Integer
            If a Is Nothing Then
                a = String.Empty
            End If
            If b Is Nothing Then
                b = String.Empty
            End If
            Dim n As Integer = a.Length
            Dim m As Integer = b.Length
            Dim d(n, m) As Integer
            For i As Integer = 0 To n
                d(i, 0) = i
            Next
            For j As Integer = 0 To m
                d(0, j) = j
            Next
            For i As Integer = 1 To n
                For j As Integer = 1 To m
                    Dim cost As Integer = If(Char.ToUpperInvariant(a(i - 1)) = Char.ToUpperInvariant(b(j - 1)), 0, 1)
                    d(i, j) = Math.Min(Math.Min(d(i - 1, j) + 1, d(i, j - 1) + 1), d(i - 1, j - 1) + cost)
                Next
            Next
            Return d(n, m)
        End Function

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

        Private Shared Function TupleComparer() As IEqualityComparer(Of Tuple(Of String, String))
            Return New TupleComparerImpl()
        End Function

        Private Shared Function CountStatus(t As DataTable, status As String) As Integer
            If t Is Nothing Then
                Return 0
            End If
            Dim cnt As Integer = 0
            For Each r As DataRow In t.Rows
                Dim s = SafeStr(r("Status"))
                If String.Equals(s, status, StringComparison.OrdinalIgnoreCase) Then
                    cnt += 1
                End If
            Next
            Return cnt
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

        Private Shared Sub AddError(t As DataTable, stage As String, msg As String)
            Dim r = t.NewRow()
            r("Stage") = stage
            r("Message") = msg
            r("ExceptionSummary") = String.Empty
            t.Rows.Add(r)
        End Sub

        Private Class ExtractSizeRow
            Public Property [File] As String = String.Empty
            Public Property SegmentId As Integer
            Public Property SegmentKey As String = String.Empty
            Public Property NdMm As Double
            Public Property IdMm As Double
            Public Property OdMm As Double
            Public Property NdKey As Double
        End Class

        Private Class PrepareRow
            Public Property [File] As String = String.Empty
            Public Property PipeTypeName As String = String.Empty
            Public Property RuleIndex As Integer
            Public Property SegmentId As Integer
            Public Property SegmentName As String = String.Empty
            Public Property SegmentKey As String = String.Empty
        End Class

        Private Class PreparePipeInfo
            Public Property [File] As String = String.Empty
            Public Property PipeTypeName As String = String.Empty
            Public Property Candidates As List(Of PrepareRow)
            Public Property DefaultRuleIndex As Integer
        End Class

        Private Shared Function CellStr(row As IRow, col As Integer) As String
            If row Is Nothing OrElse col < 0 Then
                Return String.Empty
            End If
            Dim cell As ICell = row.GetCell(col)
            If cell Is Nothing Then
                Return String.Empty
            End If
            Try
                Select Case cell.CellType
                    Case NpoiCellType.String
                        Return cell.StringCellValue
                    Case NpoiCellType.Boolean
                        Return cell.BooleanCellValue.ToString(CultureInfo.InvariantCulture)
                    Case NpoiCellType.Numeric
                        Return cell.NumericCellValue.ToString("0.###", CultureInfo.InvariantCulture)
                    Case NpoiCellType.Formula
                        If cell.CachedFormulaResultType = NpoiCellType.Numeric Then
                            Return cell.NumericCellValue.ToString("0.###", CultureInfo.InvariantCulture)
                        End If
                        If cell.CachedFormulaResultType = NpoiCellType.String Then
                            Return cell.StringCellValue
                        End If
                    Case Else
                        Return cell.ToString()
                End Select
            Catch
                Return String.Empty
            End Try
            Return String.Empty
        End Function

        Private Shared Function CellDbl(row As IRow, col As Integer, Optional def As Double = Double.NaN) As Double
            If row Is Nothing OrElse col < 0 Then
                Return def
            End If
            Dim cell As ICell = row.GetCell(col)
            If cell Is Nothing Then
                Return def
            End If
            Try
                Select Case cell.CellType
                    Case NpoiCellType.Numeric
                        Return cell.NumericCellValue
                    Case NpoiCellType.String
                        Dim txt = cell.StringCellValue
                        Dim v As Double
                        If Double.TryParse(txt, NumberStyles.Any, CultureInfo.InvariantCulture, v) Then
                            Return v
                        End If
                    Case NpoiCellType.Formula
                        If cell.CachedFormulaResultType = NpoiCellType.Numeric Then
                            Return cell.NumericCellValue
                        End If
                        If cell.CachedFormulaResultType = NpoiCellType.String Then
                            Dim txt = cell.StringCellValue
                            Dim v As Double
                            If Double.TryParse(txt, NumberStyles.Any, CultureInfo.InvariantCulture, v) Then
                                Return v
                            End If
                        End If
                End Select
            Catch
            End Try
            Return def
        End Function

        Private Shared Function SafeStr(o As Object) As String
            If o Is Nothing OrElse o Is DBNull.Value Then
                Return String.Empty
            End If
            Return o.ToString().Trim()
        End Function

        Private Shared Function SafeDouble(o As Object) As Double
            If o Is Nothing OrElse o Is DBNull.Value Then
                Return 0
            End If
            Dim d As Double
            If Double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, d) Then
                Return d
            End If
            Return 0
        End Function

        Private Shared Function SafeIntObj(o As Object) As Integer
            If o Is Nothing OrElse o Is DBNull.Value Then
                Return 0
            End If
            Dim v As Integer
            If Integer.TryParse(o.ToString(), v) Then
                Return v
            End If
            Dim dbl As Double
            If Double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, dbl) Then
                Return CInt(Math.Truncate(dbl))
            End If
            Return 0
        End Function

        Private Shared Sub EnsureSchema(ds As DataSet)
            If Not ds.Tables.Contains(TableMeta) Then
                ds.Tables.Add(BuildMetaTable())
            End If
            If Not ds.Tables.Contains(TableFiles) Then
                ds.Tables.Add(BuildFileTable())
            End If
            If Not ds.Tables.Contains(TableRules) Then
                ds.Tables.Add(BuildRuleTable())
            End If
            If Not ds.Tables.Contains(TableSizes) Then
                ds.Tables.Add(BuildSizeTable())
            End If
            If Not ds.Tables.Contains(TableRouting) Then
                ds.Tables.Add(BuildRoutingTable())
            End If
        End Sub

        Private Shared Sub WriteSheet(wb As IWorkbook, name As String, t As DataTable)
            If t Is Nothing Then
                Return
            End If
            Dim sh = wb.CreateSheet(name)
            Dim head = sh.CreateRow(0)
            For ci As Integer = 0 To t.Columns.Count - 1
                head.CreateCell(ci).SetCellValue(t.Columns(ci).ColumnName)
            Next
            Dim r As Integer = 1
            For Each row As DataRow In t.Rows
                Dim rr = sh.CreateRow(r)
                For ci As Integer = 0 To t.Columns.Count - 1
                    Dim v As Object = row(ci)
                    rr.CreateCell(ci).SetCellValue(If(v, String.Empty).ToString())
                Next
                r += 1
            Next
        End Sub

    End Class

End Namespace
