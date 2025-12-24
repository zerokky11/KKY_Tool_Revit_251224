Option Explicit On
Option Strict On

Imports System
Imports System.Collections.Generic
Imports System.Data
Imports System.Globalization
Imports System.IO
Imports System.Linq
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

        Public Class MappingRequest
            Public Property [File] As String = String.Empty
            Public Property PipeTypeName As String = String.Empty
            Public Property RuleIndex As Integer
            Public Property SegmentId As Integer
            Public Property SegmentKey As String = String.Empty
            Public Property SelectedClass As String = String.Empty
            Public Property SelectedPmsSegment As String = String.Empty
            Public Property MappingSource As String = String.Empty
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
        Private Const FeetToMm As Double = 304.8R

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
            If res.Errors.Count > 0 Then Return res

            Dim unitLabel As String = If(String.IsNullOrWhiteSpace(preferredUnit), "mm", preferredUnit).ToLowerInvariant()

            Dim lastRow As Integer = sh.LastRowNum
            For i As Integer = 1 To lastRow
                Dim row = sh.GetRow(i)
                If row Is Nothing Then Continue For
                Dim cls As String = SafeStr(row.GetCell(headerMap("class")))
                Dim seg As String = SafeStr(row.GetCell(headerMap("segment")))
                Dim nd As Double = SafeDbl(row.GetCell(headerMap("nd")))
                Dim id As Double = SafeDbl(row.GetCell(headerMap("id")))
                Dim od As Double = SafeDbl(row.GetCell(headerMap("od")))

                If String.IsNullOrWhiteSpace(seg) Then Continue For

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

        Public Shared Function ExtractToDataSet(app As UIApplication, files As IEnumerable(Of String), ndRound As Integer) As DataSet
            Dim ds As New DataSet()
            Dim meta = BuildMetaTable()
            Dim rules = BuildRuleTable()
            Dim sizes = BuildSizeTable()
            ds.Tables.Add(meta)
            ds.Tables.Add(rules)
            ds.Tables.Add(sizes)

            Dim metaRow = meta.NewRow()
            metaRow("NdRound") = ndRound
            metaRow("CreatedAt") = DateTime.Now.ToString("s")
            meta.Rows.Add(metaRow)

            Dim valid As New List(Of String)()
            If files IsNot Nothing Then
                For Each f In files
                    Dim p = TryCast(f, String)
                    Dim exists As Boolean = False
                    If Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p) Then
                        For Each v In valid
                            If v.Equals(p, StringComparison.OrdinalIgnoreCase) Then
                                exists = True
                                Exit For
                            End If
                        Next
                        If Not exists Then valid.Add(p)
                    End If
                Next
            End If
            If valid.Count = 0 Then Return ds

            Dim appObj = app.Application
            For Each p In valid
                Dim doc As Document = Nothing
                Try
                    Dim opt = BuildOpenOptions()
                    Dim mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(p)
                    doc = appObj.OpenDocumentFile(mp, opt)

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

                    Dim sizeRows = CollectSegmentSizes(doc, segIds, p, ndRound)
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
                Catch ex As Exception
                    ' 누적 오류는 ErrorTable 대신 호출 측에서 summary/toast로 처리
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

        Public Shared Sub SaveExtractXlsx(ds As DataSet, path As String)
            If ds Is Nothing Then Return
            Dim wb As IWorkbook = New XSSFWorkbook()
            If ds.Tables.Contains(TableMeta) Then WriteSheet(wb, TableMeta, ds.Tables(TableMeta))
            If ds.Tables.Contains(TableRules) Then WriteSheet(wb, TableRules, ds.Tables(TableRules))
            If ds.Tables.Contains(TableSizes) Then WriteSheet(wb, TableSizes, ds.Tables(TableSizes))
            Using fs As New FileStream(path, FileMode.Create, FileAccess.Write)
                wb.Write(fs)
            End Using
        End Sub

        Public Shared Function LoadExtractXlsx(path As String) As DataSet
            Dim ds As New DataSet()
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return ds

            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Dim wb As New XSSFWorkbook(fs)
                For i As Integer = 0 To wb.NumberOfSheets - 1
                    Dim sh = wb.GetSheetAt(i)
                    If sh Is Nothing Then Continue For
                    Dim t As New DataTable(sh.SheetName)
                    Dim head = sh.GetRow(0)
                    If head Is Nothing Then Continue For
                    For ci = 0 To head.LastCellNum - 1
                        t.Columns.Add(SafeStr(head.GetCell(ci)), GetType(String))
                    Next
                    For r = 1 To sh.LastRowNum
                        Dim row = sh.GetRow(r)
                        If row Is Nothing Then Continue For
                        Dim dr = t.NewRow()
                        For ci = 0 To t.Columns.Count - 1
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

        Public Shared Function RunUsingExtract(extract As DataSet,
                                               pmsTable As DataTable,
                                               mappings As List(Of MappingRequest),
                                               tolMm As Double,
                                               ndRound As Integer) As RunResult
            Dim res As New RunResult With {
                .MapTable = BuildMapTable(),
                .RevitSizeTable = BuildRevitSizeTable(),
                .PmsSizeTable = BuildPmsTableSkeleton(),
                .CompareTable = BuildCompareTable(),
                .ErrorTable = BuildErrorTable()
            }

            If extract Is Nothing OrElse Not extract.Tables.Contains(TableRules) OrElse Not extract.Tables.Contains(TableSizes) Then
                AddError(res.ErrorTable, "extract", "추출 데이터가 없습니다.")
                Return res
            End If

            Dim meta = extract.Tables(TableMeta)
            If meta IsNot Nothing AndAlso meta.Rows.Count > 0 Then
                Dim ndVal = SafeDouble(meta.Rows(0)("NdRound"))
                If ndVal > 0 Then ndRound = CInt(Math.Truncate(ndVal))
            End If

            Dim sizeRows As New List(Of ExtractSizeRow)()
            Dim sizeTable As DataTable = Nothing
            If extract.Tables.Contains(TableSizes) Then
                sizeTable = extract.Tables(TableSizes)
            End If
            If sizeTable IsNot Nothing Then
                For Each r As DataRow In sizeTable.Rows
                    sizeRows.Add(New ExtractSizeRow With {
                        .File = SafeStr(r("File")),
                        .SegmentId = SafeInt(r("SegmentId")),
                        .SegmentKey = SafeStr(r("SegmentKey")),
                        .NdMm = SafeDouble(r("ND_mm")),
                        .IdMm = SafeDouble(r("ID_mm")),
                        .OdMm = SafeDouble(r("OD_mm"))
                    })
                Next
            End If
            For Each s In sizeRows
                s.NdKey = Math.Round(s.NdMm, ndRound)
            Next

            Dim pmsRows As New List(Of PmsRow)()
            If pmsTable IsNot Nothing Then
                For Each r As DataRow In pmsTable.Rows
                    pmsRows.Add(New PmsRow With {
                        .Class = If(r("CLASS"), String.Empty).ToString(),
                        .SegmentKey = If(r("PMS_SegmentKey"), String.Empty).ToString(),
                        .NdMm = SafeDouble(r("ND_mm")),
                        .IdMm = SafeDouble(r("ID_mm")),
                        .OdMm = SafeDouble(r("OD_mm"))
                    })
                Next
            End If

            Dim sizeByKey = sizeRows.GroupBy(Function(s) Tuple.Create(NormalizePath(s.File), s.SegmentKey), TupleComparer()) _
                                    .ToDictionary(Function(g) g.Key, Function(g) g.ToList(), TupleComparer())

            Dim pmsDict As New Dictionary(Of Tuple(Of String, String), List(Of PmsRow))(TupleComparer())
            For Each p In pmsRows
                Dim key = Tuple.Create(p.Class, p.SegmentKey)
                If Not pmsDict.ContainsKey(key) Then pmsDict(key) = New List(Of PmsRow)()
                pmsDict(key).Add(p)
            Next

            For Each m In mappings
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
                    If revSizes IsNot Nothing Then
                        For Each r In revSizes
                            AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                          r.NdMm, r.IdMm, r.OdMm, 0, 0, "MissingMapping")
                        Next
                    Else
                        AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                      0, 0, 0, 0, 0, "MissingMapping")
                    End If
                    Continue For
                End If

                If revSizes Is Nothing OrElse revSizes.Count = 0 Then
                    AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  0, 0, 0, 0, 0, "MissingRevitRow")
                    Continue For
                End If

                Dim revByNd = revSizes.GroupBy(Function(r) Math.Round(r.NdMm, ndRound)).ToDictionary(Function(g) g.Key, Function(g) g.First())
                Dim pmsByNd As New Dictionary(Of Double, PmsRow)()
                If pmsSizes IsNot Nothing Then
                    For Each p In pmsSizes
                        Dim k = Math.Round(p.NdMm, ndRound)
                        If Not pmsByNd.ContainsKey(k) Then pmsByNd(k) = p
                    Next
                End If

                Dim ndKeys As New HashSet(Of Double)(revByNd.Keys)
                For Each k In pmsByNd.Keys
                    ndKeys.Add(k)
                Next

                If ndKeys.Count = 0 Then
                    AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  0, 0, 0, 0, 0, If(pmsSizes Is Nothing OrElse pmsSizes.Count = 0, "MissingPmsRow", "MissingRevitRow"))
                    Continue For
                End If

                For Each k In ndKeys.OrderBy(Function(x) x)
                    Dim r As ExtractSizeRow = Nothing
                    revByNd.TryGetValue(k, r)
                    Dim p As PmsRow = Nothing
                    pmsByNd.TryGetValue(k, p)

                    If r Is Nothing AndAlso p IsNot Nothing Then
                        AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                      p.NdMm, 0, 0, p.IdMm, p.OdMm, "ExtraInPms")
                        Continue For
                    End If

                    If r IsNot Nothing AndAlso p Is Nothing Then
                        AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                      r.NdMm, r.IdMm, r.OdMm, 0, 0, "MissingPmsRow")
                        Continue For
                    End If

                    Dim idDiff = Math.Abs((If(r, New ExtractSizeRow()).IdMm) - (If(p, New PmsRow()).IdMm))
                    Dim odDiff = Math.Abs((If(r, New ExtractSizeRow()).OdMm) - (If(p, New PmsRow()).OdMm))
                    Dim status As String = "OK"
                    If idDiff > tolMm AndAlso odDiff > tolMm Then
                        status = "Mismatch"
                    ElseIf idDiff > tolMm Then
                        status = "MismatchID"
                    ElseIf odDiff > tolMm Then
                        status = "MismatchOD"
                    End If

                    AddCompareRow(res.CompareTable, m.File, m.PipeTypeName, m.RuleIndex, m.SegmentKey, m.SelectedClass, m.SelectedPmsSegment,
                                  If(r, New ExtractSizeRow()).NdMm, If(r, New ExtractSizeRow()).IdMm, If(r, New ExtractSizeRow()).OdMm,
                                  If(p, New PmsRow()).IdMm, If(p, New PmsRow()).OdMm, status)
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

            If pmsRows IsNot Nothing Then
                For Each p In pmsRows
                    Dim row = res.PmsSizeTable.NewRow()
                    row("CLASS") = p.Class
                    row("PMS_SegmentKey") = p.SegmentKey
                    row("ND_mm") = p.NdMm.ToString("0.###", CultureInfo.InvariantCulture)
                    row("ID_mm") = p.IdMm.ToString("0.###", CultureInfo.InvariantCulture)
                    row("OD_mm") = p.OdMm.ToString("0.###", CultureInfo.InvariantCulture)
                    res.PmsSizeTable.Rows.Add(row)
                Next
            End If

            Return res
        End Function

        Private Shared Function BuildOpenOptions() As OpenOptions
            Dim opt As New OpenOptions()
            opt.DetachFromCentralOption = DetachFromCentralOption.DoNotDetach
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

        Private Shared Function DetectHeader(sh As ISheet) As Dictionary(Of String, Integer)
            Dim map As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Dim head = sh.GetRow(0)
            If head Is Nothing Then Return map
            For i As Integer = 0 To head.LastCellNum - 1
                Dim name = SafeStr(head.GetCell(i)).Trim()
                Dim key = NormalizeHeader(name)
                If Not String.IsNullOrEmpty(key) AndAlso Not map.ContainsKey(key) Then map(key) = i
            Next
            Return map
        End Function

        Private Shared Function NormalizeHeader(name As String) As String
            Dim n = (If(name, String.Empty)).Trim()
            If String.IsNullOrEmpty(n) Then Return String.Empty
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
            If doc Is Nothing Then Return result

            Dim typesCol As New FilteredElementCollector(doc)
            typesCol.OfClass(GetType(PipeType))
            Dim byPipe As New Dictionary(Of String, List(Of PrepareRow))(StringComparer.OrdinalIgnoreCase)

            For Each el As Element In typesCol
                Dim pt As PipeType = TryCast(el, PipeType)
                If pt Is Nothing Then Continue For
                Dim rpm = pt.RoutingPreferenceManager
                If rpm Is Nothing Then Continue For

                Dim count = rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments)
                For idx As Integer = 0 To count - 1
                    Dim rule = rpm.GetRule(RoutingPreferenceRuleGroupType.Segments, idx)
                    If rule Is Nothing Then Continue For
                    Dim segId = rule.MEPPartId
                    Dim segName = ToSegmentKey(doc, segId)
                    Dim key = pt.Name
                    If Not byPipe.ContainsKey(key) Then byPipe(key) = New List(Of PrepareRow)()
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
                Dim list = kv.Value.OrderBy(Function(r) r.RuleIndex).ToList()
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
            If doc Is Nothing Then Return res
            Dim cache As New Dictionary(Of Integer, PipeSegment)()

            For Each id In segIds
                Dim eid As New ElementId(id)
                Dim seg As PipeSegment = Nothing
                If Not cache.TryGetValue(id, seg) Then
                    seg = TryCast(doc.GetElement(eid), PipeSegment)
                    cache(id) = seg
                End If
                If seg Is Nothing Then Continue For

                Dim sizes = seg.GetSizes()
                If sizes Is Nothing Then Continue For

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

        Private Shared Function ToSegmentKey(doc As Document, segId As ElementId) As String
            If doc Is Nothing Then Return String.Empty
            Try
                Dim el As Element = doc.GetElement(segId)
                If el Is Nothing Then Return segId.IntegerValue.ToString()
                Dim fam As String = String.Empty
                Dim typ As String = String.Empty
                Try
                    Dim famParam As Parameter = el.LookupParameter("Family")
                    If famParam Is Nothing Then famParam = el.get_Parameter(BuiltInParameter.ALL_MODEL_FAMILY_NAME)
                    If famParam IsNot Nothing Then fam = famParam.AsString()
                Catch
                End Try
                Try
                    Dim typeParam As Parameter = el.LookupParameter("Type")
                    If typeParam Is Nothing Then typeParam = el.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_NAME)
                    If typeParam IsNot Nothing Then typ = typeParam.AsString()
                Catch
                End Try
                Dim name As String = el.Name
                Dim parts As New List(Of String)()
                If Not String.IsNullOrWhiteSpace(name) Then parts.Add(name)
                If Not String.IsNullOrWhiteSpace(fam) Then parts.Add(fam)
                If Not String.IsNullOrWhiteSpace(typ) Then parts.Add(typ)
                Dim joined = String.Join(" | ", parts)
                If String.IsNullOrWhiteSpace(joined) Then joined = segId.IntegerValue.ToString()
                Return joined
            Catch
                Return segId.IntegerValue.ToString()
            End Try
        End Function

        Private Shared Function NormalizePath(p As String) As String
            If String.IsNullOrWhiteSpace(p) Then Return String.Empty
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
                If x Is y Then Return True
                If x Is Nothing OrElse y Is Nothing Then Return False
                Return String.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) AndAlso String.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase)
            End Function

            Public Overloads Function GetHashCode(obj As Tuple(Of String, String)) As Integer Implements IEqualityComparer(Of Tuple(Of String, String)).GetHashCode
                If obj Is Nothing Then Return 0
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

        Private Shared Function SafeStr(cell As ICell) As String
            If cell Is Nothing Then Return String.Empty
            Try
                If cell.CellType = NpoiCellType.String Then Return cell.StringCellValue
                Return cell.ToString()
            Catch
                Return String.Empty
            End Try
        End Function

        Private Shared Function SafeDbl(c As ICell) As Double
            If c Is Nothing Then Return 0
            Try
                If c.CellType = NpoiCellType.Numeric Then Return c.NumericCellValue
                If c.CellType = NpoiCellType.String Then
                    Dim txt = c.StringCellValue
                    Dim v As Double = 0
                    If Double.TryParse(txt, NumberStyles.Any, CultureInfo.InvariantCulture, v) Then Return v
                End If
                If c.CellType = NpoiCellType.Formula Then
                    If c.CachedFormulaResultType = NpoiCellType.Numeric Then Return c.NumericCellValue
                End If
            Catch
            End Try
            Return 0
        End Function

        Private Shared Function SafeDouble(o As Object) As Double
            If o Is Nothing Then Return 0
            Dim d As Double = 0
            Double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, d)
            Return d
        End Function

        Private Shared Function SafeInt(o As Object) As Integer
            If o Is Nothing Then Return 0
            Dim v As Integer = 0
            Integer.TryParse(o.ToString(), v)
            Return v
        End Function

        Private Shared Sub EnsureSchema(ds As DataSet)
            If Not ds.Tables.Contains(TableMeta) Then ds.Tables.Add(BuildMetaTable())
            If Not ds.Tables.Contains(TableRules) Then ds.Tables.Add(BuildRuleTable())
            If Not ds.Tables.Contains(TableSizes) Then ds.Tables.Add(BuildSizeTable())
        End Sub

        Private Shared Sub WriteSheet(wb As IWorkbook, name As String, t As DataTable)
            If t Is Nothing Then Return
            Dim sh = wb.CreateSheet(name)
            Dim head = sh.CreateRow(0)
            For ci = 0 To t.Columns.Count - 1
                head.CreateCell(ci).SetCellValue(t.Columns(ci).ColumnName)
            Next
            Dim r As Integer = 1
            For Each row As DataRow In t.Rows
                Dim rr = sh.CreateRow(r)
                For ci = 0 To t.Columns.Count - 1
                    rr.CreateCell(ci).SetCellValue(If(row(ci), String.Empty).ToString())
                Next
                r += 1
            Next
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

    End Class

End Namespace
