using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;

namespace KKY_Tool_Revit.Services
{
    public static class DuplicateAnalysisService
    {
        public static List<Dictionary<string, object>> Run(UIApplication app)
        {
            var uidoc = app?.ActiveUIDocument;
            if (uidoc?.Document == null) return new List<Dictionary<string, object>>();
            var doc = uidoc.Document;

            var elems = new List<Element>();
            elems.AddRange(new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).WhereElementIsNotElementType().ToElements());
            elems.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .ToElements()
                .Where(HasAnyConnector));

            var groups = new Dictionary<string, List<Element>>(StringComparer.Ordinal);
            foreach (var e in elems)
            {
                var key = BuildGroupKey(e, doc);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<Element>();
                    groups[key] = list;
                }
                list.Add(e);
            }

            var rows = new List<Dictionary<string, object>>();
            var gno = 1;
            foreach (var kv in groups)
            {
                var list = kv.Value;
                if (list == null || list.Count == 0) continue;
                var isCandidate = list.Count >= 2;

                foreach (var e in list)
                {
                    var cat = e.Category?.Name ?? string.Empty;
                    string fam;
                    string typ;

                    if (e is FamilyInstance fi)
                    {
                        typ = fi.Symbol?.Name ?? string.Empty;
                        fam = fi.Symbol?.Family?.Name ?? string.Empty;
                    }
                    else
                    {
                        typ = e.Name;
                        var et = doc.GetElement(e.GetTypeId()) as ElementType;
                        fam = et?.FamilyName ?? string.Empty;
                    }

                    var connected = GetConnectedOwnerIds(e);
                    rows.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["groupId"] = gno,
                        ["id"] = e.Id.IntegerValue.ToString(),
                        ["category"] = cat,
                        ["family"] = fam,
                        ["type"] = typ,
                        ["connectedCount"] = connected.Count,
                        ["connectedIds"] = string.Join(",", connected.OrderBy(x => x)),
                        ["candidate"] = isCandidate
                    });
                }

                gno++;
            }

            return rows
                .OrderBy(r => Convert.ToInt32(r["groupId"]))
                .ThenBy(r => Convert.ToInt32(r["id"]))
                .ToList();
        }

        private static bool HasAnyConnector(Element e)
        {
            try
            {
                if (e is MEPCurve curve) return curve.ConnectorManager?.Connectors?.Size > 0;
                if (e is FamilyInstance fi) return fi.MEPModel?.ConnectorManager?.Connectors?.Size > 0;
            }
            catch { }
            return false;
        }

        private static string BuildGroupKey(Element e, Document doc)
        {
            var cat = e.Category?.Name ?? string.Empty;
            var fam = string.Empty;
            var typ = string.Empty;

            if (e is FamilyInstance fi)
            {
                typ = fi.Symbol?.Name ?? string.Empty;
                fam = fi.Symbol?.Family?.Name ?? string.Empty;
            }
            else
            {
                typ = e.Name;
                var et = doc.GetElement(e.GetTypeId()) as ElementType;
                fam = et?.FamilyName ?? string.Empty;
            }

            var center = GetCenterPoint(e);
            var qx = Math.Round(center.X, 4);
            var qy = Math.Round(center.Y, 4);
            var qz = Math.Round(center.Z, 4);

            return $"{cat}|{fam}|{typ}|{qx},{qy},{qz}";
        }

        private static XYZ GetCenterPoint(Element e)
        {
            try
            {
                var bb = e.get_BoundingBox(null);
                if (bb != null) return (bb.Min + bb.Max) * 0.5;
            }
            catch { }
            return XYZ.Zero;
        }

        private static HashSet<int> GetConnectedOwnerIds(Element e)
        {
            var ids = new HashSet<int>();
            var connectors = GetConnectors(e);
            foreach (Connector c in connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    try
                    {
                        if (r.Owner == null) continue;
                        var oid = r.Owner.Id.IntegerValue;
                        if (oid != e.Id.IntegerValue) ids.Add(oid);
                    }
                    catch { }
                }
            }
            return ids;
        }

        private static IEnumerable GetConnectors(Element e)
        {
            if (e is MEPCurve curve) return curve.ConnectorManager?.Connectors;
            if (e is FamilyInstance fi) return fi.MEPModel?.ConnectorManager?.Connectors;
            return new List<Connector>();
        }
    }
}
