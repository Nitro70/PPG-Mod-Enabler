using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PPGModEnabler
{
    internal sealed class GameInfo
    {
        public string GameDir;
        public string WorkshopDir;   // null when there is no Workshop folder
        public string BuildId;       // null when the Steam manifest could not be read

        public string ManagedDir => Path.Combine(GameDir, "People Playground_Data", "Managed");
        public string AssemblyPath => Path.Combine(ManagedDir, "Assembly-CSharp.dll");
        public string OriginalCopyPath => AssemblyPath + ".ppgme-original";
        public string ModsDir => Path.Combine(GameDir, "Mods");
        public string ContraptionsDir => Path.Combine(GameDir, "Contraptions");
    }

    internal static class GameLocator
    {
        public const string AppId = "1118200";
        public const string FolderName = "People Playground";

        public static bool IsGameDir(string dir)
        {
            return !string.IsNullOrEmpty(dir)
                && File.Exists(Path.Combine(dir, "People Playground.exe"))
                && File.Exists(Path.Combine(dir, "People Playground_Data", "Managed", "Assembly-CSharp.dll"));
        }

        public static GameInfo Describe(string gameDir, string workshopOverride)
        {
            gameDir = Path.GetFullPath(gameDir.Trim().Trim('"'));
            var info = new GameInfo { GameDir = gameDir };

            string steamapps = SteamappsFolderOf(gameDir);
            if (workshopOverride != null)
            {
                info.WorkshopDir = workshopOverride.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : Path.GetFullPath(workshopOverride);
            }
            else if (steamapps != null)
            {
                string ws = Path.Combine(steamapps, "workshop", "content", AppId);
                info.WorkshopDir = Directory.Exists(ws) ? ws : null;
            }

            if (steamapps != null)
                info.BuildId = ReadBuildId(Path.Combine(steamapps, "appmanifest_" + AppId + ".acf"));
            return info;
        }

        /// <summary>Looks through every Steam library on this PC for the game.</summary>
        public static string FindInstalledGame()
        {
            foreach (string library in SteamLibraries())
            {
                string candidate = Path.Combine(library, "steamapps", "common", FolderName);
                if (IsGameDir(candidate))
                    return candidate;
            }
            return null;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var steamRoots = new List<string>();
            AddIfDir(steamRoots, Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
            AddIfDir(steamRoots, Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);
            AddIfDir(steamRoots, Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string);

            var libraries = new List<string>();
            foreach (string root in steamRoots)
            {
                AddIfDir(libraries, root);
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf))
                    continue;
                try
                {
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\""))
                        AddIfDir(libraries, m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch (Exception)
                {
                    // An unreadable library list just means we fall back to asking for the folder.
                }
            }
            return libraries;
        }

        private static void AddIfDir(List<string> list, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                string full = Path.GetFullPath(path);
                if (Directory.Exists(full) && !list.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
                    list.Add(full);
            }
            catch (Exception)
            {
                // Ignore malformed paths from the registry or the library file.
            }
        }

        /// <summary>For ...\steamapps\common\People Playground returns ...\steamapps, otherwise null.</summary>
        private static string SteamappsFolderOf(string gameDir)
        {
            DirectoryInfo common = Directory.GetParent(gameDir);
            DirectoryInfo steamapps = common?.Parent;
            if (common == null || steamapps == null)
                return null;
            if (!common.Name.Equals("common", StringComparison.OrdinalIgnoreCase)
                || !steamapps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                return null;
            return steamapps.FullName;
        }

        private static string ReadBuildId(string manifest)
        {
            try
            {
                if (!File.Exists(manifest))
                    return null;
                Match m = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
