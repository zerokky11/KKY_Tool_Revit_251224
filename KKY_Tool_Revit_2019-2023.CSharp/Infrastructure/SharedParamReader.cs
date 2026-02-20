using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace KKY_Tool_Revit.Infrastructure
{
    public sealed class SharedParamStatus
    {
        public string status { get; set; } = "warn";
        public string path { get; set; } = string.Empty;
        public bool existsOnDisk { get; set; }
        public bool canOpen { get; set; }
        public bool isSet { get; set; }
        public string warning { get; set; } = string.Empty;
        public string errorMessage { get; set; } = string.Empty;
    }

    public sealed class SharedParamReadResult
    {
        public SharedParamStatus Status { get; set; } = new SharedParamStatus();
        public Dictionary<string, List<Guid>> NameToGuids { get; } = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
    }

    public static class SharedParamReader
    {
        public static SharedParamReadResult Read(Autodesk.Revit.ApplicationServices.Application app)
        {
            var result = new SharedParamReadResult();
            var path = app?.SharedParametersFilename ?? string.Empty;

            result.Status.path = path;
            result.Status.isSet = !string.IsNullOrWhiteSpace(path);

            if (!result.Status.isSet)
            {
                result.Status.status = "warn";
                result.Status.warning = "Shared Parameters 경로가 설정되지 않았습니다.";
                return result;
            }

            result.Status.existsOnDisk = File.Exists(path);
            if (!result.Status.existsOnDisk)
            {
                result.Status.status = "warn";
                result.Status.warning = "Shared Parameters 파일이 디스크에 없습니다.";
                return result;
            }

            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("PARAM\t", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var parts = line.Split('\t');
                    if (parts.Length < 4)
                    {
                        continue;
                    }

                    if (!Guid.TryParse(parts[1], out var guid))
                    {
                        continue;
                    }

                    var name = parts[2]?.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!result.NameToGuids.TryGetValue(name, out var list))
                    {
                        list = new List<Guid>();
                        result.NameToGuids[name] = list;
                    }

                    if (!list.Contains(guid))
                    {
                        list.Add(guid);
                    }
                }

                result.Status.canOpen = true;
                result.Status.status = "ok";
            }
            catch (Exception ex)
            {
                result.Status.status = "error";
                result.Status.errorMessage = ex.Message;
            }

            return result;
        }
    }
}
