Option Explicit On
Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.Globalization
Imports System.IO
Imports System.Text
Imports Autodesk.Revit.DB
Imports Autodesk.Revit.DB.Plumbing
Imports Autodesk.Revit.UI
Imports NPOI.SS.UserModel
Imports NPOI.XSSF.UserModel
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

        Public Class ExtractOptions
            Public Property NdRound As Integer = 3
            Public Property DetachFromCentral As Boolean = True
            Public Property OpenReadOnly As Boolean = True
        End Class

        Public Class CompareOptions
            Public Property NdRound As Integer = 3
            Public Property TolMm As Double = 0.01R
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

        Public Class RunResult
            Public Property MapTable As DataTable
            Public Property RevitSizeTable As DataTable
            Public Property PmsSizeTable As DataTable
            Public Property CompareTable As DataTable
            Public Property ErrorTable As DataTable
        End Class

        Public Class LoadPmsResult
            Public Property Table As DataTable
            Public Property Rows As List(Of PmsRow)
            Public Property Errors As List(Of String)
        End Class

        Public Const TableRules As String = "PipeType_SegmentRules"
        Public Const TableSizes As String = "SegmentSizes"
        Public Const TableMeta As String = "Meta"
        Public Const TableRouting As String = "RoutingPreferences"
        Private Const FeetToMm As Double = 304.8R

        ' ---------------------------
        ' Extract stage
        ' ---------------------------
        Public Shared Function ExtractToDataSet(app As UIApplication, files As IEnumerable(Of String), options As ExtractOptions) As DataSet
            Dim ds As New DataSet()
            Dim meta = BuildMetaTable()
            Dim rules = BuildRuleTable()
            Dim sizes = BuildSizeTable()
            Dim routing = BuildRoutingTable()
            ds.Tables.Add(meta)
            ds.Tables.Add(rules)
            ds.Tables.Add(sizes)
            ds.Tables.Add(routing)

            Dim metaRow = meta.NewRow()
            metaRow("NdRound") = options.NdRound
            metaRow("CreatedAt") = DateTime.Now.ToString("s", CultureInfo.InvariantCulture)
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
                Dim doc As Document = Nothing
                Try
                    Dim opt = BuildOpenOptions(options)
                    Dim mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(p)
                    doc = appObj.OpenDocumentFile(mp, opt)

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
                Dim wb As New XSSFWorkbook(fs)
                For i As Integer = 0 To wb.NumberOfSheets - 1
                    Dim sh = wb.GetSheetAt(i)
                    If sh Is Nothing Then
                        Continue For
                    End If
                    Dim t As New DataTable(sh.SheetName)
                    Dim head = sh.GetRow(0)
                    If head Is Nothing Then
                        Continue For
                    End If
                    For ci As Integer = 0 To head.LastCellNum - 1
                        t.Columns.Add(SafeStr(head.GetCell(ci)), GetType(String))
                    Next
                    For r As Integer = 1 To sh.LastRowNum
                        Dim row = sh.GetRow(r)
                        If row Is Nothing Then
                            Continue For
                        End If
                        Dim dr = t.NewRow()
                        For ci As Integer = 0 To t.Columns.Count - 1
                            dr(ci) = SafeStr(row.GetCell(ci))
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
                Dim row = sh.GetRow(i)
                If row Is Nothing Then
                    Continue For
                End If
                Dim cls As String = SafeStr(row.GetCell(headerMap("class")))
                Dim seg As String = SafeStr(row.GetCell(headerMap("segment")))
                Dim nd As Double = SafeDbl(row.GetCell(headerMap("nd")))
                Dim id As Double = SafeDbl(row.GetCell(headerMap("id")))
                Dim od As Double = SafeDbl(row.GetCell(headerMap("od")))

                If String.IsNullOrWhiteSpace(seg) Then
                    Continue For
                End If

                Dim ndMm As Double = nd
                Dim idMm As Double = id
                Dim odMm As Double = od

                If unitLabel.Contains("in") Then
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

        Public Shared Function RunCompare(extractData As DataSet,
                                          pmsData As List(Of PmsRow),
                                          mappings As List(Of MappingSelection),
                                          options As CompareOptions) As RunResult
            Dim res As New RunResult With {
                .MapTable = BuildMapTable(),
                .RevitSizeTable = BuildRevitSizeTable(),
                .PmsSizeTable = BuildPmsTableSkeleton(),
                .CompareTable = BuildCompareTable(),
                .ErrorTable = BuildErrorTable()
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

            Dim meta = extractData.Tables(TableMeta)
            If meta IsNot Nothing AndAlso meta.Rows.Count > 0 Then
                Dim ndVal = SafeDouble(meta.Rows(0)("NdRound"))
                If ndVal > 0 Then
                    ndRound = CInt(Math.Truncate(ndVal))
                End If
            End If

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

            Dim uniqueMapKeys As New HashSet(Of Tuple(Of String, String))(TupleComparer())
            For Each m In mappings
                Dim mk = Tuple.Create(NormalizePath(m.File), m.PipeTypeName)
                If uniqueMapKeys.Contains(mk) Then
                    Continue For
                End If
                uniqueMapKeys.Add(mk)

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
                    AddMissingMappingRows(res.CompareTable, revSizes, m, ndRound)
                    Continue For
                End If

                If revSizes Is Nothing OrElse revSizes.Count = 0 Then
                    AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  0, 0, 0, 0, 0, "MissingRevitRow")
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
                                  0, 0, 0, 0, 0, If(pmsSizes Is Nothing OrElse pmsSizes.Count = 0, "MissingPmsRow", "MissingRevitRow"))
                    Continue For
                End If

                For Each k In New List(Of Double)(ndKeys)
                    Dim r As ExtractSizeRow = Nothing
                    revByNd.TryGetValue(k, r)
                    Dim p As PmsRow = Nothing
                    pmsByNd.TryGetValue(k, p)

                    If r Is Nothing AndAlso p IsNot Nothing Then
                        AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                      p.NdMm, 0, 0, p.IdMm, p.OdMm, "MissingRevitRow")
                        Continue For
                    End If

                    If r IsNot Nothing AndAlso p Is Nothing Then
                        AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                      r.NdMm, r.IdMm, r.OdMm, 0, 0, "MissingPmsRow")
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
                                  r.NdMm, r.IdMm, r.OdMm, p.IdMm, p.OdMm, status)
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

            Return res
        End Function

        ' ---------------------------
        ' Helpers
        ' ---------------------------
        Private Shared Function BuildOpenOptions(opts As ExtractOptions) As OpenOptions
            Dim opt As New OpenOptions()
            If opts Is Nothing Then
                opt.DetachFromCentralOption = DetachFromCentralOption.DoNotDetach
                opt.Audit = False
                opt.AllowOpeningLocalByWrongUser = True
                Return opt
            End If

            opt.DetachFromCentralOption = If(opts.DetachFromCentral, DetachFromCentralOption.DetachAndPreserveWorksets, DetachFromCentralOption.DoNotDetach)
            opt.Audit = False
            opt.AllowOpeningLocalByWrongUser = True
            Return opt
        End Function

        Private Shared Function BuildMetaTable() As DataTable
            Dim t As New DataTable(TableMeta)
            t.Columns.Add("NdRound", GetType(Integer))
            t.Columns.Add("CreatedAt", GetType(String))
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
            Return t
        End Function

        Private Shared Function BuildErrorTable() As DataTable
            Dim t As New DataTable("Error")
            t.Columns.Add("Stage", GetType(String))
            t.Columns.Add("Message", GetType(String))
            t.Columns.Add("ExceptionSummary", GetType(String))
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
                                         status As String)
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
            table.Rows.Add(row)
        End Sub

        Private Shared Sub AddMissingMappingRows(table As DataTable, revSizes As List(Of ExtractSizeRow), m As MappingSelection, ndRound As Integer)
            If revSizes IsNot Nothing AndAlso revSizes.Count > 0 Then
                For Each r In revSizes
                    AddCompareRow(table, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  Math.Round(r.NdMm, ndRound), r.IdMm, r.OdMm, 0, 0, "MissingMapping")
                Next
            Else
                AddCompareRow(table, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                              0, 0, 0, 0, 0, "MissingMapping")
            End If
        End Sub

        Private Shared Function DetectHeader(sh As ISheet) As Dictionary(Of String, Integer)
            Dim map As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Dim head = sh.GetRow(0)
            If head Is Nothing Then
                Return map
            End If
            For i As Integer = 0 To head.LastCellNum - 1
                Dim name = SafeStr(head.GetCell(i)).Trim()
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
                Case "segment", "segmentkey", "segmentname", "pms_segment", "seg_pms"
                    Return "segment"
                Case "nd", "nominaldiameter", "nd_mm", "nd_in"
                    Return "nd"
                Case "id", "innerdiameter", "id_mm", "id_in"
                    Return "id"
                Case "od", "outerdiameter", "od_mm", "od_in"
                    Return "od"
                Case Else
                    Return String.Empty
            End Select
        End Function

        Private Shared Function CollectPipeTypeSegmentCandidates(doc As Document, filePath As String) As List(Of PreparePipeInfo)
            Dim result As New List(Of PreparePipeInfo)()
            If doc Is Nothing Then
                Return result
            End If

            Dim typesCol As New FilteredElementCollector(doc)
            typesCol.OfClass(GetType(PipeType))
            Dim byPipe As New Dictionary(Of String, List(Of PrepareRow))(StringComparer.OrdinalIgnoreCase)

            For Each el As Element In typesCol
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

        Private Shared Function CollectSegmentSizes(doc As Document, segIds As IEnumerable(Of Integer), filePath As String, ndRound As Integer) As List(Of ExtractSizeRow)
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

        Private Shared Function CollectRouting(doc As Document, filePath As String) As List(Of RoutingRow)
            Dim res As New List(Of RoutingRow)()
            If doc Is Nothing Then
                Return res
            End If

            Dim col As New FilteredElementCollector(doc)
            col.OfClass(GetType(PipeType))
            For Each el As Element In col
                Dim pt As PipeType = TryCast(el, PipeType)
                If pt Is Nothing Then
                    Continue For
                End If
                Dim rpm = pt.RoutingPreferenceManager
                If rpm Is Nothing Then
                    Continue For
                End If

                For Each group As RoutingPreferenceRuleGroupType In [Enum].GetValues(GetType(RoutingPreferenceRuleGroupType))
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

        Private Shared Function ToSegmentKey(doc As Document, segId As ElementId) As String
            If doc Is Nothing Then
                Return String.Empty
            End If
            Try
                Dim el As Element = doc.GetElement(segId)
                If el Is Nothing Then
                    Return segId.IntegerValue.ToString(CultureInfo.InvariantCulture)
                End If
                Dim fam As String = String.Empty
                Dim typ As String = String.Empty
                Try
                    Dim famParam As Parameter = el.LookupParameter("Family")
                    If famParam Is Nothing Then
                        famParam = el.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME)
                    End If
                    If famParam IsNot Nothing Then
                        fam = famParam.AsString()
                    End If
                Catch
                End Try
                Try
                    Dim typeParam As Parameter = el.LookupParameter("Type")
                    If typeParam Is Nothing Then
                        typeParam = el.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME)
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
            While compact.Contains("  ")
                compact = compact.Replace("  ", " ")
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

        Private Shared Function SafeStr(cell As ICell) As String
            If cell Is Nothing Then
                Return String.Empty
            End If
            Try
                If cell.CellType = NpoiCellType.String Then
                    Return cell.StringCellValue
                End If
                Return cell.ToString()
            Catch
                Return String.Empty
            End Try
        End Function

        Private Shared Function SafeDbl(c As ICell) As Double
            If c Is Nothing Then
                Return 0
            End If
            Try
                If c.CellType = NpoiCellType.Numeric Then
                    Return c.NumericCellValue
                End If
                If c.CellType = NpoiCellType.String Then
                    Dim txt = c.StringCellValue
                    Dim v As Double
                    If Double.TryParse(txt, NumberStyles.Any, CultureInfo.InvariantCulture, v) Then
                        Return v
                    End If
                End If
                If c.CellType = NpoiCellType.Formula Then
                    If c.CachedFormulaResultType = NpoiCellType.Numeric Then
                        Return c.NumericCellValue
                    End If
                End If
            Catch
            End Try
            Return 0
        End Function

        Private Shared Function SafeDouble(o As Object) As Double
            If o Is Nothing Then
                Return 0
            End If
            Dim d As Double
            If Double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, d) Then
                Return d
            End If
            Return 0
        End Function

        Private Shared Function SafeIntObj(o As Object) As Integer
            If o Is Nothing Then
                Return 0
            End If
            Dim v As Integer
            If Integer.TryParse(o.ToString(), v) Then
                Return v
            End If
            Return 0
        End Function

        Private Shared Sub EnsureSchema(ds As DataSet)
            If Not ds.Tables.Contains(TableMeta) Then
                ds.Tables.Add(BuildMetaTable())
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
