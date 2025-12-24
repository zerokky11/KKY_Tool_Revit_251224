Imports System.Data
Imports System.IO
Imports System.Windows.Forms
Imports NPOI.SS.UserModel
Imports NPOI.SS.Util
Imports NPOI.XSSF.UserModel

Namespace Infrastructure

    Public Module ExcelCore

        ' 저장 대화상자 + 저장
        Public Function PickAndSaveXlsx(sheetName As String, table As DataTable, Optional defaultFileName As String = Nothing) As String
            If table Is Nothing Then Return String.Empty
            Dim fileName As String = If(String.IsNullOrWhiteSpace(defaultFileName), $"{sheetName}.xlsx", defaultFileName)
            Using sfd As New SaveFileDialog()
                sfd.Filter = "Excel Workbook (*.xlsx)|*.xlsx"
                sfd.FileName = fileName
                sfd.AddExtension = True
                sfd.DefaultExt = "xlsx"
                sfd.OverwritePrompt = True
                sfd.RestoreDirectory = True
                If sfd.ShowDialog() = DialogResult.OK Then
                    SaveXlsx(sfd.FileName, sheetName, table)
                    Return sfd.FileName
                End If
            End Using
            Return String.Empty
        End Function

        ' 일반 테이블 저장
        Public Sub SaveXlsx(filePath As String, sheetName As String, table As DataTable)
            If table Is Nothing Then Throw New ArgumentNullException(NameOf(table))
            Dim wb As IWorkbook = New XSSFWorkbook()

            Dim headFont = wb.CreateFont() : headFont.IsBold = True
            Dim headStyle = wb.CreateCellStyle()
            headStyle.SetFont(headFont)
            headStyle.FillPattern = FillPattern.SolidForeground
            headStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index
            SetThinBorders(headStyle)

            Dim bodyStyle = wb.CreateCellStyle() : SetThinBorders(bodyStyle)

            Dim sh = wb.CreateSheet(SafeSheetName(If(sheetName, "Sheet1")))

            ' 헤더
            Dim r0 = sh.CreateRow(0)
            For ci = 0 To table.Columns.Count - 1
                Dim c = r0.CreateCell(ci)
                c.SetCellValue(table.Columns(ci).ColumnName)
                c.CellStyle = headStyle
            Next

            If table.Columns.Count > 0 Then
                Dim lastCol As Integer = table.Columns.Count - 1
                Dim range As New CellRangeAddress(0, 0, 0, lastCol)
                sh.SetAutoFilter(range)
            End If

            ' 바디
            For ri = 0 To table.Rows.Count - 1
                Dim rr = sh.CreateRow(ri + 1)
                For ci = 0 To table.Columns.Count - 1
                    Dim cc = rr.CreateCell(ci)
                    Dim v = If(table.Rows(ri)(ci), "").ToString()
                    cc.SetCellValue(v)
                    cc.CellStyle = bodyStyle
                Next
            Next

            AutoSizeAll(sh, table.Columns.Count)

            Using fs As New FileStream(filePath, FileMode.Create, FileAccess.Write)
                wb.Write(fs)
            End Using
            wb.Close()
        End Sub

        ' 요약 테이블(그룹 밴딩 + 그룹 글꼴 강조)
        Public Sub SaveStyledSimple(outPath As String, sheetName As String, table As DataTable, groupColumnName As String)
            If table Is Nothing Then Throw New ArgumentNullException(NameOf(table))
            Dim wb As IWorkbook = New XSSFWorkbook()

            Dim headFont = wb.CreateFont() : headFont.IsBold = True
            Dim headStyle = wb.CreateCellStyle()
            headStyle.SetFont(headFont)
            headStyle.FillPattern = FillPattern.SolidForeground
            headStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index
            SetThinBorders(headStyle)

            Dim bodyA = wb.CreateCellStyle() : SetThinBorders(bodyA)
            bodyA.FillPattern = FillPattern.SolidForeground
            bodyA.FillForegroundColor = IndexedColors.PaleBlue.Index

            Dim bodyB = wb.CreateCellStyle() : SetThinBorders(bodyB)
            bodyB.FillPattern = FillPattern.SolidForeground
            bodyB.FillForegroundColor = IndexedColors.LightCornflowerBlue.Index

            Dim bodyPlain = wb.CreateCellStyle() : SetThinBorders(bodyPlain)

            Dim grpFont = wb.CreateFont() : grpFont.IsBold = True : grpFont.Color = IndexedColors.RoyalBlue.Index

            Dim sh = wb.CreateSheet(SafeSheetName(If(sheetName, "Sheet1")))

            ' 헤더
            Dim r0 = sh.CreateRow(0)
            For ci = 0 To table.Columns.Count - 1
                Dim c = r0.CreateCell(ci)
                c.SetCellValue(table.Columns(ci).ColumnName)
                c.CellStyle = headStyle
            Next

            If table.Columns.Count > 0 Then
                Dim lastCol As Integer = table.Columns.Count - 1
                Dim range As New CellRangeAddress(0, 0, 0, lastCol)
                sh.SetAutoFilter(range)
            End If

            Dim grpIx As Integer = -1
            If Not String.IsNullOrWhiteSpace(groupColumnName) AndAlso table.Columns.Contains(groupColumnName) Then
                grpIx = table.Columns(groupColumnName).Ordinal
            End If

            Dim lastGroup As String = Nothing
            Dim flagA As Boolean = True

            For ri = 0 To table.Rows.Count - 1
                Dim rr = sh.CreateRow(ri + 1)
                Dim curGroup As String = Nothing

                If grpIx >= 0 Then
                    curGroup = If(table.Rows(ri)(grpIx), "").ToString()
                    If lastGroup Is Nothing OrElse Not lastGroup.Equals(curGroup, StringComparison.Ordinal) Then
                        flagA = Not flagA
                        lastGroup = curGroup
                    End If
                End If

                For ci = 0 To table.Columns.Count - 1
                    Dim cc = rr.CreateCell(ci)
                    Dim v = If(table.Rows(ri)(ci), "").ToString()
                    cc.SetCellValue(v)

                    Dim st = If(grpIx >= 0, If(flagA, bodyA, bodyB), bodyPlain)
                    If grpIx = ci AndAlso grpIx >= 0 Then
                        Dim s2 = wb.CreateCellStyle()
                        s2.CloneStyleFrom(st)
                        s2.SetFont(grpFont)
                        cc.CellStyle = s2
                    Else
                        cc.CellStyle = st
                    End If
                Next
            Next

            AutoSizeAll(sh, table.Columns.Count)

            Using fs As New FileStream(outPath, FileMode.Create, FileAccess.Write)
                wb.Write(fs)
            End Using
            wb.Close()
        End Sub

        ' 공통: 얇은 테두리 + 자동열너비
        Private Sub SetThinBorders(st As ICellStyle)
            st.BorderBottom = NPOI.SS.UserModel.BorderStyle.Thin
            st.BorderTop = NPOI.SS.UserModel.BorderStyle.Thin
            st.BorderLeft = NPOI.SS.UserModel.BorderStyle.Thin
            st.BorderRight = NPOI.SS.UserModel.BorderStyle.Thin
        End Sub

        Private Sub AutoSizeAll(sh As ISheet, colCount As Integer)
            For ci = 0 To colCount - 1
                sh.AutoSizeColumn(ci, True) ' DPI 반영
                Dim cur = sh.GetColumnWidth(ci)
                sh.SetColumnWidth(ci, Math.Min(cur + 512, 255 * 256))
            Next
        End Sub

        Private Function SafeSheetName(name As String) As String
            Dim bad = New Char() {"/"c, "\"c, "?"c, "*"c, "["c, "]"c, ":"c}
            Dim s = name
            For Each ch In bad : s = s.Replace(ch, "-"c) : Next
            If s.Length > 31 Then s = s.Substring(0, 31)
            If String.IsNullOrWhiteSpace(s) Then s = "Sheet1"
            Return s
        End Function

    End Module
End Namespace
