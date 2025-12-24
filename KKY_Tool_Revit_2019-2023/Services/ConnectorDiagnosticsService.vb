Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Autodesk.Revit.DB
Imports Autodesk.Revit.DB.Mechanical
Imports Autodesk.Revit.DB.Plumbing
Imports Autodesk.Revit.DB.Electrical
Imports Autodesk.Revit.UI


Namespace Services

    Public Class ConnectorDiagnosticsService

        Private Class ParamInfo
            Public Property HasValue As Boolean
            Public Property Text As String
        End Class

        ' === 디버그 로그 (호출자가 읽음) ===
        Public Shared Property LastDebug As List(Of String)
        Private Shared Sub Log(msg As String)
            If LastDebug Is Nothing Then LastDebug = New List(Of String)()
            LastDebug.Add($"{DateTime.Now:HH\:mm\:ss.fff} {msg}")
        End Sub

        Private Class TargetFilter
            Public Property Evaluator As Func(Of Element, Boolean)
            Public Property PrimaryParam As String = String.Empty
        End Class

        ' 3-인자: tolFt 는 피트 단위 (ft)
        Public Shared Function Run(app As UIApplication, tolFt As Double, param As String) As List(Of Dictionary(Of String, Object))
            Return Run(app, tolFt, param, CType(Nothing, IEnumerable(Of String)), Nothing, False)
        End Function

        Public Shared Function Run(app As UIApplication, tolFt As Double, param As String, extraParams As IEnumerable(Of String)) As List(Of Dictionary(Of String, Object))
            Return Run(app, tolFt, param, extraParams, Nothing, False)
        End Function

        Public Shared Function Run(app As UIApplication, tolFt As Double, param As String, extraParams As IEnumerable(Of String), targetFilter As String, excludeEndDummy As Boolean) As List(Of Dictionary(Of String, Object))
            LastDebug = New List(Of String)()
            Dim rows As New List(Of Dictionary(Of String, Object))()

            Dim uidoc = app.ActiveUIDocument
            If uidoc Is Nothing OrElse uidoc.Document Is Nothing Then
                Log("ActiveUIDocument 없음")
                Return rows
            End If
            Dim doc = uidoc.Document
            Dim normalizedExtras = NormalizeExtraParams(extraParams)
            Dim extraCache As New Dictionary(Of Integer, Dictionary(Of String, String))()
            Dim filter = ParseTargetFilter(targetFilter)

            Log($"시작 tolFt={tolFt:0.###}, param='{param}', extra={String.Join(",", normalizedExtras)}, targetFilter='{targetFilter}', excludeEndDummy={excludeEndDummy}")

            ' 1) 커넥터 있는 요소 수집 (Command 버전 기준)
            Dim elems = CollectElementsWithConnectors(doc, filter, excludeEndDummy)
            Log($"수집 요소: {elems.Count}")

            If elems.Count = 0 Then
                Log("커넥터를 가진 요소가 없습니다.")
                Return rows
            End If

            Dim allowedIds As HashSet(Of Integer) = New HashSet(Of Integer)(elems.Select(Function(e) e.Id.IntegerValue))

            ' 요소별 커넥터 매핑
            Dim elemConns As New Dictionary(Of Integer, List(Of Connector))()
            For Each el In elems
                elemConns(el.Id.IntegerValue) = GetConnectors(el)
            Next

            ' 모든 커넥터 좌표 버킷 구성 (1ft 셀)
            Dim allConnPoints As New List(Of Tuple(Of Integer, XYZ, Connector))()
            For Each kv In elemConns
                For Each c In kv.Value
                    allConnPoints.Add(Tuple.Create(kv.Key, c.Origin, c))
                Next
            Next
            Dim buckets = BuildGrid(allConnPoints)
            Log($"버킷 수: {buckets.Count}")

            ' 후보 비교 (Command 로직)
            For Each el In elems
                Dim baseId = el.Id.IntegerValue
                Dim conns = elemConns(baseId)
                For Each c In conns
                    Dim found As Element = Nothing
                    Dim distFt As Double = 0
                    Dim connType As String = ""

                    ' 1) 실제 연결
                    If c.IsConnected Then
                        For Each r As Connector In c.AllRefs.Cast(Of Connector)()
                            If r?.Owner Is Nothing Then Continue For
                            If r.Owner.Id.IntegerValue = baseId Then Continue For
                            If TypeOf r.Owner Is MEPSystem Then Continue For
                            If Not allowedIds.Contains(r.Owner.Id.IntegerValue) Then Continue For
                            found = r.Owner
                            connType = "Physical(커넥터 연결 됨)"
                            Exit For
                        Next
                    End If

                    ' 2) 근접 후보 - 최단거리 선정
                    If found Is Nothing Then
                        Dim key = BucketKey(c.Origin)
                        Dim bestOtherId As Integer = 0
                        Dim bestDistFt As Double = 0.0

                        For dx = -1 To 1
                            For dy = -1 To 1
                                For dz = -1 To 1
                                    Dim nbKey = Tuple.Create(key.Item1 + dx, key.Item2 + dy, key.Item3 + dz)
                                    If Not buckets.ContainsKey(nbKey) Then Continue For

                                    For Each nb In buckets(nbKey)
                                        Dim otherId = nb.Item1
                                        If otherId = baseId Then Continue For

                                        Dim d = c.Origin.DistanceTo(nb.Item2)
                                        If d > tolFt Then Continue For

                                        If bestOtherId = 0 OrElse d < bestDistFt Then
                                            bestOtherId = otherId
                                            bestDistFt = d
                                        End If
                                    Next
                                Next
                            Next
                        Next

                        If bestOtherId <> 0 Then
                            found = doc.GetElement(New ElementId(bestOtherId))
                            distFt = bestDistFt
                            connType = "Proximity(커넥터 연결 필요)"
                        End If
                    End If

                    If String.IsNullOrEmpty(connType) Then connType = "연결 대상 객체 없음"

                    Dim distInch As Double = Math.Round(distFt * 12.0, 2)
                    Dim info1 = GetParamInfo(el, param)
                    Dim info2 As ParamInfo = If(found IsNot Nothing, GetParamInfo(found, param), New ParamInfo() With {.HasValue = False, .Text = ""})

                    Dim status As String

                    If found Is Nothing Then
                        status = "연결 대상 객체 없음"
                    Else
                        If Not info1.HasValue AndAlso Not info2.HasValue Then
                            status = "Match"
                        ElseIf String.Equals(info1.Text, info2.Text, StringComparison.OrdinalIgnoreCase) Then
                            status = "Match"
                        Else
                            status = "Mismatch"
                        End If
                    End If

                    Dim v1 As String = info1.Text
                    Dim v2 As String = info2.Text

                    Dim extras1 = GetExtraValues(el, normalizedExtras, extraCache)
                    Dim extras2 = GetExtraValues(found, normalizedExtras, extraCache)

                    Dim shouldAdd As Boolean = False
                    If String.Equals(status, "Mismatch", StringComparison.OrdinalIgnoreCase) Then
                        shouldAdd = True
                    ElseIf connType.IndexOf("Proximity", StringComparison.OrdinalIgnoreCase) >= 0 OrElse String.Equals(connType, "Near", StringComparison.OrdinalIgnoreCase) Then
                        shouldAdd = True
                    ElseIf String.Equals(status, "연결 대상 객체 없음", StringComparison.OrdinalIgnoreCase) Then
                        shouldAdd = True
                    End If

                    If shouldAdd Then
                        Dim row = BuildRow(el, found, distInch, connType, param, v1, v2, status, normalizedExtras, extras1, extras2)
                        rows.Add(row)
                    End If
                Next
            Next

            ' 정렬 및 샘플 로그
            rows = rows.OrderBy(Function(r) ToDouble(r("Distance (inch)"))) _
                       .ThenBy(Function(r) ToInt(r("Id1"))) _
                       .ThenBy(Function(r) ToInt(r("Id2"))) _
                       .ToList()

            If rows.Count > 0 Then
                Dim s = rows(0)
                Log($"샘플: Id1={s("Id1")}, Id2={s("Id2")}, d(in)={s("Distance (inch)")}, type={s("ConnectionType")}, v1='{s("Value1")}', v2='{s("Value2")}', status={s("Status")}")
            Else
                Log("최종 rows=0 (근접도/연결 모두 해당 없음)")
            End If

            Return rows
        End Function

        ' 4-인자: tol 은 unit 기준(mm/inch/ft) → 내부에서 ft 로 환산 후 3-인자 호출
        Public Shared Function Run(app As UIApplication, tol As Double, unit As String, paramName As String) As List(Of Dictionary(Of String, Object))
            Return Run(app, tol, unit, paramName, CType(Nothing, IEnumerable(Of String)), Nothing, False)
        End Function

        Public Shared Function Run(app As UIApplication, tol As Double, unit As String, paramName As String, extraParams As IEnumerable(Of String)) As List(Of Dictionary(Of String, Object))
            Return Run(app, tol, unit, paramName, extraParams, Nothing, False)
        End Function

        Public Shared Function Run(app As UIApplication, tol As Double, unit As String, paramName As String, extraParams As IEnumerable(Of String), targetFilter As String, excludeEndDummy As Boolean) As List(Of Dictionary(Of String, Object))
            Dim tolFt As Double
            If String.Equals(unit, "mm", StringComparison.OrdinalIgnoreCase) Then
                tolFt = tol / 304.8
            ElseIf String.Equals(unit, "inch", StringComparison.OrdinalIgnoreCase) OrElse String.Equals(unit, "in", StringComparison.OrdinalIgnoreCase) Then
                tolFt = tol / 12.0
            Else
                tolFt = tol ' ft 가정
            End If
            Return Run(app, tolFt, paramName, extraParams, targetFilter, excludeEndDummy)
        End Function

        ' --------- 내부 유틸 ---------

        Private Shared Function BuildRow(e1 As Element, e2 As Element, distInch As Double, connType As String, param As String, v1 As String, v2 As String, status As String, extraNames As IList(Of String), extraVals1 As Dictionary(Of String, String), extraVals2 As Dictionary(Of String, String)) As Dictionary(Of String, Object)
            Dim cat1 As String = If(e1?.Category Is Nothing, "", e1.Category.Name)
            Dim cat2 As String = If(e2?.Category Is Nothing, "", e2.Category.Name)
            Dim fam1 As String = GetFamilyName(e1)
            Dim fam2 As String = GetFamilyName(e2)

            Dim row As New Dictionary(Of String, Object)(StringComparer.Ordinal) From {
                {"Id1", If(e1 IsNot Nothing, e1.Id.IntegerValue.ToString(), "0")},
                {"Id2", If(e2 IsNot Nothing, e2.Id.IntegerValue.ToString(), "")},
                {"Category1", cat1},
                {"Category2", cat2},
                {"Family1", fam1},
                {"Family2", fam2},
                {"Distance (inch)", distInch},
                {"ConnectionType", connType},
                {"ParamName", param},
                {"Value1", v1},
                {"Value2", v2},
                {"Status", status}
            }

            If extraNames IsNot Nothing Then
                For Each name In extraNames
                    Dim vId1 As String = ""
                    Dim vId2 As String = ""
                    If extraVals1 IsNot Nothing AndAlso extraVals1.ContainsKey(name) Then vId1 = extraVals1(name)
                    If extraVals2 IsNot Nothing AndAlso extraVals2.ContainsKey(name) Then vId2 = extraVals2(name)
                    row.Add($"{name}(ID1)", vId1)
                    row.Add($"{name}(ID2)", vId2)
                Next
            End If

            Return row
        End Function

        Private Shared Function CollectElementsWithConnectors(doc As Document, filter As TargetFilter, excludeEndDummy As Boolean) As List(Of Element)
            Dim elems As New List(Of Element)()

            For Each fi As FamilyInstance In New FilteredElementCollector(doc).OfClass(GetType(FamilyInstance))
                Try
                    If fi.MEPModel IsNot Nothing AndAlso fi.MEPModel.ConnectorManager IsNot Nothing AndAlso fi.MEPModel.ConnectorManager.Connectors IsNot Nothing AndAlso fi.MEPModel.ConnectorManager.Connectors.Cast(Of Connector)().Any() Then
                        If IsElementAllowed(fi, filter, excludeEndDummy) Then elems.Add(fi)
                    End If
                Catch
                End Try
            Next

            Dim cats = New BuiltInCategory() {
                BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
                BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting,
                BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_DuctAccessory
            }

            For Each cat In cats
                For Each el As Element In New FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType()
                    If HasConnectors(el) AndAlso IsElementAllowed(el, filter, excludeEndDummy) Then elems.Add(el)
                Next
            Next

            Return elems.Distinct().ToList()
        End Function

        Private Shared Function HasConnectors(el As Element) As Boolean
            Try
                Dim fi = TryCast(el, FamilyInstance)
                If fi?.MEPModel IsNot Nothing AndAlso fi.MEPModel.ConnectorManager?.Connectors IsNot Nothing Then
                    Return fi.MEPModel.ConnectorManager.Connectors.Cast(Of Connector)().Any()
                End If

                Dim mc = TryCast(el, MEPCurve)
                If mc?.ConnectorManager?.Connectors IsNot Nothing Then
                    Return mc.ConnectorManager.Connectors.Cast(Of Connector)().Any()
                End If
            Catch
            End Try
            Return False
        End Function

        Private Shared Function GetConnectors(el As Element) As List(Of Connector)
            Try
                Dim fi = TryCast(el, FamilyInstance)
                If fi?.MEPModel IsNot Nothing AndAlso fi.MEPModel.ConnectorManager IsNot Nothing Then
                    Return fi.MEPModel.ConnectorManager.Connectors.Cast(Of Connector)().ToList()
                End If

                Dim mc = TryCast(el, MEPCurve)
                If mc?.ConnectorManager IsNot Nothing Then
                    Return mc.ConnectorManager.Connectors.Cast(Of Connector)().ToList()
                End If
            Catch
            End Try
            Return New List(Of Connector)()
        End Function

        Private Shared Function GetFamilyName(e As Element) As String
            Try
                If TypeOf e Is FamilyInstance Then
                    Dim fi = DirectCast(e, FamilyInstance)
                    If fi.Symbol IsNot Nothing AndAlso fi.Symbol.Family IsNot Nothing Then
                        Return fi.Symbol.Family.Name
                    End If
                Else
                    Dim et = TryCast(e.Document.GetElement(e.GetTypeId()), ElementType)
                    If et IsNot Nothing Then
                        Return et.FamilyName
                    End If
                End If
            Catch
            End Try
            Return ""
        End Function

        Private Shared Function GetParamInfo(el As Element, name As String) As ParamInfo
            Dim info As New ParamInfo() With {.HasValue = False, .Text = ""}

            If el Is Nothing OrElse String.IsNullOrWhiteSpace(name) Then
                Return info
            End If

            Dim raw As String = ResolveParamText(el, name)

            info.Text = raw
            info.HasValue = (raw <> "")
            Return info
        End Function

        Private Shared Function ResolveParamText(el As Element, name As String) As String
            If el Is Nothing OrElse String.IsNullOrWhiteSpace(name) Then Return ""

            Dim p As Parameter = el.LookupParameter(name)
            If p Is Nothing OrElse Not p.HasValue Then Return ""

            Dim raw As String = Nothing
            Try
                If p.StorageType = StorageType.[String] Then
                    raw = p.AsString()
                Else
                    raw = p.AsValueString()
                    If String.IsNullOrWhiteSpace(raw) Then raw = p.AsString()
                End If
            Catch
            End Try

            If raw Is Nothing Then raw = ""
            Return raw.Trim()
        End Function

        Private Shared Function GetExtraValues(el As Element, names As IList(Of String), cache As Dictionary(Of Integer, Dictionary(Of String, String))) As Dictionary(Of String, String)
            Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If names Is Nothing OrElse names.Count = 0 Then Return result
            If el Is Nothing Then
                For Each n In names
                    result(n) = ""
                Next
                Return result
            End If

            Dim id = el.Id.IntegerValue
            Dim perElem As Dictionary(Of String, String) = Nothing
            If Not cache.TryGetValue(id, perElem) Then
                perElem = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                cache(id) = perElem
            End If

            For Each n In names
                If perElem.ContainsKey(n) Then
                    result(n) = perElem(n)
                Else
                    Dim text = ResolveParamText(el, n)
                    perElem(n) = text
                    result(n) = text
                End If
            Next

            Return result
        End Function

        Private Shared Function StatusRank(status As String) As Integer
            Select Case status
                Case "Mismatch"
                    Return 3
                Case "Match"
                    Return 2
                Case "연결 대상 객체 없음"
                    Return 1
                Case Else
                    Return 0
            End Select
        End Function

        Private Shared Function BuildGrid(items As List(Of Tuple(Of Integer, XYZ, Connector))) As Dictionary(Of Tuple(Of Integer, Integer, Integer), List(Of Tuple(Of Integer, XYZ, Connector)))
            Dim grid As New Dictionary(Of Tuple(Of Integer, Integer, Integer), List(Of Tuple(Of Integer, XYZ, Connector)))()
            For Each tup In items
                Dim key = BucketKey(tup.Item2)
                If Not grid.ContainsKey(key) Then
                    grid(key) = New List(Of Tuple(Of Integer, XYZ, Connector))()
                End If
                grid(key).Add(tup)
            Next
            Return grid
        End Function

        Private Shared Function BucketKey(p As XYZ) As Tuple(Of Integer, Integer, Integer)
            Return Tuple.Create(CInt(Math.Floor(p.X)), CInt(Math.Floor(p.Y)), CInt(Math.Floor(p.Z)))
        End Function

        Private Shared Function ToDouble(o As Object) As Double
            Try
                If o Is Nothing Then Return 0.0
                Return Convert.ToDouble(o)
            Catch
                Return 0.0
            End Try
        End Function

        Private Shared Function ToInt(o As Object) As Integer
            Try
                If o Is Nothing Then Return 0
                Dim s = o.ToString().Trim()
                If String.IsNullOrEmpty(s) Then Return 0
                Return Convert.ToInt32(s)
            Catch
                Return 0
            End Try
        End Function

        Private Shared Function NormalizeExtraParams(extraParams As IEnumerable(Of String)) As List(Of String)
            Dim result As New List(Of String)()
            If extraParams Is Nothing Then Return result

            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each raw In extraParams
                Dim name = If(raw, "")
                name = name.Trim()
                If String.IsNullOrEmpty(name) Then Continue For
                If seen.Add(name) Then result.Add(name)
            Next
            Return result
        End Function

        Private Class FilterToken
            Public Property Kind As String
            Public Property Text As String
        End Class

        Private Class FilterParser
            Private ReadOnly _tokens As List(Of FilterToken)
            Private _pos As Integer = 0
            Public Property FirstParam As String = String.Empty

            Public Sub New(raw As String)
                _tokens = Tokenize(raw)
            End Sub

            Public Function Parse() As Func(Of Element, Boolean)
                If _tokens.Count = 0 Then Return Nothing
                Dim expr = ParseExpr()
                If expr Is Nothing Then Return Nothing
                Return Function(el As Element) expr(el)
            End Function

            Private Function ParseExpr() As Func(Of Element, Boolean)
                If AtEnd() Then Return Nothing
                Dim tok = Peek()
                If tok Is Nothing Then Return Nothing

                If tok.Kind = "ident" Then
                    If NextIs("lparen", 1) Then
                        Return ParseFunc()
                    End If
                    Return ParseComparison()
                End If

                Return Nothing
            End Function

            Private Function ParseFunc() As Func(Of Element, Boolean)
                Dim nameTok = Expect("ident")
                If nameTok Is Nothing Then Return Nothing
                Dim funcName = nameTok.Text.ToLowerInvariant()
                Expect("lparen")

                Dim args As New List(Of Func(Of Element, Boolean))()
                While Not AtEnd()
                    If PeekIs("rparen") Then
                        Exit While
                    End If
                    Dim arg = ParseExpr()
                    If arg Is Nothing Then Exit While
                    args.Add(arg)

                    If PeekIs("comma") Then
                        [Next]()
                    ElseIf PeekIs("rparen") Then
                        Exit While
                    ElseIf PeekIs("ident") OrElse PeekIs("lparen") Then
                        ' 허용: or(and(...)and(...)) 같이 콤마 생략된 경우
                        Continue While
                    Else
                        Exit While
                    End If
                End While

                Expect("rparen")

                Select Case funcName
                    Case "and"
                        Return Function(el As Element)
                                   For Each a In args
                                       If a IsNot Nothing AndAlso Not a(el) Then Return False
                                   Next
                                   Return True
                               End Function
                    Case "or"
                        Return Function(el As Element)
                                   For Each a In args
                                       If a IsNot Nothing AndAlso a(el) Then Return True
                                   Next
                                   Return False
                               End Function
                    Case "not"
                        Dim inner As Func(Of Element, Boolean) = If(args.Count > 0, args(0), Nothing)
                        Return Function(el As Element)
                                   If inner Is Nothing Then Return True
                                   Return Not inner(el)
                               End Function
                    Case Else
                        Return Nothing
                End Select
            End Function

            Private Function ParseComparison() As Func(Of Element, Boolean)
                Dim left = Expect("ident")
                If left Is Nothing Then Return Nothing
                Expect("eq")
                Dim right = ExpectValue()
                If right Is Nothing Then Return Nothing

                If String.IsNullOrEmpty(FirstParam) Then FirstParam = left.Text

                Dim expected As String = right.Text
                Dim paramName As String = left.Text
                Return Function(el As Element)
                           Dim actual As String = ResolveParamText(el, paramName)
                           Return String.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase)
                       End Function
            End Function

            Private Function Expect(kind As String) As FilterToken
                If PeekIs(kind) Then Return [Next]()
                Return Nothing
            End Function

            Private Function ExpectValue() As FilterToken
                If PeekIs("string") OrElse PeekIs("ident") Then Return [Next]()
                Return Nothing
            End Function

            Private Function Peek() As FilterToken
                If _pos >= _tokens.Count Then Return Nothing
                Return _tokens(_pos)
            End Function

            Private Function PeekIs(kind As String, Optional offset As Integer = 0) As Boolean
                Dim idx = _pos + offset
                If idx < 0 OrElse idx >= _tokens.Count Then Return False
                Dim t = _tokens(idx)
                Return String.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase)
            End Function

            Private Function NextIs(kind As String, Optional offset As Integer = 0) As Boolean
                Return PeekIs(kind, offset)
            End Function

            Private Function [Next]() As FilterToken
                Dim t = Peek()
                _pos += 1
                Return t
            End Function

            Private Function AtEnd() As Boolean
                Return _pos >= _tokens.Count
            End Function

            Private Shared Function Tokenize(raw As String) As List(Of FilterToken)
                Dim list As New List(Of FilterToken)()
                If String.IsNullOrWhiteSpace(raw) Then Return list

                Dim i As Integer = 0
                While i < raw.Length
                    Dim ch = raw(i)
                    If Char.IsWhiteSpace(ch) Then
                        i += 1
                        Continue While
                    End If

                    If ch = "("c Then
                        list.Add(New FilterToken With {.Kind = "lparen", .Text = "("})
                        i += 1
                        Continue While
                    End If
                    If ch = ")"c Then
                        list.Add(New FilterToken With {.Kind = "rparen", .Text = ")"})
                        i += 1
                        Continue While
                    End If
                    If ch = ","c Then
                        list.Add(New FilterToken With {.Kind = "comma", .Text = ","})
                        i += 1
                        Continue While
                    End If
                    If ch = "="c Then
                        list.Add(New FilterToken With {.Kind = "eq", .Text = "="})
                        i += 1
                        Continue While
                    End If
                    If ch = "'"c OrElse ch = """"c Then
                        Dim quoteCh As Char = ch
                        i += 1
                        Dim start = i
                        While i < raw.Length AndAlso raw(i) <> quoteCh
                            i += 1
                        End While
                        Dim content As String = raw.Substring(start, i - start)
                        list.Add(New FilterToken With {.Kind = "string", .Text = content})
                        If i < raw.Length AndAlso raw(i) = quoteCh Then i += 1
                        Continue While
                    End If

                    Dim startWord = i
                    While i < raw.Length AndAlso Not Char.IsWhiteSpace(raw(i)) AndAlso raw(i) <> "("c AndAlso raw(i) <> ")"c AndAlso raw(i) <> ","c AndAlso raw(i) <> "="c
                        i += 1
                    End While
                    Dim word = raw.Substring(startWord, i - startWord)
                    If Not String.IsNullOrEmpty(word) Then
                        list.Add(New FilterToken With {.Kind = "ident", .Text = word})
                    End If
                End While

                Return list
            End Function
        End Class

        Private Shared Function ParseTargetFilter(raw As String) As TargetFilter
            Dim result As New TargetFilter()
            If String.IsNullOrWhiteSpace(raw) Then Return result

            Try
                Dim parser As New FilterParser(raw)
                Dim evaluator = parser.Parse()
                If evaluator Is Nothing Then Return result
                result.Evaluator = evaluator
                result.PrimaryParam = parser.FirstParam
            Catch ex As Exception
                Log($"필터 파싱 실패: {ex.Message}")
            End Try

            Return result
        End Function

        Private Shared Function IsElementAllowed(el As Element, filter As TargetFilter, excludeEndDummy As Boolean) As Boolean
            If el Is Nothing Then Return False
            If excludeEndDummy Then
                Dim fam As String = GetFamilyName(el)
                If fam.IndexOf("End_", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso fam.IndexOf("Dummy", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Return False
                End If
            End If

            If filter Is Nothing OrElse filter.Evaluator Is Nothing Then Return True

            Return filter.Evaluator(el)
        End Function

    End Class

End Namespace
