using Autodesk.Revit.DB;

namespace KKY_Tool_Revit.Infrastructure
{
    internal static class ElementIdCompat
    {
        public static int IntValue(this ElementId id)
        {
            if (id == null) return -1;
            return id.IntegerValue;
        }

        public static ElementId FromInt(int id)
        {
            return new ElementId(id);
        }

        public static ElementId FromLong(long id)
        {
            return new ElementId((int)id);
        }
    }
}
