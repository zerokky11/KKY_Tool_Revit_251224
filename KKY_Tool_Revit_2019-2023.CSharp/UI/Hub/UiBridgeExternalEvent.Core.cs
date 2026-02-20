using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.UI;

namespace KKY_Tool_Revit.UI.Hub
{
    public partial class UiBridgeExternalEvent
    {
        internal static HubHostWindow _host;
        private static readonly UiBridgeExternalEvent _self = new UiBridgeExternalEvent();
        private static readonly object _gate = new object();
        private static readonly Queue<Action<UIApplication>> _queue = new Queue<Action<UIApplication>>();

        private static ExternalEvent _extEv;
        private static IExternalEventHandler _handler;

        public static void Initialize(HubHostWindow host)
        {
            _host = host;

            if (_extEv == null)
            {
                _handler = new BridgeHandler(ProcessQueue);
                _extEv = ExternalEvent.Create(_handler);
            }

            BroadcastTopmost();
            SendToWeb("host:connected", new { ok = true });
        }

        public static void Raise(string name, object payload)
        {
            Enqueue(app => Dispatch(app, name, payload));
        }

        private static void Enqueue(Action<UIApplication> work)
        {
            lock (_gate)
            {
                _queue.Enqueue(work);
            }

            _extEv?.Raise();
        }

        private static void ProcessQueue(UIApplication app)
        {
            while (true)
            {
                Action<UIApplication> todo;
                lock (_gate)
                {
                    todo = _queue.Count > 0 ? _queue.Dequeue() : null;
                }

                if (todo == null) break;

                try { todo(app); }
                catch (Exception ex) { SendToWeb("host:error", new { message = ex.Message }); }
            }
        }

        private static void Dispatch(UIApplication app, string name, object payload)
        {
            switch (name)
            {
                case "ui:query-topmost":
                    BroadcastTopmost();
                    return;
                case "ui:set-topmost":
                    var turnOn = false;
                    try
                    {
                        var raw = GetProp(payload, "on");
                        if (raw != null) turnOn = Convert.ToBoolean(raw);
                    }
                    catch { }

                    try
                    {
                        if (_host != null) _host.Topmost = turnOn;
                    }
                    catch { }

                    BroadcastTopmost();
                    return;
                case "ui:toggle-topmost":
                    try
                    {
                        if (_host != null) _host.Topmost = !_host.Topmost;
                    }
                    catch { }

                    BroadcastTopmost();
                    return;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dup:run"] = "HandleDupRun",
                ["duplicate:export"] = "HandleDuplicateExport",
                ["duplicate:delete"] = "HandleDuplicateDelete",
                ["duplicate:restore"] = "HandleDuplicateRestore",
                ["duplicate:select"] = "HandleDuplicateSelect",

                ["connector:run"] = "HandleConnectorRun",
                ["connector:save-excel"] = "HandleConnectorSaveExcel",

                ["export:browse-folder"] = "HandleExportBrowse",
                ["export:preview"] = "HandleExportPreview",
                ["export:save-excel"] = "HandleExportSaveExcel",

                ["paramprop:run"] = "HandleSharedParamRun",
                ["sharedparam:run"] = "HandleSharedParamRun",
                ["sharedparam:list"] = "HandleSharedParamList",
                ["sharedparam:export-excel"] = "HandleSharedParamExport",

                ["excel:open"] = "HandleExcelOpen",

                ["segmentpms:register-pms"] = "HandleSegmentPmsRegister",
                ["segmentpms:load-defaultmap"] = "HandleSegmentPmsLoadDefault",
                ["segmentpms:extract"] = "HandleSegmentPmsExtract",
                ["segmentpms:save-extract"] = "HandleSegmentPmsSaveExtract",
                ["segmentpms:open-extract"] = "HandleSegmentPmsOpenExtract",
                ["segmentpms:prepare"] = "HandleSegmentPmsPrepare",
                ["segmentpms:run"] = "HandleSegmentPmsRun",
                ["segmentpms:save-excel"] = "HandleSegmentPmsSaveExcel",

                ["guid:add-files"] = "HandleGuidAddFiles",
                ["guid:run"] = "HandleGuidRun",
                ["guid:request-family-detail"] = "HandleGuidRequestFamilyDetail",
                ["guid:export"] = "HandleGuidExport",
                ["sharedparam:status"] = "HandleSharedParamStatus"
            };

            if (!map.TryGetValue(name, out var methodName))
            {
                SendToWeb("host:warn", new { message = $"알 수 없는 이벤트 '{name}'" });
                return;
            }

            var m = typeof(UiBridgeExternalEvent).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null)
            {
                SendToWeb("host:warn", new { message = $"핸들러 '{methodName}' 가 구현되어 있지 않습니다." });
                return;
            }

            var ps = m.GetParameters();
            object[] args;
            if (ps.Length == 2) args = new object[] { app, payload };
            else if (ps.Length == 1) args = ps[0].ParameterType == typeof(UIApplication) ? new object[] { app } : new[] { payload };
            else args = Array.Empty<object>();

            try
            {
                m.Invoke(_self, args);
            }
            catch (TargetInvocationException ex)
            {
                var msg = ex.InnerException?.Message ?? ex.Message;
                SendToWeb("host:error", new { message = $"핸들러 실행 오류({methodName}): {msg}" });
            }
            catch (Exception ex)
            {
                SendToWeb("host:error", new { message = $"핸들러 실행 오류({methodName}): {ex.Message}" });
            }
        }

        internal static void SendToWeb(string channel, object payload)
        {
            try { _host?.SendToWeb(channel, payload); } catch { }
        }

        private static void BroadcastTopmost()
        {
            try
            {
                var onTop = _host != null && _host.Topmost;
                SendToWeb("host:topmost", new { on = onTop });
            }
            catch { }
        }

        private static object GetProp(object obj, string prop)
        {
            if (obj == null) return null;

            if (obj is IDictionary<string, object> d)
            {
                return d.TryGetValue(prop, out var v) ? v : null;
            }

            var p = obj.GetType().GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return p?.GetValue(obj, null);
        }

        public static void HostLog(string kind, string text)
        {
            SendToWeb("host:log", new { kind, text });
        }

        private void HandleExcelOpen(object payload)
        {
            try
            {
                var path = GetProp(payload, "path") as string;
                if (string.IsNullOrWhiteSpace(path)) return;
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SendToWeb("host:error", new { message = "엑셀을 열 수 없습니다: " + ex.Message });
            }
        }

        private void HandleSwitchDocument(UIApplication app, object payload)
        {
            var name = GetProp(payload, "name") as string;
            if (string.IsNullOrWhiteSpace(name)) name = GetProp(payload, "path") as string;

            SendToWeb("host:info", new
            {
                message = "문서 전환은 Revit 창에서 직접 선택해 주세요.",
                target = name
            });
        }

        // GUID 기능은 후속 변환에서 구현 예정인 핸들러의 안전 기본값 제공
        private void HandleGuidAddFiles(UIApplication app, object payload) => SendToWeb("guid:warn", new { message = "guid:add-files 변환 진행중" });
        private void HandleGuidExport(UIApplication app, object payload) => SendToWeb("guid:warn", new { message = "guid:export 변환 진행중" });
        private void HandleSharedParamStatus(UIApplication app, object payload) => HandleGuidRun(app, payload);
    }

    internal class BridgeHandler : IExternalEventHandler
    {
        private readonly Action<UIApplication> _run;
        public BridgeHandler(Action<UIApplication> run) { _run = run; }
        public void Execute(UIApplication uiApp) { _run?.Invoke(uiApp); }
        public string GetName() => "KKY Hub Bridge";
    }
}
