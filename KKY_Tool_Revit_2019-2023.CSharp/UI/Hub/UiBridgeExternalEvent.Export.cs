using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using KKY_Tool_Revit.Services;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace KKY_Tool_Revit.UI.Hub
{
    public partial class UiBridgeExternalEvent
    {
        private static List<Dictionary<string, object>> Export_LastExportRows = new List<Dictionary<string, object>>();

        private void HandleExportBrowse()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                var r = dlg.ShowDialog();
                if (r == System.Windows.Forms.DialogResult.OK)
                {
                    var files = Directory.GetFiles(dlg.SelectedPath, "*.rvt", SearchOption.AllDirectories);
                    _host?.SendToWeb("export:files", new { files });
                }
            }
        }

        private void HandleExportPreview(UIApplication app, object payload)
        {
            try
            {
                var pd = payload as Dictionary<string, object>;
                var files = ExtractStringList(pd, "files");
                var rows = ExportPointsService.Run(app, files)
                    .Select(r => new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["File"] = r.File,
                        ["ProjectPoint_E(mm)"] = r.ProjectE,
                        ["ProjectPoint_N(mm)"] = r.ProjectN,
                        ["ProjectPoint_Z(mm)"] = r.ProjectZ,
                        ["SurveyPoint_E(mm)"] = r.SurveyE,
                        ["SurveyPoint_N(mm)"] = r.SurveyN,
                        ["SurveyPoint_Z(mm)"] = r.SurveyZ,
                        ["TrueNorthAngle(deg)"] = r.TrueNorth
                    }).ToList();

                Export_LastExportRows = rows;
                _host?.SendToWeb("export:previewed", new { rows });
            }
            catch (Exception ex)
            {
                _host?.SendToWeb("revit:error", new { message = "미리보기 실패: " + ex.Message });
                _host?.SendToWeb("export:previewed", new { rows = new List<Dictionary<string, object>>() });
            }
        }

        private void HandleExportSaveExcel(object payload)
        {
            try
            {
                var pd = payload as Dictionary<string, object>;
                var unit = ExtractUnit(pd);
                var rows = TryGetRowsFromPayload(pd);
                if (rows == null || rows.Count == 0) rows = Export_LastExportRows;
                if (rows == null) rows = new List<Dictionary<string, object>>();

                var dt = BuildExportDataTableFromRows(rows, unit);
                var defaultName = $"{DateTime.Now:yyMMdd}_좌표 추출 결과.xlsx";
                var savePath = SaveExcelWithDialog(dt, defaultName);
                if (!string.IsNullOrEmpty(savePath)) _host?.SendToWeb("export:saved", new { path = savePath });
            }
            catch (Exception ex)
            {
                _host?.SendToWeb("revit:error", new { message = "엑셀 저장 실패: " + ex.Message });
            }
        }

        private static List<string> ExtractStringList(Dictionary<string, object> payload, string key)
        {
            var list = new List<string>();
            if (payload == null || !payload.TryGetValue(key, out var v) || v == null) return list;
            if (v is IEnumerable ie)
            {
                foreach (var it in ie)
                {
                    var s = Convert.ToString(it, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            else
            {
                var s = Convert.ToString(v, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            return list;
        }

        private static string ExtractUnit(Dictionary<string, object> payload)
        {
            if (payload == null) return "ft";
            if (!payload.TryGetValue("unit", out var v) || v == null) return "ft";
            return NormalizeUnit(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static string NormalizeUnit(string unit)
        {
            var u = (unit ?? "ft").Trim().ToLowerInvariant();
            if (u == "mm" || u == "millimeter" || u == "millimeters") return "mm";
            if (u == "m" || u == "meter" || u == "meters") return "m";
            return "ft";
        }

        private static List<Dictionary<string, object>> TryGetRowsFromPayload(Dictionary<string, object> payload)
        {
            if (payload == null) return new List<Dictionary<string, object>>();
            if (payload.TryGetValue("rows", out var rowsObj) && rowsObj is IEnumerable rowsEnum) return AnyToRows(rowsEnum);
            if (payload.TryGetValue("data", out var dataObj) && dataObj is IEnumerable dataEnum) return AnyToRows(dataEnum);
            return new List<Dictionary<string, object>>();
        }

        private static List<Dictionary<string, object>> AnyToRows(IEnumerable any)
        {
            var result = new List<Dictionary<string, object>>();
            if (any == null) return result;
            foreach (var item in any)
            {
                if (item is Dictionary<string, object> d)
                {
                    result.Add(d);
                }
                else
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    var t = item?.GetType();
                    if (t != null)
                    {
                        foreach (var p in t.GetProperties()) row[p.Name] = p.GetValue(item, null);
                    }
                    result.Add(row);
                }
            }
            return result;
        }

        private static DataTable BuildExportDataTableFromRows(List<Dictionary<string, object>> rows, string unit)
        {
            var normalizedUnit = NormalizeUnit(unit);
            var suffix = normalizedUnit == "mm" ? "(mm)" : normalizedUnit == "m" ? "(m)" : "(ft)";

            var headers = new[]
            {
                "File",
                $"ProjectPoint_E{suffix}",
                $"ProjectPoint_N{suffix}",
                $"ProjectPoint_Z{suffix}",
                $"SurveyPoint_E{suffix}",
                $"SurveyPoint_N{suffix}",
                $"SurveyPoint_Z{suffix}",
                "TrueNorthAngle(deg)"
            };

            var dt = new DataTable("Export");
            foreach (var h in headers) dt.Columns.Add(h);

            foreach (var r in rows ?? new List<Dictionary<string, object>>())
            {
                var dr = dt.NewRow();
                dr[0] = SafeToString(r, "File");
                dr[1] = SafeToString(r, "ProjectPoint_E(mm)");
                dr[2] = SafeToString(r, "ProjectPoint_N(mm)");
                dr[3] = SafeToString(r, "ProjectPoint_Z(mm)");
                dr[4] = SafeToString(r, "SurveyPoint_E(mm)");
                dr[5] = SafeToString(r, "SurveyPoint_N(mm)");
                dr[6] = SafeToString(r, "SurveyPoint_Z(mm)");
                dr[7] = SafeToString(r, "TrueNorthAngle(deg)");
                dt.Rows.Add(dr);
            }

            return dt;
        }

        private static string SafeToString(Dictionary<string, object> row, string key)
        {
            if (row == null) return string.Empty;
            if (!row.TryGetValue(key, out var v) || v == null) return string.Empty;
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string SaveExcelWithDialog(DataTable dt, string defaultName = "export.xlsx")
        {
            if (dt == null || dt.Columns.Count == 0) return string.Empty;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = defaultName
            };

            var ok = dlg.ShowDialog();
            if (ok != true) return string.Empty;

            var path = dlg.FileName;
            try
            {
                IWorkbook wb = new XSSFWorkbook();
                var sh = wb.CreateSheet("Export");

                var hr = sh.CreateRow(0);
                for (var c = 0; c < dt.Columns.Count; c++) hr.CreateCell(c).SetCellValue(dt.Columns[c].ColumnName);

                var rIndex = 1;
                foreach (DataRow dr in dt.Rows)
                {
                    var rr = sh.CreateRow(rIndex++);
                    for (var c = 0; c < dt.Columns.Count; c++) rr.CreateCell(c).SetCellValue(Convert.ToString(dr[c], CultureInfo.InvariantCulture));
                }

                for (var c = 0; c < dt.Columns.Count; c++) sh.AutoSizeColumn(c);
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) wb.Write(fs);

                return path;
            }
            catch (Exception ex)
            {
                _host?.SendToWeb("host:error", new { message = "엑셀 저장 실패: " + ex.Message });
                return string.Empty;
            }
        }
    }
}
