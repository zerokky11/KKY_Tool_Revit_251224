using System.Data;
using KKY_Tool_Revit.Infrastructure;

namespace KKY_Tool_Revit.Exports
{
    public static class PointsExport
    {
        public static string SaveWithDialog(DataTable resultTable)
        {
            if (resultTable == null) return string.Empty;
            return ExcelCore.PickAndSaveXlsx("Exported Points", resultTable, "ExportPoints.xlsx");
        }

        public static void Save(string outPath, DataTable resultTable)
        {
            if (resultTable == null) return;
            ExcelCore.SaveXlsx(outPath, "Exported Points", resultTable);
        }
    }
}
