using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using KKY_Tool_Revit.Services;

namespace KKY_Tool_Revit.UI.Hub
{
    public partial class UiBridgeExternalEvent
    {
        private static ParamPropagateService.SharedParamRunResult _lastParamResult;

        private void HandleSharedParamList(UIApplication app, object payload)
        {
            try
            {
                var res = ParamPropagateService.GetSharedParameterDefinitions(app);
                var shaped = new
                {
                    ok = res != null && res.Ok,
                    message = res?.Message,
                    definitions = (res?.Definitions ?? new List<ParamPropagateService.SharedParamDefinitionDto>()).Select(d => new
                    {
                        groupName = d.GroupName,
                        name = d.Name,
                        paramType = d.ParamType,
                        visible = d.Visible
                    }).ToList(),
                    targetGroups = (res?.TargetGroups ?? new List<ParamPropagateService.ParameterGroupOption>()).Select(g => new { id = g.Id, name = g.Name }).ToList()
                };
                SendToWeb("sharedparam:list", shaped);
            }
            catch (Exception ex)
            {
                SendToWeb("sharedparam:list", new { ok = false, message = ex.Message });
                SendToWeb("revit:error", new { message = ex.Message });
            }
        }

        private void HandleSharedParamRun(UIApplication app, object payload)
        {
            try
            {
                var req = ParamPropagateService.SharedParamRunRequest.FromPayload(payload);
                var res = ParamPropagateService.Run(app, req);
                _lastParamResult = res;

                var status = res?.Status ?? ParamPropagateService.RunStatus.Failed;
                var ok = status == ParamPropagateService.RunStatus.Succeeded;

                var responsePayload = new Dictionary<string, object>
                {
                    ["ok"] = ok,
                    ["status"] = status.ToString().ToLowerInvariant(),
                    ["message"] = res?.Message,
                    ["report"] = res?.Report,
                    ["details"] = (res?.Details ?? new List<ParamPropagateService.SharedParamDetailRow>()).Select(d => new { kind = d.Kind, family = d.Family, detail = d.Detail }).ToList()
                };

                SendToWeb("sharedparam:done", responsePayload);
                SendToWeb("paramprop:done", responsePayload);

                if (!ok) SendToWeb("revit:error", new { message = "공유 파라미터 연동 실패: " + (res?.Message ?? "실패") });
            }
            catch (Exception ex)
            {
                SendToWeb("sharedparam:done", new { ok = false, status = "failed", message = ex.Message });
                SendToWeb("paramprop:done", new { ok = false, status = "failed", message = ex.Message });
                SendToWeb("revit:error", new { message = "공유 파라미터 연동 실패: " + ex.Message });
            }
        }

        private void HandleSharedParamExport(UIApplication app, object payload)
        {
            try
            {
                if (_lastParamResult?.Details == null || _lastParamResult.Details.Count == 0)
                {
                    SendToWeb("sharedparam:exported", new { ok = false, message = "최근 실행 결과가 없습니다." });
                    return;
                }

                var saved = ParamPropagateService.ExportResultToExcel(_lastParamResult);
                if (string.IsNullOrWhiteSpace(saved))
                {
                    SendToWeb("sharedparam:exported", new { ok = false, message = "엑셀 저장이 취소되었습니다." });
                    return;
                }

                SendToWeb("sharedparam:exported", new { ok = true, path = saved });
            }
            catch (Exception ex)
            {
                SendToWeb("sharedparam:exported", new { ok = false, message = ex.Message });
                SendToWeb("revit:error", new { message = "엑셀 저장 실패: " + ex.Message });
            }
        }
    }
}
