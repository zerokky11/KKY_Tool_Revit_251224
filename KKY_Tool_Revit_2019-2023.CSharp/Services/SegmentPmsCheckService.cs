using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;

namespace KKY_Tool_Revit.Services
{
    public static class SegmentPmsCheckService
    {
        public const string TableRules = "Rules";
        public const string TableSizes = "Sizes";

        public class PmsRow
        {
            public string CLASS { get; set; }
            public string SegmentKey { get; set; }
            public double ID { get; set; }
            public double OD { get; set; }
            public double ND_mm { get; set; }
        }

        public class MappingRequest
        {
            public string File { get; set; }
            public string PipeTypeName { get; set; }
            public int RuleIndex { get; set; }
            public int SegmentId { get; set; }
            public string SegmentKey { get; set; }
            public string SelectedClass { get; set; }
            public string SelectedPmsSegment { get; set; }
            public string MappingSource { get; set; }
        }

        public class RunResult
        {
            public DataTable CompareTable { get; set; }
            public DataTable MapTable { get; set; }
            public DataTable RevitSizeTable { get; set; }
            public DataTable PmsSizeTable { get; set; }
            public DataTable ErrorTable { get; set; }
        }

        public static (DataTable Table, List<string> Errors) LoadPmsTable(string path)
        {
            var dt = new DataTable("PMS");
            dt.Columns.Add("CLASS");
            dt.Columns.Add("SegmentKey");
            dt.Columns.Add("ID");
            dt.Columns.Add("OD");
            dt.Columns.Add("ND_mm");

            var errors = new List<string>();
            if (!File.Exists(path))
            {
                errors.Add("파일이 존재하지 않습니다.");
                return (dt, errors);
            }

            var lines = File.ReadAllLines(path);
            foreach (var l in lines.Skip(1))
            {
                var p = l.Split('\t', ',', ';');
                if (p.Length < 2) continue;
                var r = dt.NewRow();
                r["CLASS"] = p.ElementAtOrDefault(0) ?? "";
                r["SegmentKey"] = p.ElementAtOrDefault(1) ?? "";
                r["ID"] = p.ElementAtOrDefault(2) ?? "";
                r["OD"] = p.ElementAtOrDefault(3) ?? "";
                r["ND_mm"] = p.ElementAtOrDefault(4) ?? "";
                dt.Rows.Add(r);
            }

            return (dt, errors);
        }

        public static List<PmsRow> ToPmsRows(DataTable dt)
        {
            var list = new List<PmsRow>();
            if (dt == null) return list;
            foreach (DataRow r in dt.Rows)
            {
                list.Add(new PmsRow
                {
                    CLASS = Convert.ToString(r["CLASS"]),
                    SegmentKey = Convert.ToString(r["SegmentKey"]),
                    ID = ToDouble(r["ID"]),
                    OD = ToDouble(r["OD"]),
                    ND_mm = ToDouble(r["ND_mm"])
                });
            }
            return list;
        }

        public static DataSet BuildExtractData(IEnumerable<string> files)
        {
            var ds = new DataSet("Extract");
            var rules = new DataTable(TableRules);
            rules.Columns.Add("File");
            rules.Columns.Add("PipeTypeName");
            rules.Columns.Add("RuleIndex", typeof(int));
            rules.Columns.Add("SegmentId", typeof(int));
            rules.Columns.Add("SegmentKey");

            var sizes = new DataTable(TableSizes);
            sizes.Columns.Add("File");
            sizes.Columns.Add("PipeTypeName");
            sizes.Columns.Add("SegmentKey");
            sizes.Columns.Add("ND_mm", typeof(double));
            sizes.Columns.Add("ID", typeof(double));
            sizes.Columns.Add("OD", typeof(double));

            var idx = 1;
            foreach (var f in files ?? Array.Empty<string>())
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var rr = rules.NewRow();
                rr["File"] = f;
                rr["PipeTypeName"] = "PipeType-" + name;
                rr["RuleIndex"] = idx;
                rr["SegmentId"] = idx;
                rr["SegmentKey"] = "SEG-" + idx;
                rules.Rows.Add(rr);

                var sr = sizes.NewRow();
                sr["File"] = f;
                sr["PipeTypeName"] = "PipeType-" + name;
                sr["SegmentKey"] = "SEG-" + idx;
                sr["ND_mm"] = 100 + idx;
                sr["ID"] = 90 + idx;
                sr["OD"] = 110 + idx;
                sizes.Rows.Add(sr);
                idx++;
            }

            ds.Tables.Add(rules);
            ds.Tables.Add(sizes);
            return ds;
        }

        public static RunResult RunUsingExtract(DataSet extract, DataTable pmsTable, List<MappingRequest> mappings, double tolMm, int ndRound)
        {
            var compare = new DataTable("SizeCompare");
            compare.Columns.Add("File");
            compare.Columns.Add("PipeTypeName");
            compare.Columns.Add("SegmentRuleIndex");
            compare.Columns.Add("RevitSegmentKey");
            compare.Columns.Add("CLASS");
            compare.Columns.Add("PMS_SegmentKey");
            compare.Columns.Add("ND_mm");
            compare.Columns.Add("Revit_ID");
            compare.Columns.Add("Revit_OD");
            compare.Columns.Add("PMS_ID");
            compare.Columns.Add("PMS_OD");
            compare.Columns.Add("Status");

            var map = new DataTable("PipeTypeSegmentMap");
            map.Columns.Add("File"); map.Columns.Add("PipeTypeName"); map.Columns.Add("RuleIndex");
            map.Columns.Add("SegmentKey"); map.Columns.Add("CLASS"); map.Columns.Add("PMS_SegmentKey"); map.Columns.Add("Source");

            var revitRaw = extract?.Tables.Contains(TableSizes) == true ? extract.Tables[TableSizes].Copy() : new DataTable("SegmentSizeRaw_Revit");
            var pmsRaw = pmsTable?.Copy() ?? new DataTable("SegmentSizeRaw_PMS");
            var err = new DataTable("Error"); err.Columns.Add("Message");

            foreach (var m in mappings ?? new List<MappingRequest>())
            {
                var mr = map.NewRow();
                mr["File"] = m.File ?? "";
                mr["PipeTypeName"] = m.PipeTypeName ?? "";
                mr["RuleIndex"] = m.RuleIndex;
                mr["SegmentKey"] = m.SegmentKey ?? "";
                mr["CLASS"] = m.SelectedClass ?? "";
                mr["PMS_SegmentKey"] = m.SelectedPmsSegment ?? "";
                mr["Source"] = m.MappingSource ?? "";
                map.Rows.Add(mr);
            }

            if (revitRaw.Columns.Count > 0)
            {
                foreach (DataRow rr in revitRaw.Rows)
                {
                    var cr = compare.NewRow();
                    cr["File"] = Convert.ToString(rr["File"]);
                    cr["PipeTypeName"] = Convert.ToString(rr["PipeTypeName"]);
                    cr["SegmentRuleIndex"] = 0;
                    cr["RevitSegmentKey"] = Convert.ToString(rr["SegmentKey"]);
                    cr["CLASS"] = "";
                    cr["PMS_SegmentKey"] = "";
                    cr["ND_mm"] = Convert.ToString(rr["ND_mm"]);
                    cr["Revit_ID"] = Convert.ToString(rr["ID"]);
                    cr["Revit_OD"] = Convert.ToString(rr["OD"]);
                    cr["PMS_ID"] = "";
                    cr["PMS_OD"] = "";
                    cr["Status"] = "Review";
                    compare.Rows.Add(cr);
                }
            }

            return new RunResult
            {
                CompareTable = compare,
                MapTable = map,
                RevitSizeTable = revitRaw,
                PmsSizeTable = pmsRaw,
                ErrorTable = err
            };
        }

        private static double ToDouble(object o)
        {
            if (o == null) return 0;
            return double.TryParse(Convert.ToString(o), out var v) ? v : 0;
        }
    }
}
