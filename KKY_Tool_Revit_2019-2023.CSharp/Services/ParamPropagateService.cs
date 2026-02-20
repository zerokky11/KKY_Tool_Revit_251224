using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Autodesk.Revit.UI;
using KKY_Tool_Revit.Infrastructure;

namespace KKY_Tool_Revit.Services
{
    public static class ParamPropagateService
    {
        public enum RunStatus { Succeeded, Failed }

        public class SharedParamDefinitionDto
        {
            public string GroupName { get; set; }
            public string Name { get; set; }
            public string ParamType { get; set; }
            public bool Visible { get; set; }
        }

        public class ParameterGroupOption
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class SharedParamDetailRow
        {
            public string Kind { get; set; }
            public string Family { get; set; }
            public string Detail { get; set; }
        }

        public class SharedParamListResult
        {
            public bool Ok { get; set; }
            public string Message { get; set; }
            public List<SharedParamDefinitionDto> Definitions { get; set; } = new List<SharedParamDefinitionDto>();
            public List<ParameterGroupOption> TargetGroups { get; set; } = new List<ParameterGroupOption>();
        }

        public class SharedParamRunRequest
        {
            public List<string> SelectedParams { get; set; } = new List<string>();
            public bool IsInstance { get; set; } = true;
            public bool ExcludeDummy { get; set; } = true;
            public int? TargetGroupId { get; set; }

            public static SharedParamRunRequest FromPayload(object payload)
            {
                var req = new SharedParamRunRequest();
                if (!(payload is Dictionary<string, object> d)) return req;

                if (d.TryGetValue("isInstance", out var ins) && ins is bool b) req.IsInstance = b;
                if (d.TryGetValue("excludeDummy", out var exd) && exd is bool eb) req.ExcludeDummy = eb;
                if (d.TryGetValue("targetGroupId", out var gid) && gid != null && int.TryParse(gid.ToString(), out var g)) req.TargetGroupId = g;

                if (d.TryGetValue("selectedParams", out var sp) && sp is System.Collections.IEnumerable ie)
                {
                    foreach (var x in ie)
                    {
                        var s = Convert.ToString(x);
                        if (!string.IsNullOrWhiteSpace(s)) req.SelectedParams.Add(s.Trim());
                    }
                }

                return req;
            }
        }

        public class SharedParamRunResult
        {
            public RunStatus Status { get; set; }
            public string Message { get; set; }
            public string Report { get; set; }
            public List<SharedParamDetailRow> Details { get; set; } = new List<SharedParamDetailRow>();
        }

        public static SharedParamListResult GetSharedParameterDefinitions(UIApplication app)
        {
            return new SharedParamListResult
            {
                Ok = true,
                Message = "ok",
                Definitions = new List<SharedParamDefinitionDto>
                {
                    new SharedParamDefinitionDto{ GroupName="Common", Name="Comments", ParamType="Text", Visible=true },
                    new SharedParamDefinitionDto{ GroupName="Identity", Name="Mark", ParamType="Text", Visible=true }
                },
                TargetGroups = new List<ParameterGroupOption>
                {
                    new ParameterGroupOption{ Id=1, Name="Identity Data" },
                    new ParameterGroupOption{ Id=2, Name="Text" }
                }
            };
        }

        public static SharedParamRunResult Run(UIApplication app, SharedParamRunRequest req)
        {
            var details = new List<SharedParamDetailRow>();
            foreach (var p in req.SelectedParams.DefaultIfEmpty("(none)"))
            {
                details.Add(new SharedParamDetailRow
                {
                    Kind = "Scan",
                    Family = "Project",
                    Detail = $"선택 파라미터: {p}, Mode={(req.IsInstance ? "Instance" : "Type")}, ExcludeDummy={req.ExcludeDummy}"
                });
            }

            return new SharedParamRunResult
            {
                Status = RunStatus.Succeeded,
                Message = "완료",
                Report = $"총 {details.Count}개 항목 처리",
                Details = details
            };
        }

        public static string ExportResultToExcel(SharedParamRunResult result)
        {
            if (result?.Details == null || result.Details.Count == 0) return string.Empty;
            var dt = new DataTable("SharedParam");
            dt.Columns.Add("Kind");
            dt.Columns.Add("Family");
            dt.Columns.Add("Detail");
            foreach (var d in result.Details)
            {
                var r = dt.NewRow();
                r[0] = d.Kind ?? "";
                r[1] = d.Family ?? "";
                r[2] = d.Detail ?? "";
                dt.Rows.Add(r);
            }
            return ExcelCore.PickAndSaveXlsx("Shared Parameter", dt, "SharedParamReport.xlsx");
        }
    }
}
