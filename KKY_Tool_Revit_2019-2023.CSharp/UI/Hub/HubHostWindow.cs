using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KKY_Tool_Revit.UI.Hub
{
    public class HubHostWindow : Window
    {
        private const string BaseTitle = "KKY Tool Hub";

        private static HubHostWindow _instance;
        private static readonly object _gate = new object();

        private readonly WebView2 _web = new WebView2();
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        private UIApplication _uiApp;
        private string _currentDocName = string.Empty;
        private string _currentDocPath = string.Empty;
        private bool _initStarted;
        private bool _isClosing;

        public WebView2 Web => _web;
        public bool IsClosing => _isClosing;

        public static void ShowSingleton(UIApplication uiApp)
        {
            if (uiApp == null) return;

            lock (_gate)
            {
                if (_instance != null && !_instance.IsClosing)
                {
                    _instance.AttachTo(uiApp);
                    UiBridgeExternalEvent.Initialize(_instance);
                    if (_instance.WindowState == WindowState.Minimized)
                    {
                        _instance.WindowState = WindowState.Normal;
                    }

                    _instance.Activate();
                    _instance.Focus();
                    return;
                }

                var wnd = new HubHostWindow(uiApp);
                UiBridgeExternalEvent.Initialize(wnd);
                _instance = wnd;
                wnd.Show();
            }
        }

        public static void NotifyActiveDocumentChanged(Document doc)
        {
            var inst = _instance;
            if (inst == null || inst.IsClosing) return;
            inst.UpdateActiveDocument(doc);
        }

        public static void NotifyDocumentListChanged()
        {
            var inst = _instance;
            if (inst == null || inst.IsClosing) return;
            inst.BroadcastDocumentList();
        }

        public HubHostWindow(UIApplication uiApp)
        {
            _uiApp = uiApp;
            Title = BaseTitle;
            Width = 1280;
            Height = 800;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Content = _web;

            Loaded += OnLoaded;
            Closing += OnWindowClosing;
            Closed += OnWindowClosed;

            UpdateActiveDocument(GetActiveDocument());
        }

        public void AttachTo(UIApplication uiApp)
        {
            _uiApp = uiApp;
            UpdateActiveDocument(GetActiveDocument());
            BroadcastDocumentList();
        }

        private Document GetActiveDocument()
        {
            try
            {
                return _uiApp?.ActiveUIDocument?.Document;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateActiveDocument(Document doc)
        {
            var name = string.Empty;
            var path = string.Empty;

            if (doc != null)
            {
                try { name = doc.Title; } catch { }
                try { path = doc.PathName; } catch { }
            }

            if (string.IsNullOrWhiteSpace(path)) path = name;

            _currentDocName = name;
            _currentDocPath = path;

            UpdateWindowTitle();
            SendActiveDocument();
        }

        private void UpdateWindowTitle()
        {
            Title = string.IsNullOrWhiteSpace(_currentDocName) ? BaseTitle : $"{BaseTitle} - {_currentDocName}";
        }

        private static string ResolveUiFolder()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var baseDir = Path.GetDirectoryName(asm.Location);
                var ui = Path.Combine(baseDir ?? string.Empty, "Resources", "HubUI");
                if (Directory.Exists(ui)) return Path.GetFullPath(ui);
            }
            catch { }

            return null;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initStarted) return;
            _initStarted = true;

            try
            {
                var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KKY_Tool_Revit", "WebView2UserData");
                Directory.CreateDirectory(userData);

                var env = await CoreWebView2Environment.CreateAsync(null, userData, null);
                await _web.EnsureCoreWebView2Async(env);
                var core = _web.CoreWebView2;

                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
#if DEBUG
                core.Settings.AreDevToolsEnabled = true;
#else
                core.Settings.AreDevToolsEnabled = false;
#endif

                var uiFolder = ResolveUiFolder();
                if (string.IsNullOrEmpty(uiFolder))
                {
                    throw new DirectoryNotFoundException("Resources\\HubUI 폴더를 찾을 수 없습니다.");
                }

                core.SetVirtualHostNameToFolderMapping("hub.local", uiFolder, CoreWebView2HostResourceAccessKind.Allow);
                core.WebMessageReceived += OnWebMessage;

                _web.Source = new Uri("https://hub.local/index.html");

                SendToWeb("host:topmost", new { on = Topmost });
                SendActiveDocument();
                BroadcastDocumentList();
            }
            catch (Exception ex)
            {
                var hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex);
                MessageBox.Show($"WebView 초기화 실패 (0x{hr:X8}) : {ex.Message}", "KKY Tool", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            _isClosing = true;
        }

        private void OnWindowClosed(object sender, EventArgs e)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var root = _serializer.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);

                string name = null;
                if (root != null)
                {
                    if (root.ContainsKey("ev") && root["ev"] != null) name = Convert.ToString(root["ev"]);
                    else if (root.ContainsKey("name") && root["name"] != null) name = Convert.ToString(root["name"]);
                }

                if (string.IsNullOrEmpty(name)) return;

                object payload = null;
                if (root != null && root.ContainsKey("payload")) payload = root["payload"];

                switch (name)
                {
                    case "ui:ping":
                        SendToWeb("host:pong", new { t = DateTime.Now.Ticks });
                        break;
                    case "ui:toggle-topmost":
                        Topmost = !Topmost;
                        SendToWeb("host:topmost", new { on = Topmost });
                        break;
                    case "ui:query-topmost":
                        SendToWeb("host:topmost", new { on = Topmost });
                        break;
                    default:
                        UiBridgeExternalEvent.Raise(name, payload);
                        break;
                }
            }
            catch (Exception ex)
            {
                SendToWeb("host:error", new { message = ex.Message });
            }
        }

        private void BroadcastDocumentList()
        {
            var docs = new List<object>();
            try
            {
                var app = _uiApp?.Application;
                if (app != null)
                {
                    foreach (Document d in app.Documents)
                    {
                        try
                        {
                            var name = d.Title;
                            var path = d.PathName;
                            if (string.IsNullOrWhiteSpace(path)) path = name;
                            docs.Add(new { name, path });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            SendToWeb("host:doc-list", docs);
        }

        private void SendActiveDocument()
        {
            SendToWeb("host:doc-changed", new { name = _currentDocName, path = _currentDocPath });
        }

        public void SendToWeb(string ev, object payload)
        {
            var core = _web.CoreWebView2;
            if (core == null) return;

            var msg = new Dictionary<string, object>
            {
                ["ev"] = ev,
                ["name"] = ev,
                ["payload"] = payload
            };

            core.PostWebMessageAsJson(_serializer.Serialize(msg));
        }
    }
}
