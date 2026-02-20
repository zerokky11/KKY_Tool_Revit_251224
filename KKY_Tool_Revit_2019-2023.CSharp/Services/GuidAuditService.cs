using System;
using System.Collections.Generic;
using System.Data;
using Autodesk.Revit.DB;
using KKY_Tool_Revit.Infrastructure;

namespace KKY_Tool_Revit.Services
{
    public sealed class GuidAuditRunResult
    {
        public string RunId { get; set; } = Guid.NewGuid().ToString("N");
        public DataTable Project { get; set; } = CreateProjectTable();
        public DataTable FamilyDetail { get; set; } = CreateFamilyDetailTable();
        public DataTable FamilyIndex { get; set; } = CreateFamilyIndexTable();

        public static DataTable CreateProjectTable()
        {
            var dt = new DataTable("Project");
            dt.Columns.Add("RvtPath");
            dt.Columns.Add("RvtName");
            dt.Columns.Add("Scope");
            dt.Columns.Add("ParamName");
            dt.Columns.Add("ParamKind");
            dt.Columns.Add("ProjectGuid");
            dt.Columns.Add("FileGuid");
            dt.Columns.Add("Result");
            dt.Columns.Add("Notes");
            return dt;
        }

        public static DataTable CreateFamilyDetailTable()
        {
            var dt = new DataTable("FamilyDetail");
            dt.Columns.Add("RvtPath");
            dt.Columns.Add("RvtName");
            dt.Columns.Add("FamilyName");
            dt.Columns.Add("Category");
            dt.Columns.Add("ParamName");
            dt.Columns.Add("ParamKind");
            dt.Columns.Add("IsShared");
            dt.Columns.Add("ProjectGuid");
            dt.Columns.Add("FileGuid");
            dt.Columns.Add("Result");
            dt.Columns.Add("Notes");
            return dt;
        }

        public static DataTable CreateFamilyIndexTable()
        {
            var dt = new DataTable("FamilyIndex");
            dt.Columns.Add("RvtPath");
            dt.Columns.Add("RvtName");
            dt.Columns.Add("FamilyName");
            return dt;
        }
    }

    public static class GuidAuditService
    {
        public static GuidAuditRunResult Run(Document doc, SharedParamReadResult sharedParam, Action<int, string> progress)
        {
            var result = new GuidAuditRunResult();
            var rvtPath = doc?.PathName ?? string.Empty;
            var rvtName = string.IsNullOrWhiteSpace(rvtPath) ? (doc?.Title ?? string.Empty) : System.IO.Path.GetFileName(rvtPath);

            var map = doc.ParameterBindings;
            var it = map?.ForwardIterator();
            it?.Reset();

            var rows = new List<object[]>();
            while (it != null && it.MoveNext())
            {
                if (!(it.Key is Definition def)) continue;
                if (!(def is ExternalDefinition extDef)) continue;

                var name = def.Name;
                var projectGuid = extDef.GUID;
                sharedParam.NameToGuids.TryGetValue(name, out var fileGuids);

                if (fileGuids == null || fileGuids.Count == 0)
                {
                    rows.Add(new object[] { rvtPath, rvtName, "Project", name, "Shared", projectGuid.ToString(), string.Empty, "Missing", "Shared parameter file에 같은 이름 없음" });
                    continue;
                }

                var matched = false;
                foreach (var fileGuid in fileGuids)
                {
                    if (fileGuid == projectGuid)
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    rows.Add(new object[] { rvtPath, rvtName, "Project", name, "Shared", projectGuid.ToString(), string.Join(",", fileGuids), "Mismatch", "GUID 불일치" });
                }
            }

            var total = Math.Max(rows.Count, 1);
            for (var i = 0; i < rows.Count; i++)
            {
                result.Project.Rows.Add(rows[i]);
                var pct = (int)Math.Round((i + 1) * 100.0 / total);
                progress?.Invoke(pct, $"Project audit {i + 1}/{total}");
            }

            return result;
        }
    }
}
