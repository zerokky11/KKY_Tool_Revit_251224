using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace KKY_Tool_Revit
{
    public sealed class ResourceExtractor
    {
        private ResourceExtractor() { }

        public static void EnsureExtractedUI(string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            var asm = Assembly.GetExecutingAssembly();
            var resNames = asm.GetManifestResourceNames();

            var resName = resNames.FirstOrDefault(n => n.EndsWith("HubUI.zip", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(resName))
            {
                resName = resNames.FirstOrDefault(n => n.IndexOf("hubui", StringComparison.OrdinalIgnoreCase) >= 0 && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            }

            Stream stream = null;
            if (!string.IsNullOrEmpty(resName)) stream = asm.GetManifestResourceStream(resName);

            var asmDir = Path.GetDirectoryName(asm.Location) ?? string.Empty;
            var diskCandidates = new[]
            {
                Path.Combine(asmDir, "Resources", "HubUI.zip"),
                Path.Combine(asmDir, "HubUI.zip")
            };

            if (stream == null)
            {
                foreach (var p in diskCandidates)
                {
                    if (!File.Exists(p)) continue;
                    stream = File.OpenRead(p);
                    break;
                }
            }

            if (stream == null)
            {
                var msg = new StringBuilder();
                msg.AppendLine("임베디드 리소스 'HubUI.zip'을 찾지 못했습니다.");
                msg.AppendLine("어셈블리: " + asm.Location);
                msg.AppendLine("임베디드 목록(최대 20개):");
                foreach (var n in resNames.Take(20)) msg.AppendLine(" - " + n);
                msg.AppendLine("디스크에서 시도한 경로:");
                foreach (var p in diskCandidates) msg.AppendLine(" - " + p);
                throw new FileNotFoundException(msg.ToString());
            }

            using (stream)
            {
                var newHash = Sha256Hex(stream);
                var stamp = Path.Combine(targetDir, ".ui_hash");

                var need = true;
                if (File.Exists(stamp))
                {
                    try
                    {
                        if (string.Equals(File.ReadAllText(stamp, Encoding.UTF8), newHash, StringComparison.OrdinalIgnoreCase)) need = false;
                    }
                    catch { }
                }

                if (need)
                {
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
                        {
                            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                        }
                        Directory.Delete(targetDir, true);
                    }
                    catch { }

                    Directory.CreateDirectory(targetDir);

                    stream.Position = 0;
                    using (var z = new ZipArchive(stream, ZipArchiveMode.Read, false))
                    {
                        z.ExtractToDirectory(targetDir);
                    }

                    File.WriteAllText(stamp, newHash, Encoding.UTF8);
                }
            }
        }

        private static string Sha256Hex(Stream stream)
        {
            stream.Position = 0;
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
