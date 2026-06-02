using System;
using System.IO;

namespace MDDFoundation
{
    public enum FoundationAppLayout
    {
        Normal,
        LauncherVersionDirectory
    }

    public sealed class FoundationAppPathInfo
    {
        internal FoundationAppPathInfo(string appBaseDirectory, string appRootDirectory, FoundationAppLayout appLayout)
        {
            AppBaseDirectory = appBaseDirectory;
            AppRootDirectory = appRootDirectory;
            AppLayout = appLayout;
            ConfigDirectory = Path.Combine(appRootDirectory, "config");
            LogDirectory = appLayout == FoundationAppLayout.LauncherVersionDirectory
                ? Path.Combine(appRootDirectory, "logs")
                : appBaseDirectory;
        }

        public string AppBaseDirectory { get; }
        public string AppRootDirectory { get; }
        public string ConfigDirectory { get; }
        public string LogDirectory { get; }
        public FoundationAppLayout AppLayout { get; }
        public bool IsLauncherManaged => AppLayout == FoundationAppLayout.LauncherVersionDirectory;
    }

    public static class FoundationAppPaths
    {
        public static FoundationAppPathInfo Current => Resolve(AppContext.BaseDirectory);

        public static FoundationAppPathInfo Resolve(string appBaseDirectory)
        {
            appBaseDirectory = NormalizeDirectory(appBaseDirectory);
            var baseDir = new DirectoryInfo(appBaseDirectory);
            var versionsDir = baseDir.Parent;
            var appRootDir = versionsDir?.Parent;

            if (versionsDir != null
                && appRootDir != null
                && string.Equals(versionsDir.Name, "versions", StringComparison.OrdinalIgnoreCase)
                && !IsPathRootDirectory(appRootDir.FullName))
            {
                return new FoundationAppPathInfo(NormalizeDirectory(appBaseDirectory),
                    NormalizeDirectory(appRootDir.FullName), FoundationAppLayout.LauncherVersionDirectory);
            }

            return new FoundationAppPathInfo(appBaseDirectory, appBaseDirectory, FoundationAppLayout.Normal);
        }

        public static string ResolveConfigFile(string fileName, string configDirectoryName = "config")
        {
            if (Path.IsPathRooted(fileName))
            {
                return Path.GetFullPath(fileName);
            }

            var paths = Current;
            return Path.GetFullPath(Path.Combine(paths.AppRootDirectory, configDirectoryName, fileName));
        }

        public static string ResolveLogFile(string fileName)
        {
            if (Path.IsPathRooted(fileName))
            {
                return Path.GetFullPath(fileName);
            }

            return Path.GetFullPath(Path.Combine(Current.LogDirectory, fileName));
        }

        static bool IsPathRootDirectory(string path)
        {
            var fullPath = NormalizeDirectory(path);
            var root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root)
                && string.Equals(TrimDirectorySeparators(fullPath), TrimDirectorySeparators(root),
                    StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeDirectory(string path)
        {
            return Path.GetFullPath(path);
        }

        static string TrimDirectorySeparators(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
