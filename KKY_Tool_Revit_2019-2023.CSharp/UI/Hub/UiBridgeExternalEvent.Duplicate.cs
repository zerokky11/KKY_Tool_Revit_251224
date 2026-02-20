using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KKY_Tool_Revit.Services;

namespace KKY_Tool_Revit.UI.Hub
{
    public partial class UiBridgeExternalEvent
    {
        private class DupRowState
        {
            public int ElementId { get; set; }
            public string Category { get; set; }
            public string Family { get; set; }
            public string Type { get; set; }
            public int ConnectedCount { get; set; }
            public string ConnectedIds { get; set; }
            public bool Candidate { get; set; }
            public bool Deleted { get; set; }
        }

        private static readonly List<DupRowState> _lastRows = new List<DupRowState>();
        private static readonly Stack<List<int>> _deleteOps = new Stack<List<int>>();

        private void HandleDupRun(UIApplication app, object payload)
        {
            var rows = DuplicateAnalysisService.Run(app);
            _lastRows.Clear();

            foreach (var r in rows)
            {
                _lastRows.Add(new DupRowState
                {
                    ElementId = SafeInt(r.TryGetValue("id", out var id) ? id : null),
                    Category = Convert.ToString(r.TryGetValue("category", out var c) ? c : "", CultureInfo.InvariantCulture),
                    Family = Convert.ToString(r.TryGetValue("family", out var f) ? f : "", CultureInfo.InvariantCulture),
                    Type = Convert.ToString(r.TryGetValue("type", out var t) ? t : "", CultureInfo.InvariantCulture),
                    ConnectedCount = SafeInt(r.TryGetValue("connectedCount", out var cc) ? cc : null),
                    ConnectedIds = Convert.ToString(r.TryGetValue("connectedIds", out var ci) ? ci : "", CultureInfo.InvariantCulture),
                    Candidate = SafeBool(r.TryGetValue("candidate", out var cd) ? cd : null),
                    Deleted = false
                });
            }

            var wireRows = _lastRows.Select(r => new
            {
                elementId = r.ElementId,
                category = r.Category,
                family = r.Family,
                type = r.Type,
                connectedCount = r.ConnectedCount,
                connectedIds = r.ConnectedIds,
                candidate = r.Candidate,
                deleted = r.Deleted
            }).ToList();

            var groupsWithDup = _lastRows.GroupBy(r => $"{r.Category}|{r.Family}|{r.Type}").Count(g => g.Count() > 1);
            SendToWeb("dup:list", wireRows);
            SendToWeb("dup:result", new { scan = wireRows.Count, groups = groupsWithDup, candidates = wireRows.Count(r => r.candidate) });
        }

        private void HandleDuplicateSelect(UIApplication app, object payload)
        {
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc?.Document == null) return;

            var idVal = SafeInt(GetProp(payload, "id"));
            if (idVal <= 0) return;

            var elId = new ElementId(idVal);
            var el = uiDoc.Document.GetElement(elId);
            if (el == null)
            {
                SendToWeb("host:warn", new { message = $"요소 {idVal} 을(를) 찾을 수 없습니다." });
                return;
            }

            try { uiDoc.Selection.SetElementIds(new List<ElementId> { elId }); } catch { }

            try
            {
                var bb = el.get_BoundingBox(uiDoc.ActiveView) ?? el.get_BoundingBox(null);
                if (bb != null)
                {
                    var target = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId.IntegerValue == uiDoc.ActiveView.Id.IntegerValue);
                    if (target != null) target.ZoomAndCenterRectangle(bb.Min, bb.Max);
                    else uiDoc.ShowElements(elId);
                }
                else
                {
                    uiDoc.ShowElements(elId);
                }
            }
            catch { }
        }

        private void HandleDuplicateDelete(UIApplication app, object payload)
        {
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                SendToWeb("revit:error", new { message = "활성 문서를 찾을 수 없습니다." });
                return;
            }

            var doc = uiDoc.Document;
            var ids = ExtractIds(payload);
            if (ids.Count == 0)
            {
                SendToWeb("revit:error", new { message = "잘못된 요청입니다(id 누락/형식 오류)." });
                return;
            }

            var eidList = ids.Where(i => i > 0).Select(i => new ElementId(i)).Where(eid => doc.GetElement(eid) != null).ToList();
            if (eidList.Count == 0)
            {
                SendToWeb("host:warn", new { message = "삭제할 유효한 요소가 없습니다." });
                return;
            }

            using (var t = new Transaction(doc, $"KKY Dup Delete ({eidList.Count})"))
            {
                t.Start();
                try
                {
                    doc.Delete(eidList);
                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    SendToWeb("revit:error", new { message = $"삭제 실패({eidList.Count}개): {ex.Message}" });
                    return;
                }
            }

            var actuallyDeleted = new List<int>();
            foreach (var eid in eidList)
            {
                if (doc.GetElement(eid) == null)
                {
                    actuallyDeleted.Add(eid.IntegerValue);
                    var row = _lastRows.FirstOrDefault(r => r.ElementId == eid.IntegerValue);
                    if (row != null) row.Deleted = true;
                    SendToWeb("dup:deleted", new { id = eid.IntegerValue });
                }
            }

            if (actuallyDeleted.Count > 0) _deleteOps.Push(actuallyDeleted);
        }

        private void HandleDuplicateRestore(UIApplication app, object payload)
        {
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                SendToWeb("revit:error", new { message = "활성 문서를 찾을 수 없습니다." });
                return;
            }

            if (_deleteOps.Count == 0)
            {
                SendToWeb("host:warn", new { message = "되돌릴 수 있는 최신 삭제가 없습니다." });
                return;
            }

            var requestIds = ExtractIds(payload);
            var lastPack = _deleteOps.Peek();
            var same = requestIds.Count == lastPack.Count && !requestIds.Except(lastPack).Any();
            if (!same)
            {
                SendToWeb("host:warn", new { message = "되돌리기는 직전 삭제 묶음만 가능합니다." });
                return;
            }

            try
            {
                var cmdId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
                if (cmdId == null) throw new InvalidOperationException("Undo 명령을 찾을 수 없습니다.");
                uiDoc.Application.PostCommand(cmdId);
            }
            catch (Exception ex)
            {
                SendToWeb("revit:error", new { message = $"되돌리기 실패: {ex.Message}" });
                return;
            }

            _deleteOps.Pop();
            foreach (var i in lastPack)
            {
                var row = _lastRows.FirstOrDefault(x => x.ElementId == i);
                if (row != null) row.Deleted = false;
                SendToWeb("dup:restored", new { id = i });
            }
        }

        private void HandleDuplicateExport(UIApplication app, object payload)
        {
            if (_lastRows.Count == 0)
            {
                SendToWeb("host:warn", new { message = "내보낼 데이터가 없습니다." });
                return;
            }

            var token = Convert.ToString(GetProp(payload, "token"), CultureInfo.InvariantCulture);
            try
            {
                var groupsCount = _lastRows.GroupBy(r => $"{r.Category}|{r.Family}|{r.Type}").Count();
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var defaultFileName = $"{DateTime.Now:yyMMdd}_중복객체 검토결과_{groupsCount}개.xlsx";

                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    FileName = defaultFileName,
                    AddExtension = true,
                    DefaultExt = "xlsx",
                    OverwritePrompt = true
                };

                if (sfd.ShowDialog() != true) return;

                var outPath = sfd.FileName;
                KKY_Tool_Revit.Exports.DuplicateExport.Save(outPath, _lastRows.Cast<object>());
                SendToWeb("dup:exported", new { path = outPath, ok = true, token });
            }
            catch (IOException)
            {
                SendToWeb("dup:exported", new { ok = false, message = "해당 파일이 열려 있어 저장에 실패했습니다. 엑셀에서 파일을 닫은 뒤 다시 시도해 주세요.", token });
            }
            catch (Exception ex)
            {
                SendToWeb("dup:exported", new { ok = false, message = $"엑셀 저장에 실패했습니다: {ex.Message}", token });
            }
        }

        private static List<int> ExtractIds(object payload)
        {
            var result = new List<int>();
            if (payload == null) return result;

            var id = SafeInt(GetProp(payload, "id"));
            if (id > 0) result.Add(id);

            var idsObj = GetProp(payload, "ids");
            if (idsObj is IEnumerable ie)
            {
                foreach (var x in ie)
                {
                    var v = SafeInt(x);
                    if (v > 0) result.Add(v);
                }
            }

            return result.Distinct().ToList();
        }

        private static int SafeInt(object v)
        {
            if (v == null) return 0;
            if (int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            return 0;
        }

        private static bool SafeBool(object v)
        {
            if (v == null) return false;
            if (v is bool b) return b;
            if (bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var p)) return p;
            return false;
        }
    }
}
