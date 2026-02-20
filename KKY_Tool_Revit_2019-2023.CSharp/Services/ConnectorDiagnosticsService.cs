using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;

namespace KKY_Tool_Revit.Services
{
    public static class ConnectorDiagnosticsService
    {
        public static List<Dictionary<string, object>> LastDebugRows { get; private set; } = new List<Dictionary<string, object>>();

        public static List<Dictionary<string, object>> Run(UIApplication app, double tolFt, string param)
        {
            return Run(app, tolFt, param, null, null, false);
        }

        public static List<Dictionary<string, object>> Run(UIApplication app, double tolFt, string param, IEnumerable<string> extraParams, string targetFilter, bool excludeEndDummy)
        {
            var rows = new List<Dictionary<string, object>>();
            var uidoc = app?.ActiveUIDocument;
            if (uidoc?.Document == null) return rows;
            var doc = uidoc.Document;

            var elems = new FilteredElementCollector(doc)
                .OfClass(typeof(MEPCurve))
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();

            for (var i = 0; i < elems.Count; i++)
            {
                var e1 = elems[i];
                var p1 = e1.Location as LocationCurve;
                if (p1?.Curve == null) continue;
                var p1c = p1.Curve.Evaluate(0.5, true);

                for (var j = i + 1; j < elems.Count; j++)
                {
                    var e2 = elems[j];
                    var p2 = e2.Location as LocationCurve;
                    if (p2?.Curve == null) continue;
                    var p2c = p2.Curve.Evaluate(0.5, true);

                    var dFt = p1c.DistanceTo(p2c);
                    if (dFt > tolFt) continue;

                    var v1 = ReadParam(e1, param);
                    var v2 = ReadParam(e2, param);
                    var status = string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase) ? "OK" : "Mismatch";
                    if (!string.Equals(status, "Mismatch", StringComparison.OrdinalIgnoreCase)) continue;

                    rows.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["Id1"] = e1.Id.IntegerValue.ToString(CultureInfo.InvariantCulture),
                        ["Id2"] = e2.Id.IntegerValue.ToString(CultureInfo.InvariantCulture),
                        ["Category1"] = e1.Category?.Name ?? string.Empty,
                        ["Category2"] = e2.Category?.Name ?? string.Empty,
                        ["Family1"] = e1.Name,
                        ["Family2"] = e2.Name,
                        ["Distance (inch)"] = dFt * 12.0,
                        ["ConnectionType"] = "Near",
                        ["ParamName"] = param ?? string.Empty,
                        ["Value1"] = v1,
                        ["Value2"] = v2,
                        ["Status"] = status
                    });
                }
            }

            rows = rows.OrderBy(r => Convert.ToDouble(r["Distance (inch)"], CultureInfo.InvariantCulture))
                .ThenBy(r => Convert.ToInt32(r["Id1"], CultureInfo.InvariantCulture))
                .ThenBy(r => Convert.ToInt32(r["Id2"], CultureInfo.InvariantCulture))
                .ToList();

            LastDebugRows = rows;
            return rows;
        }

        public static List<Dictionary<string, object>> Run(UIApplication app, double tol, string unit, string paramName, IEnumerable<string> extraParams, string targetFilter, bool excludeEndDummy)
        {
            var tolFt = string.Equals(unit, "mm", StringComparison.OrdinalIgnoreCase) ? tol / 304.8
                : (string.Equals(unit, "inch", StringComparison.OrdinalIgnoreCase) || string.Equals(unit, "in", StringComparison.OrdinalIgnoreCase)) ? tol / 12.0
                : tol;
            return Run(app, tolFt, paramName, extraParams, targetFilter, excludeEndDummy);
        }

        private static string ReadParam(Element e, string param)
        {
            if (e == null || string.IsNullOrWhiteSpace(param)) return string.Empty;
            try
            {
                var p = e.LookupParameter(param);
                if (p == null || !p.HasValue) return string.Empty;
                if (p.StorageType == StorageType.String) return p.AsString() ?? string.Empty;
                if (p.StorageType == StorageType.Integer) return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                if (p.StorageType == StorageType.Double) return p.AsDouble().ToString("0.###", CultureInfo.InvariantCulture);
                if (p.StorageType == StorageType.ElementId) return p.AsElementId()?.IntegerValue.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                return p.AsValueString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
