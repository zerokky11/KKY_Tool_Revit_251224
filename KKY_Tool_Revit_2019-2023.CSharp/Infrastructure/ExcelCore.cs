using System;
using System.Data;
using System.IO;
using System.Windows.Forms;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;

namespace KKY_Tool_Revit.Infrastructure
{
    public static class ExcelCore
    {
        public static string PickAndSaveXlsx(string sheetName, DataTable table, string defaultFileName = null)
        {
            if (table == null) return string.Empty;
            var fileName = string.IsNullOrWhiteSpace(defaultFileName) ? $"{sheetName}.xlsx" : defaultFileName;

            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "Excel Workbook (*.xlsx)|*.xlsx";
                sfd.FileName = fileName;
                sfd.AddExtension = true;
                sfd.DefaultExt = "xlsx";
                sfd.OverwritePrompt = true;
                sfd.RestoreDirectory = true;
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    SaveXlsx(sfd.FileName, sheetName, table);
                    return sfd.FileName;
                }
            }

            return string.Empty;
        }

        public static void SaveXlsx(string filePath, string sheetName, DataTable table)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            IWorkbook wb = new XSSFWorkbook();

            var headFont = wb.CreateFont();
            headFont.IsBold = true;
            var headStyle = wb.CreateCellStyle();
            headStyle.SetFont(headFont);
            headStyle.FillPattern = FillPattern.SolidForeground;
            headStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
            SetThinBorders(headStyle);

            var bodyStyle = wb.CreateCellStyle();
            SetThinBorders(bodyStyle);

            var sh = wb.CreateSheet(SafeSheetName(sheetName ?? "Sheet1"));

            var r0 = sh.CreateRow(0);
            for (var ci = 0; ci < table.Columns.Count; ci++)
            {
                var c = r0.CreateCell(ci);
                c.SetCellValue(table.Columns[ci].ColumnName);
                c.CellStyle = headStyle;
            }

            if (table.Columns.Count > 0)
            {
                var lastCol = table.Columns.Count - 1;
                sh.SetAutoFilter(new CellRangeAddress(0, 0, 0, lastCol));
            }

            for (var ri = 0; ri < table.Rows.Count; ri++)
            {
                var rr = sh.CreateRow(ri + 1);
                for (var ci = 0; ci < table.Columns.Count; ci++)
                {
                    var cc = rr.CreateCell(ci);
                    cc.SetCellValue(Convert.ToString(table.Rows[ri][ci] ?? ""));
                    cc.CellStyle = bodyStyle;
                }
            }

            AutoSizeAll(sh, table.Columns.Count);
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write)) wb.Write(fs);
            wb.Close();
        }

        public static void SaveStyledSimple(string outPath, string sheetName, DataTable table, string groupColumnName)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            IWorkbook wb = new XSSFWorkbook();

            var headFont = wb.CreateFont();
            headFont.IsBold = true;
            var headStyle = wb.CreateCellStyle();
            headStyle.SetFont(headFont);
            headStyle.FillPattern = FillPattern.SolidForeground;
            headStyle.FillForegroundColor = IndexedColors.Grey25Percent.Index;
            SetThinBorders(headStyle);

            var bodyA = wb.CreateCellStyle();
            SetThinBorders(bodyA);
            bodyA.FillPattern = FillPattern.SolidForeground;
            bodyA.FillForegroundColor = IndexedColors.PaleBlue.Index;

            var bodyB = wb.CreateCellStyle();
            SetThinBorders(bodyB);
            bodyB.FillPattern = FillPattern.SolidForeground;
            bodyB.FillForegroundColor = IndexedColors.LightCornflowerBlue.Index;

            var bodyPlain = wb.CreateCellStyle();
            SetThinBorders(bodyPlain);

            var grpFont = wb.CreateFont();
            grpFont.IsBold = true;
            grpFont.Color = IndexedColors.RoyalBlue.Index;

            var sh = wb.CreateSheet(SafeSheetName(sheetName ?? "Sheet1"));

            var r0 = sh.CreateRow(0);
            for (var ci = 0; ci < table.Columns.Count; ci++)
            {
                var c = r0.CreateCell(ci);
                c.SetCellValue(table.Columns[ci].ColumnName);
                c.CellStyle = headStyle;
            }

            if (table.Columns.Count > 0)
            {
                var lastCol = table.Columns.Count - 1;
                sh.SetAutoFilter(new CellRangeAddress(0, 0, 0, lastCol));
            }

            var grpIx = -1;
            if (!string.IsNullOrWhiteSpace(groupColumnName) && table.Columns.Contains(groupColumnName)) grpIx = table.Columns[groupColumnName].Ordinal;

            string lastGroup = null;
            var flagA = true;

            for (var ri = 0; ri < table.Rows.Count; ri++)
            {
                var rr = sh.CreateRow(ri + 1);

                if (grpIx >= 0)
                {
                    var curGroup = Convert.ToString(table.Rows[ri][grpIx] ?? "");
                    if (lastGroup == null || !lastGroup.Equals(curGroup, StringComparison.Ordinal))
                    {
                        flagA = !flagA;
                        lastGroup = curGroup;
                    }
                }

                for (var ci = 0; ci < table.Columns.Count; ci++)
                {
                    var cc = rr.CreateCell(ci);
                    cc.SetCellValue(Convert.ToString(table.Rows[ri][ci] ?? ""));

                    var st = grpIx >= 0 ? (flagA ? bodyA : bodyB) : bodyPlain;
                    if (grpIx == ci && grpIx >= 0)
                    {
                        var s2 = wb.CreateCellStyle();
                        s2.CloneStyleFrom(st);
                        s2.SetFont(grpFont);
                        cc.CellStyle = s2;
                    }
                    else
                    {
                        cc.CellStyle = st;
                    }
                }
            }

            AutoSizeAll(sh, table.Columns.Count);
            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write)) wb.Write(fs);
            wb.Close();
        }

        private static void SetThinBorders(ICellStyle st)
        {
            st.BorderBottom = BorderStyle.Thin;
            st.BorderTop = BorderStyle.Thin;
            st.BorderLeft = BorderStyle.Thin;
            st.BorderRight = BorderStyle.Thin;
        }

        private static void AutoSizeAll(ISheet sh, int colCount)
        {
            for (var ci = 0; ci < colCount; ci++)
            {
                sh.AutoSizeColumn(ci, true);
                var cur = sh.GetColumnWidth(ci);
                sh.SetColumnWidth(ci, Math.Min(cur + 512, 255 * 256));
            }
        }

        private static string SafeSheetName(string name)
        {
            var s = name;
            foreach (var ch in new[] { '/', '\\', '?', '*', '[', ']', ':' }) s = s.Replace(ch, '-');
            if (s.Length > 31) s = s.Substring(0, 31);
            if (string.IsNullOrWhiteSpace(s)) s = "Sheet1";
            return s;
        }
    }
}
