using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using KKY_Tool_Revit.UI.Hub;

namespace KKY_Tool_Revit.KKY_Tool_Revit
{
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication a)
        {
            const string tabName = "KKY Tools";
            const string panelName = "Hub";

            try { a.CreateRibbonTab(tabName); } catch { }

            var ribbonPanel = a.GetRibbonPanels(tabName).FirstOrDefault(p => p.Name == panelName) ?? a.CreateRibbonPanel(tabName, panelName);

            var asmPath = Assembly.GetExecutingAssembly().Location;
            var cmdFullName = typeof(DuplicateExport).FullName;
            var pbd = new PushButtonData("KKY_Hub_Button", "KKY Hub", asmPath, cmdFullName);

            var btn = ribbonPanel.AddItem(pbd) as PushButton;
            if (btn != null)
            {
                btn.ToolTip = "KKY Tool 허브 열기";
                btn.Image = LoadPng("KKY_Tool_Revit.Resources.icons.hub_16.png");
                btn.LargeImage = LoadPng("KKY_Tool_Revit.Resources.icons.hub_32.png");
            }

            a.ViewActivated += OnViewActivated;
            a.ControlledApplication.DocumentOpened += OnDocumentListChanged;
            a.ControlledApplication.DocumentClosed += OnDocumentListChanged;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a)
        {
            try
            {
                a.ViewActivated -= OnViewActivated;
                a.ControlledApplication.DocumentOpened -= OnDocumentListChanged;
                a.ControlledApplication.DocumentClosed -= OnDocumentListChanged;
            }
            catch { }

            return Result.Succeeded;
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try { HubHostWindow.NotifyActiveDocumentChanged(e.Document); } catch { }
        }

        private static void OnDocumentListChanged(object sender, EventArgs e)
        {
            try { HubHostWindow.NotifyDocumentListChanged(); } catch { }
        }

        private static ImageSource LoadPng(string resName)
        {
            var asm = Assembly.GetExecutingAssembly();
            var resolved = ResolveResourceName(asm, resName);
            if (string.IsNullOrEmpty(resolved)) return null;

            using (var s = asm.GetManifestResourceStream(resolved))
            {
                if (s == null) return null;
                var decoder = new PngBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var src = decoder.Frames[0];
                src.Freeze();
                return src;
            }
        }

        private static string ResolveResourceName(Assembly asm, string desired)
        {
            var names = asm.GetManifestResourceNames();
            var exact = names.FirstOrDefault(n => string.Equals(n, desired, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(exact)) return exact;

            var fileName = Path.GetFileName(desired);
            return names.FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
