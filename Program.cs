using System;
using System.IO;

namespace Dls24Editor
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string workspace = AppContext.BaseDirectory;
            string tools = Path.Combine(workspace, "DLS24-Editor");
            string package = args.Length > 0 ? Path.GetFullPath(args[0]) : DefaultPackage(workspace);
            EditorWindow.Run(workspace, tools, package);
        }

        private static string DefaultPackage(string workspace)
        {
            string package = Path.Combine(workspace, "DLS24_100M_ARM64_ARMV7", "DLS24_100M_ARM64_ARMV7.zip");
            return File.Exists(package) ? package : null;
        }
    }
}
