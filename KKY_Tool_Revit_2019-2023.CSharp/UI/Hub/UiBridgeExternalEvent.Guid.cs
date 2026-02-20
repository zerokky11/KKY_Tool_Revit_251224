using System;
using System.Collections.Generic;
using System.Data;
using Autodesk.Revit.UI;
using KKY_Tool_Revit.Infrastructure;
using KKY_Tool_Revit.Services;

namespace KKY_Tool_Revit.UI.Hub
{
    public partial class UiBridgeExternalEvent
    {
        private static readonly Dictionary<string, GuidAuditRunResult> _guidRuns = new Dictionary<string, GuidAuditRunResult>(StringComparer.OrdinalIgnoreCase);

        private void HandleGuidRun(UIApplication app, object payload)
        {
            try
            {
                var shared = SharedParamReader.Read(app.Application);
                SendToWeb("sharedparam:status", new
                {
                    status = shared.Status.status,
                    path = shared.Status.path,
                    existsOnDisk = shared.Status.existsOnDisk,
                    canOpen = shared.Status.canOpen,
                    isSet = shared.Status.isSet,
                    warning = shared.Status.warning,
                    errorMessage = shared.Status.errorMessage
                });

                if (shared.Status.status == "error")
                {
                    SendToWeb("guid:error", new { message = shared.Status.errorMessage });
                    return;
                }

                var run = GuidAuditService.Run(app.ActiveUIDocument.Document, shared, (pct, text) =>
                {
                    SendToWeb("guid:progress", new
                    {
                        phase = "RUN",
                        total = 100,
                        current = pct,
                        phaseProgress = pct,
                        message = text,
                        pct,
                        text
                    });
                });

                _guidRuns[run.RunId] = run;
                SendToWeb("guid:done", new
                {
                    runId = run.RunId,
                    project = DataTableToPayload(run.Project),
                    familyIndex = DataTableToPayload(run.FamilyIndex)
                });
            }
            catch (Exception ex)
            {
                SendToWeb("guid:error", new { message = ex.Message });
            }
        }

        private void HandleGuidRequestFamilyDetail(UIApplication app, object payload)
        {
            var runId = Convert.ToString(GetProp(payload, "runId") ?? string.Empty);
            if (string.IsNullOrWhiteSpace(runId) || !_guidRuns.TryGetValue(runId, out var run))
            {
                SendToWeb("guid:warn", new { message = "runId를 찾을 수 없습니다." });
                return;
            }

            SendToWeb("guid:family-detail", DataTableToPayload(run.FamilyDetail));
        }

        private static object DataTableToPayload(DataTable dt)
        {
            var cols = new List<string>();
            foreach (DataColumn col in dt.Columns)
            {
                cols.Add(col.ColumnName);
            }

            var rows = new List<List<object>>();
            foreach (DataRow dr in dt.Rows)
            {
                var row = new List<object>();
                foreach (DataColumn col in dt.Columns)
                {
                    row.Add(dr[col]);
                }
                rows.Add(row);
            }

            return new { columns = cols, rows };
        }
    }
}
