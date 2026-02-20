using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KKY_Tool_Revit.Infrastructure;

namespace KKY_Tool_Revit.Services
{
    public class ExportPointsService
    {
        public class Row
        {
            public string File { get; set; }
            public double ProjectE { get; set; }
            public double ProjectN { get; set; }
            public double ProjectZ { get; set; }
            public double SurveyE { get; set; }
            public double SurveyN { get; set; }
            public double SurveyZ { get; set; }
            public double TrueNorth { get; set; }
        }

        public static IList<Row> Run(UIApplication uiapp, object files)
        {
            var app = uiapp.Application;
            var list = new List<Row>();
            var paths = NormalizePaths(files);
            if (paths.Count == 0) return list;

            foreach (var p in paths)
            {
                Document doc = null;
                try
                {
                    var opt = BuildOpenOptions(p);
                    var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(p);
                    doc = app.OpenDocumentFile(mp, opt);

                    var row = new Row { File = Path.GetFileName(p) };
                    Extract(doc, row);
                    list.Add(row);
                }
                catch
                {
                }
                finally
                {
                    try { doc?.Close(false); } catch { }
                }
            }

            return list;
        }

        public static string ExportToExcel(UIApplication uiapp, object files, string unit = "ft")
        {
            var rows = Run(uiapp, files);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outPath = Path.Combine(desktop, $"ExportPoints_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

            var normalizedUnit = NormalizeUnit(unit);
            var headers = BuildHeaders(normalizedUnit);
            var data = rows.Select(r => new object[]
            {
                r.File,
                RoundCoord(ToUnitValue(r.ProjectE, normalizedUnit)),
                RoundCoord(ToUnitValue(r.ProjectN, normalizedUnit)),
                RoundCoord(ToUnitValue(r.ProjectZ, normalizedUnit)),
                RoundCoord(ToUnitValue(r.SurveyE, normalizedUnit)),
                RoundCoord(ToUnitValue(r.SurveyN, normalizedUnit)),
                RoundCoord(ToUnitValue(r.SurveyZ, normalizedUnit)),
                Math.Round(r.TrueNorth, 3)
            });

            var dt = BuildTable(headers, data);
            ExcelCore.SaveXlsx(outPath, "Points", dt);
            return outPath;
        }

        private static List<string> NormalizePaths(object files)
        {
            var paths = new List<string>();
            if (files is IEnumerable<object> eo)
            {
                foreach (var o in eo)
                {
                    if (o is string s && !string.IsNullOrWhiteSpace(s) && File.Exists(s)) paths.Add(s);
                }
            }
            else if (files is IEnumerable<string> es)
            {
                foreach (var s in es)
                {
                    if (!string.IsNullOrWhiteSpace(s) && File.Exists(s)) paths.Add(s);
                }
            }
            else if (files is string one && File.Exists(one))
            {
                paths.Add(one);
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static OpenOptions BuildOpenOptions(string p)
        {
            var opt = new OpenOptions();
            try
            {
                var ws = new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets);
                opt.SetOpenWorksetsConfiguration(ws);
            }
            catch { }
            return opt;
        }

        private static void Extract(Document doc, Row row)
        {
            var basePt = new FilteredElementCollector(doc)
                .OfClass(typeof(BasePoint))
                .Cast<BasePoint>()
                .FirstOrDefault(bp => !bp.IsShared);

            var surveyPt = new FilteredElementCollector(doc)
                .OfClass(typeof(BasePoint))
                .Cast<BasePoint>()
                .FirstOrDefault(bp => bp.IsShared);

            var project = basePt?.Position ?? XYZ.Zero;
            var survey = surveyPt?.Position ?? XYZ.Zero;

            row.ProjectE = project.X;
            row.ProjectN = project.Y;
            row.ProjectZ = project.Z;
            row.SurveyE = survey.X;
            row.SurveyN = survey.Y;
            row.SurveyZ = survey.Z;

            var projectPos = doc.ActiveProjectLocation?.GetProjectPosition(XYZ.Zero);
            row.TrueNorth = projectPos?.Angle * 180.0 / Math.PI ?? 0.0;
        }

        private static IEnumerable<string> BuildHeaders(string unit)
        {
            var suffix = unit == "mm" ? "(mm)" : unit == "m" ? "(m)" : "(ft)";
            return new[]
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
        }

        private static DataTable BuildTable(IEnumerable<string> headers, IEnumerable<IEnumerable<object>> rows)
        {
            var dt = new DataTable("ExportedPoints");
            var hs = (headers ?? Enumerable.Empty<string>()).ToArray();
            foreach (var h in hs) dt.Columns.Add(h);

            if (rows != null)
            {
                foreach (var r in rows)
                {
                    var vals = (r ?? Enumerable.Empty<object>()).ToArray();
                    var dr = dt.NewRow();
                    for (var i = 0; i < Math.Min(vals.Length, dt.Columns.Count); i++) dr[i] = Convert.ToString(vals[i], CultureInfo.InvariantCulture) ?? string.Empty;
                    dt.Rows.Add(dr);
                }
            }

            return dt;
        }

        private static string NormalizeUnit(string unit)
        {
            var u = (unit ?? "ft").Trim().ToLowerInvariant();
            if (u == "mm" || u == "millimeter" || u == "millimeters") return "mm";
            if (u == "m" || u == "meter" || u == "meters") return "m";
            return "ft";
        }

        private static double ToUnitValue(double ft, string unit)
        {
            if (unit == "mm") return UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
            if (unit == "m") return UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
            return ft;
        }

        private static string RoundCoord(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
