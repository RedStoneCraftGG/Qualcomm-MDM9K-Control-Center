using System;
using System.IO;

namespace ModemController
{
    internal static class AppIcon
    {
        public const string PngFileName = "AppIccon.png";
        public const string IcoFileName = "App.ico";

        public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Assets");

        public static string PngPath => Path.Combine(DirectoryPath, PngFileName);

        public static string IcoPath => Path.Combine(DirectoryPath, IcoFileName);

        public static bool TryGetPngUri(out Uri uri)
        {
            uri = new Uri(PngPath, UriKind.Absolute);
            return File.Exists(PngPath);
        }

        public static bool TryGetIcoPath(out string path)
        {
            path = IcoPath;
            return File.Exists(path);
        }
    }
}
