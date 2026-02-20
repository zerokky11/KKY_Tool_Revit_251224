using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using KKY_Tool_Revit.UI.Hub;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class DuplicateExport : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            HubHostWindow.ShowSingleton(commandData.Application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
