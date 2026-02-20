using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using KKY_Tool_Revit.Infrastructure;

namespace KKY_Tool_Revit.Exports
{
    public class DupRowDto
    {
        public string Id { get; set; }
        public string Category { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        public List<string> ConnectedIds { get; set; } = new List<string>();
    }

    public static class DuplicateExport
    {
        public static string Save(IEnumerable rows)
        {
            var mapped = MapRows(rows);
            var dt = BuildSimpleTable(mapped);
            return ExcelCore.PickAndSaveXlsx("Duplicates (Simple)", dt, "Duplicates.xlsx");
        }

        public static void Save(string outPath, IEnumerable rows)
        {
            Export(outPath, rows);
        }

        public static void Export(string outPath, IEnumerable rows)
        {
            var mapped = MapRows(rows);
            var dt = BuildSimpleTable(mapped);
            ExcelCore.SaveStyledSimple(outPath, "Duplicates (Simple)", dt, "Group");
        }

        private static List<DupRowDto> MapRows(IEnumerable rows)
        {
            var list = new List<DupRowDto>();
            if (rows == null) return list;
            foreach (var o in rows)
            {
                var it = new DupRowDto
                {
                    Id = ReadProp(o, "Id", "ID", "ElementId", "ElementID", "elementId", "id"),
                    Category = ReadProp(o, "Category", "category"),
                    Family = ReadProp(o, "Family", "family"),
                    Type = ReadProp(o, "Type", "type"),
                    ConnectedIds = ReadList(o, "ConnectedIds", "connectedIds", "Links", "links", "connected", "Connected", "ConnectedElements")
                };
                list.Add(it);
            }
            return list;
        }

        private static DataTable BuildSimpleTable(List<DupRowDto> rows)
        {
            var dt = new DataTable("simple");
            dt.Columns.Add("Group");
            dt.Columns.Add("ID");
            dt.Columns.Add("Category");
            dt.Columns.Add("Family");
            dt.Columns.Add("Type");

            var groupList = GroupByLogic(rows);
            for (var i = 0; i < groupList.Count; i++)
            {
                var gName = $"Group{i + 1}";
                foreach (var r in groupList[i])
                {
                    var famOut = string.IsNullOrWhiteSpace(r.Family)
                        ? (string.IsNullOrWhiteSpace(r.Category) ? "" : r.Category + " Type")
                        : r.Family;
                    var dr = dt.NewRow();
                    dr["Group"] = gName;
                    dr["ID"] = Nz(r.Id);
                    dr["Category"] = Nz(r.Category);
                    dr["Family"] = Nz(famOut);
                    dr["Type"] = Nz(r.Type);
                    dt.Rows.Add(dr);
                }
            }

            return dt;
        }

        private static List<List<DupRowDto>> GroupByLogic(List<DupRowDto> items)
        {
            var buckets = new Dictionary<string, List<DupRowDto>>();
            foreach (var r in items)
            {
                var fam = string.IsNullOrWhiteSpace(r.Family)
                    ? (string.IsNullOrWhiteSpace(r.Category) ? "" : r.Category + " Type")
                    : r.Family;
                var typ = r.Type ?? "";
                var cat = r.Category ?? "";

                var clusterSrc = new List<string>();
                if (!string.IsNullOrWhiteSpace(r.Id)) clusterSrc.Add(r.Id);
                if (r.ConnectedIds != null) clusterSrc.AddRange(r.ConnectedIds);

                var cluster = clusterSrc
                    .SelectMany(SplitIds)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct()
                    .OrderBy(PadNum)
                    .ToList();

                var clusterKey = cluster.Count > 1 ? string.Join(",", cluster) : "";
                var key = string.Join("|", new[] { cat, fam, typ, clusterKey });
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<DupRowDto>();
                    buckets[key] = list;
                }
                list.Add(r);
            }

            return buckets.Values.ToList();
        }

        private static IEnumerable<string> SplitIds(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
            return s.Split(new[] { ',', ' ', ';', '|', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string PadNum(string s) => int.TryParse(s, out var n) ? n.ToString("D10") : s;
        private static string Nz(string s) => string.IsNullOrWhiteSpace(s) ? "" : s;

        private static string ReadProp(object obj, params string[] names)
        {
            if (obj == null) return "";
            foreach (var nm in names)
            {
                var p = obj.GetType().GetProperty(nm);
                if (p == null) continue;
                var v = p.GetValue(obj, null);
                if (v != null) return Convert.ToString(v) ?? "";
            }
            return "";
        }

        private static List<string> ReadList(object obj, params string[] names)
        {
            var res = new List<string>();
            if (obj == null) return res;
            foreach (var nm in names)
            {
                var p = obj.GetType().GetProperty(nm);
                if (p == null) continue;
                var v = p.GetValue(obj, null);
                if (v == null) continue;

                if (v is string s)
                {
                    res.AddRange(SplitIds(s));
                    break;
                }

                if (v is IEnumerable ie)
                {
                    foreach (var x in ie) if (x != null) res.Add(Convert.ToString(x));
                    break;
                }
            }
            return res;
        }
    }
}
