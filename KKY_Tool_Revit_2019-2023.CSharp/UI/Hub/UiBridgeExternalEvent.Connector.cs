using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.UI;
using KKY_Tool_Revit.Services;

namespace KKY_Tool_Revit.UI.Hub
{
    public partial class UiBridgeExternalEvent
    {
        private static List<Dictionary<string, object>> _connectorTotalRows = new List<Dictionary<string, object>>();
        private static List<string> _connectorExtraParams = new List<string>();

        private void HandleConnectorRun(UIApplication app, object payload)
        {
            try
            {
                var p = payload as Dictionary<string, object> ?? new Dictionary<string, object>();

                var tol = ToDouble(GetLocal(p, "tol"), 1.0);
                var unit = Convert.ToString(GetLocal(p, "unit"), CultureInfo.InvariantCulture) ?? "inch";
                var param = Convert.ToString(GetLocal(p, "param"), CultureInfo.InvariantCulture) ?? "Comments";
                var targetFilter = Convert.ToString(GetLocal(p, "targetFilter"), CultureInfo.InvariantCulture);
                var excludeEndDummy = ToBool(GetLocal(p, "excludeEndDummy"));
                _connectorExtraParams = ParseExtraParams(Convert.ToString(GetLocal(p, "extraParams"), CultureInfo.InvariantCulture));

                SendToWeb("connector:progress", new { phase = "RUN", total = 100, current = 5, phaseProgress = 5, message = "커넥터 진단 시작" });

                var rows = ConnectorDiagnosticsService.Run(app, tol, unit, param, _connectorExtraParams, targetFilter, excludeEndDummy);
                _connectorTotalRows = rows ?? new List<Dictionary<string, object>>();

                var previewRows = _connectorTotalRows.Take(150).ToList();
                var mismatchAll = _connectorTotalRows.Where(r => string.Equals(Convert.ToString(GetLocal(r, "Status")), "Mismatch", StringComparison.OrdinalIgnoreCase)).ToList();
                var nearAll = _connectorTotalRows.Where(r => string.Equals(Convert.ToString(GetLocal(r, "ConnectionType")), "Near", StringComparison.OrdinalIgnoreCase)).ToList();

                SendToWeb("connector:progress", new { phase = "DONE", total = 100, current = 100, phaseProgress = 100, message = "커넥터 진단 완료" });

                var loadedPayload = new
                {
                    rows = previewRows,
                    total = _connectorTotalRows.Count,
                    previewCount = previewRows.Count,
                    hasMore = _connectorTotalRows.Count > previewRows.Count,
                    mismatch = new
                    {
                        rows = mismatchAll.Take(150).ToList(),
                        total = mismatchAll.Count,
                        previewCount = Math.Min(150, mismatchAll.Count),
                        hasMore = mismatchAll.Count > 150
                    },
                    near = new
                    {
                        rows = nearAll.Take(150).ToList(),
                        total = nearAll.Count,
                        previewCount = Math.Min(150, nearAll.Count),
                        hasMore = nearAll.Count > 150
                    },
                    extraParams = _connectorExtraParams
                };

                SendToWeb("connector:loaded", loadedPayload);
                SendToWeb("connector:done", loadedPayload);
            }
            catch (Exception ex)
            {
                SendToWeb("connector:done", new { ok = false, message = ex.Message });
                SendToWeb("revit:error", new { message = "실행 실패: " + ex.Message });
            }
        }

        private void HandleConnectorSaveExcel(UIApplication app, object payload)
        {
            try
            {
                var rows = _connectorTotalRows;
                if (rows == null || rows.Count == 0)
                {
                    rows = TryGetConnectorRowsFromPayload(payload as Dictionary<string, object>);
                }

                if (rows == null || rows.Count == 0)
                {
                    SendToWeb("revit:error", new { message = "저장할 데이터가 없습니다." });
                    return;
                }

                var filtered = rows.Where(r => string.Equals(Convert.ToString(GetLocal(r, "Status"), CultureInfo.InvariantCulture), "Mismatch", StringComparison.OrdinalIgnoreCase)).ToList();
                if (filtered.Count == 0)
                {
                    System.Windows.Forms.MessageBox.Show("Mismatch 항목이 없습니다.", "검토 결과", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
                    return;
                }

                var dt = BuildConnectorTable(filtered, _connectorExtraParams);
                var path = KKY_Tool_Revit.Exports.ConnectorExport.SaveWithDialog(dt);
                if (!string.IsNullOrWhiteSpace(path)) SendToWeb("connector:saved", new { path });
            }
            catch (Exception ex)
            {
                SendToWeb("revit:error", new { message = "엑셀 저장 실패: " + ex.Message });
            }
        }

        private static DataTable BuildConnectorTable(List<Dictionary<string, object>> rows, List<string> extras)
        {
            var dt = new DataTable("Connector");
            var headers = new List<string>
            {
                "Id1","Id2","Category1","Category2","Family1","Family2","Distance (inch)","ConnectionType","ParamName","Value1","Value2","Status"
            };
            if (extras != null)
            {
                foreach (var e in extras)
                {
                    headers.Add($"{e}(ID1)");
                    headers.Add($"{e}(ID2)");
                }
            }
            foreach (var h in headers) dt.Columns.Add(h);

            foreach (var r in rows)
            {
                var dr = dt.NewRow();
                foreach (var h in headers) dr[h] = Convert.ToString(GetLocal(r, h), CultureInfo.InvariantCulture) ?? string.Empty;
                dt.Rows.Add(dr);
            }
            return dt;
        }

        private static object GetLocal(Dictionary<string, object> d, string key)
        {
            if (d == null || string.IsNullOrWhiteSpace(key)) return null;
            if (d.TryGetValue(key, out var v)) return v;
            var kv = d.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrEmpty(kv.Key) ? null : kv.Value;
        }

        private static double ToDouble(object v, double fallback)
        {
            if (v == null) return fallback;
            if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
            return fallback;
        }

        private static bool ToBool(object v)
        {
            if (v == null) return false;
            if (v is bool b) return b;
            if (bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var p)) return p;
            return false;
        }

        private static List<string> ParseExtraParams(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new List<string>();
            return s.Split(new[] { ',', ';', '|', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<Dictionary<string, object>> TryGetConnectorRowsFromPayload(Dictionary<string, object> payload)
        {
            if (payload == null) return new List<Dictionary<string, object>>();
            if (payload.TryGetValue("rows", out var rowsObj) && rowsObj is IEnumerable rowsEnum) return AnyConnectorToRows(rowsEnum);
            if (payload.TryGetValue("data", out var dataObj) && dataObj is IEnumerable dataEnum) return AnyConnectorToRows(dataEnum);
            return new List<Dictionary<string, object>>();
        }

        private static List<Dictionary<string, object>> AnyConnectorToRows(IEnumerable any)
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
    }
}
