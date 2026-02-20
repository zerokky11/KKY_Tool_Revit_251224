using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
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
        private string _pmsPath;
        private DataTable _pmsTable;
        private List<SegmentPmsCheckService.PmsRow> _pmsRows;
        private Dictionary<string, List<string>> _defaultMap;
        private DataSet _extractData;
        private int _lastNdRound = 3;

        private static string PmsFolder()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KKY_Tool_Revit", "PMS");
            Directory.CreateDirectory(root);
            return root;
        }

        private static string PmsMetaPath() => Path.Combine(PmsFolder(), "pms_path.txt");

        private void HandleSegmentPmsRegister(UIApplication app, object payload)
        {
            try
            {
                var path = Convert.ToString(GetProp(payload, "path"));
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    SendToWeb("segmentpms:error", new { message = "PMS 파일 경로가 올바르지 않습니다." });
                    return;
                }

                var dest = Path.Combine(PmsFolder(), Path.GetFileName(path));
                File.Copy(path, dest, true);
                File.WriteAllText(PmsMetaPath(), dest);

                var loaded = SegmentPmsCheckService.LoadPmsTable(dest);
                _pmsPath = dest;
                _pmsTable = loaded.Table;
                _pmsRows = SegmentPmsCheckService.ToPmsRows(_pmsTable);

                if (loaded.Errors.Any()) SendToWeb("segmentpms:error", new { message = string.Join(";", loaded.Errors) });
                else SendToWeb("segmentpms:pms-registered", new { ok = true, path = dest, options = BuildPmsOptions() });
            }
            catch (Exception ex)
            {
                SendToWeb("segmentpms:error", new { message = ex.Message });
            }
        }

        private void HandleSegmentPmsLoadDefault(object payload)
        {
            try
            {
                var path = Convert.ToString(GetProp(payload, "path"));
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    SendToWeb("segmentpms:defaultmap-loaded", new { ok = true, items = new object[] { } });
                    return;
                }

                var arr = File.ReadAllLines(path)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Split('\t', ',', ';'))
                    .Where(p => p.Length >= 2)
                    .Select(p => new { segment = p[0].Trim(), @default = p[1].Trim() })
                    .ToArray();

                _defaultMap = arr.GroupBy(x => x.segment, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.@default).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);

                SendToWeb("segmentpms:defaultmap-loaded", new { ok = true, items = arr });
            }
            catch (Exception ex)
            {
                SendToWeb("segmentpms:error", new { message = ex.Message });
            }
        }

        private void HandleSegmentPmsExtract(UIApplication app, object payload)
        {
            try
            {
                var files = ExtractFiles(payload);
                if (files.Count == 0)
                {
                    SendToWeb("segmentpms:error", new { message = "등록된 RVT 파일이 없습니다. 먼저 파일을 등록하세요." });
                    return;
                }

                _extractData = SegmentPmsCheckService.BuildExtractData(files);
                var summary = BuildExtractSummary(_extractData);
                var pipes = BuildPipePayload(_extractData);
                SendToWeb("segmentpms:extracted", new { summary, pipes, pms = BuildPmsOptions() });
            }
            catch (Exception ex)
            {
                SendToWeb("segmentpms:error", new { message = ex.Message });
            }
        }

        private void HandleSegmentPmsSaveExtract(object payload)
        {
            if (_extractData == null)
            {
                SendToWeb("segmentpms:error", new { message = "추출 데이터가 없습니다." });
                return;
            }

            using (var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Extract (*.xml)|*.xml", FileName = "SegmentPmsExtract.xml" })
            {
                if (dlg.ShowDialog() != true) return;
                try
                {
                    _extractData.WriteXml(dlg.FileName, XmlWriteMode.WriteSchema);
                    SendToWeb("segmentpms:extract-saved", new { path = dlg.FileName });
                }
                catch (Exception ex)
                {
                    SendToWeb("segmentpms:error", new { message = ex.Message });
                }
            }
        }

        private void HandleSegmentPmsOpenExtract(object payload)
        {
            using (var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Extract (*.xml)|*.xml" })
            {
                if (dlg.ShowDialog() != true) return;
                try
                {
                    var ds = new DataSet();
                    ds.ReadXml(dlg.FileName, XmlReadMode.ReadSchema);
                    _extractData = ds;
                    var summary = BuildExtractSummary(ds);
                    SendToWeb("segmentpms:extract-opened", new { summary, pipes = BuildPipePayload(ds), pms = BuildPmsOptions() });
                }
                catch (Exception ex)
                {
                    SendToWeb("segmentpms:error", new { message = ex.Message });
                }
            }
        }

        private void HandleSegmentPmsPrepare(UIApplication app, object payload) => HandleSegmentPmsExtract(app, payload);

        private void HandleSegmentPmsRun(UIApplication app, object payload)
        {
            if (_extractData == null)
            {
                SendToWeb("segmentpms:error", new { message = "추출 데이터를 먼저 준비하세요." });
                return;
            }
            if (_pmsTable == null)
            {
                SendToWeb("segmentpms:error", new { message = "PMS 데이터를 등록하세요." });
                return;
            }

            var p = payload as Dictionary<string, object> ?? new Dictionary<string, object>();
            var mappings = new List<SegmentPmsCheckService.MappingRequest>();
            if (p.TryGetValue("mappings", out var mapsObj) && mapsObj is IEnumerable maps)
            {
                foreach (var o in maps)
                {
                    mappings.Add(new SegmentPmsCheckService.MappingRequest
                    {
                        File = Convert.ToString(GetProp(o, "file")),
                        PipeTypeName = Convert.ToString(GetProp(o, "pipeType")),
                        RuleIndex = ToInt(GetProp(o, "ruleIndex")),
                        SegmentId = ToInt(GetProp(o, "segmentId")),
                        SegmentKey = Convert.ToString(GetProp(o, "segmentKey")),
                        SelectedClass = Convert.ToString(GetProp(o, "cls")),
                        SelectedPmsSegment = Convert.ToString(GetProp(o, "segment")),
                        MappingSource = Convert.ToString(GetProp(o, "source"))
                    });
                }
            }

            var tolMm = ToDouble(GetProp(payload, "tolMm"), 1.0);
            var ndRound = ToInt(GetProp(payload, "ndRound"), 3);
            _lastNdRound = ndRound;

            var res = SegmentPmsCheckService.RunUsingExtract(_extractData, _pmsTable, mappings, tolMm, ndRound);
            SendToWeb("segmentpms:result", new
            {
                map = DataTableToObjects(res.MapTable),
                revitRaw = DataTableToObjects(res.RevitSizeTable),
                pmsRaw = DataTableToObjects(res.PmsSizeTable),
                compare = DataTableToObjects(res.CompareTable),
                errors = DataTableToObjects(res.ErrorTable)
            });
        }

        private void HandleSegmentPmsSaveExcel(UIApplication app, object payload)
        {
            if (payload == null) return;
            var compare = GetProp(payload, "compare") as IEnumerable;
            if (compare == null)
            {
                SendToWeb("segmentpms:error", new { message = "저장할 결과가 없습니다." });
                return;
            }

            var map = GetProp(payload, "map") as IEnumerable;
            var revitRaw = GetProp(payload, "revitRaw") as IEnumerable;
            var pmsRaw = GetProp(payload, "pmsRaw") as IEnumerable;
            var err = GetProp(payload, "errors") as IEnumerable;

            using (var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "SegmentPmsCheck.xlsx", AddExtension = true })
            {
                if (dlg.ShowDialog() != true) return;
                try
                {
                    IWorkbook wb = new XSSFWorkbook();
                    AddSheet(wb, "PipeTypeSegmentMap", map);
                    AddSheet(wb, "SegmentSizeRaw_Revit", revitRaw);
                    AddSheet(wb, "SegmentSizeRaw_PMS", pmsRaw);
                    AddSheet(wb, "SizeCompare", compare);
                    AddSheet(wb, "Error", err);
                    using (var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write)) wb.Write(fs);
                    SendToWeb("segmentpms:saved", new { path = dlg.FileName });
                }
                catch (Exception ex)
                {
                    SendToWeb("segmentpms:error", new { message = ex.Message });
                }
            }
        }

        private static List<string> ExtractFiles(object payload)
        {
            var files = new List<string>();
            if (!(GetProp(payload, "files") is IEnumerable ie)) return files;
            foreach (var x in ie)
            {
                var s = Convert.ToString(x);
                if (!string.IsNullOrWhiteSpace(s)) files.Add(s);
            }
            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private object BuildPmsOptions()
        {
            if (_pmsRows == null) return new object[] { };
            return _pmsRows
                .GroupBy(r => new { r.CLASS, r.SegmentKey })
                .Select(g => new { cls = g.Key.CLASS, segment = g.Key.SegmentKey, label = $"{g.Key.CLASS} / {g.Key.SegmentKey}" })
                .ToArray();
        }

        private static string BuildExtractSummary(DataSet ds)
        {
            if (ds == null) return string.Empty;
            var rules = ds.Tables.Contains(SegmentPmsCheckService.TableRules) ? ds.Tables[SegmentPmsCheckService.TableRules] : null;
            var sizes = ds.Tables.Contains(SegmentPmsCheckService.TableSizes) ? ds.Tables[SegmentPmsCheckService.TableSizes] : null;
            var fileCount = rules == null ? 0 : rules.AsEnumerable().Select(r => Convert.ToString(r["File"])).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var pipeCount = rules == null ? 0 : rules.Rows.Count;
            var candCount = rules?.Rows.Count ?? 0;
            var sizeCount = sizes?.Rows.Count ?? 0;
            return $"파일 {fileCount}개, PipeType {pipeCount}, Segment 후보 {candCount}, 사이즈 {sizeCount}";
        }

        private static object BuildPipePayload(DataSet ds)
        {
            if (ds == null || !ds.Tables.Contains(SegmentPmsCheckService.TableRules)) return new object[] { };
            var t = ds.Tables[SegmentPmsCheckService.TableRules];
            var buckets = t.AsEnumerable()
                .GroupBy(r => new { File = Convert.ToString(r["File"]), PipeTypeName = Convert.ToString(r["PipeTypeName"]) });

            return buckets.Select(g => new
            {
                file = g.Key.File,
                pipeType = g.Key.PipeTypeName,
                candidates = g.Select(r => new
                {
                    ruleIndex = ToInt(r["RuleIndex"]),
                    segmentId = ToInt(r["SegmentId"]),
                    segmentKey = Convert.ToString(r["SegmentKey"]),
                    segmentName = Convert.ToString(r["SegmentKey"])
                }).OrderBy(x => x.ruleIndex).ToList(),
                defaultRuleIndex = g.Select(r => ToInt(r["RuleIndex"])).DefaultIfEmpty(0).Min()
            }).ToArray();
        }

        private static List<Dictionary<string, object>> DataTableToObjects(DataTable dt)
        {
            var list = new List<Dictionary<string, object>>();
            if (dt == null) return list;
            foreach (DataRow r in dt.Rows)
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DataColumn c in dt.Columns) d[c.ColumnName] = r[c];
                list.Add(d);
            }
            return list;
        }

        private static void AddSheet(IWorkbook wb, string name, IEnumerable rows)
        {
            var sh = wb.CreateSheet(name);
            var data = new List<Dictionary<string, object>>();
            if (rows != null)
            {
                foreach (var o in rows)
                {
                    if (o is Dictionary<string, object> d) data.Add(d);
                    else
                    {
                        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        var t = o?.GetType();
                        if (t != null) foreach (var p in t.GetProperties()) row[p.Name] = p.GetValue(o);
                        data.Add(row);
                    }
                }
            }

            var headers = data.SelectMany(x => x.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var hr = sh.CreateRow(0);
            for (var i = 0; i < headers.Count; i++) hr.CreateCell(i).SetCellValue(headers[i]);

            var rix = 1;
            foreach (var d in data)
            {
                var rr = sh.CreateRow(rix++);
                for (var c = 0; c < headers.Count; c++) rr.CreateCell(c).SetCellValue(Convert.ToString(d.TryGetValue(headers[c], out var v) ? v : ""));
            }
            for (var i = 0; i < headers.Count; i++) sh.AutoSizeColumn(i);
        }

        private static int ToInt(object v, int fb = 0)
        {
            if (v == null) return fb;
            return int.TryParse(Convert.ToString(v), out var i) ? i : fb;
        }

        private static double ToDouble(object v, double fb = 0)
        {
            if (v == null) return fb;
            return double.TryParse(Convert.ToString(v), out var d) ? d : fb;
        }
    }
}
