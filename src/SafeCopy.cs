using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace PPGModEnabler
{
    /// <summary>
    /// Copying that never overwrites and checks every copied file byte for byte (SHA-256).
    /// Nothing in this tool deletes a user file. The only removals are the tool's own temp files and
    /// the tool's own unfinished backup folder from the same run.
    /// </summary>
    internal static class SafeCopy
    {
        public sealed class Totals
        {
            public int Files;
            public long Bytes;
            public readonly List<string> SkippedLinks = new List<string>();
        }

        /// <summary>Extended-length form (\\?\...) so deep mod folders do not hit the 260 character limit.</summary>
        public static string Long(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                return path;
            string full = Path.GetFullPath(path);
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }

        public static string Short(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }

        public static bool IsLink(string path)
        {
            return (File.GetAttributes(Long(path)) & FileAttributes.ReparsePoint) != 0;
        }

        /// <summary>
        /// Visits every file under root without following folder links (junctions, symlinks).
        /// Problems with single folders are reported through onError instead of stopping the walk.
        /// </summary>
        public static void Walk(string root, Action<string> onFile, Action<string> onLink, Action<string, Exception> onError)
        {
            var pending = new Stack<string>();
            pending.Push(Long(root));
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception ex)
                {
                    onError(Short(dir), ex);
                    continue;
                }
                foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    onFile(file);
                foreach (string sub in dirs)
                {
                    try
                    {
                        if (IsLink(sub))
                        {
                            onLink(Short(sub));
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        onError(Short(sub), ex);
                        continue;
                    }
                    pending.Push(sub);
                }
            }
        }

        /// <summary>Rough space the copies will take on disk: each file rounded up to 4 KB, plus 4 KB of overhead.</summary>
        public static long DiskEstimate(string path)
        {
            const long block = 4096;
            if (File.Exists(Long(path)))
                return RoundUp(new FileInfo(Long(path)).Length) + block;
            if (!Directory.Exists(Long(path)))
                return 0;
            long total = 0;
            Walk(path, f =>
            {
                try { total += RoundUp(new FileInfo(f).Length) + block; }
                catch (Exception) { total += block; }
            }, _ => { }, (_, __) => { });
            return total;

            long RoundUp(long n) => (n + block - 1) / block * block;
        }

        /// <summary>
        /// Copies a folder into a destination that must not exist yet, verifying every file.
        /// Files named holdBack (for example "mod.json") are not copied; their paths are added to heldBack
        /// so the caller can copy them last. Folder links are not followed; they are listed in totals.
        /// </summary>
        public static Totals CopyDirectory(string source, string destination, Totals totals = null,
                                           string holdBack = null, List<string> heldBack = null)
        {
            totals = totals ?? new Totals();
            if (Directory.Exists(Long(destination)) || File.Exists(Long(destination)))
                throw new IOException("Refusing to copy over something that already exists: " + destination);

            string sourceRoot = Long(source);
            Directory.CreateDirectory(Long(destination));
            Exception firstError = null;
            Walk(source,
                file =>
                {
                    if (firstError != null)
                        return;
                    if (holdBack != null && Path.GetFileName(file).Equals(holdBack, StringComparison.OrdinalIgnoreCase))
                    {
                        heldBack?.Add(Short(file));
                        return;
                    }
                    try
                    {
                        CopyFile(file, Path.Combine(Long(destination), Relative(sourceRoot, file)), totals);
                    }
                    catch (Exception ex)
                    {
                        firstError = ex;
                    }
                },
                link => totals.SkippedLinks.Add(link),
                (dir, ex) => firstError = firstError ?? new IOException("Could not read " + dir + ": " + ex.Message, ex));
            if (firstError != null)
                throw firstError;
            return totals;
        }

        /// <summary>Copies one file to a path that must not exist yet and verifies the copy.</summary>
        public static void CopyFile(string source, string destination, Totals totals = null)
        {
            string src = Long(source);
            string dst = Long(destination);
            if (File.Exists(dst))
                throw new IOException("Refusing to overwrite an existing file: " + Short(dst));
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, overwrite: false);
            // A copy of a read-only file is read-only too; clear that so the copy can be handled normally.
            File.SetAttributes(dst, File.GetAttributes(dst) & ~FileAttributes.ReadOnly);
            if (!SameContent(src, dst))
                throw new IOException("Copy check failed, the copied file does not match the original: " + Short(src));
            if (totals != null)
            {
                totals.Files++;
                totals.Bytes += new FileInfo(dst).Length;
            }
        }

        public static bool SameContent(string a, string b)
        {
            var fa = new FileInfo(Long(a));
            var fb = new FileInfo(Long(b));
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length)
                return false;
            return Hash(a).SequenceEqual(Hash(b));
        }

        public static byte[] Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (var stream = new FileStream(Long(path), FileMode.Open, FileAccess.Read, FileShare.Read))
                return sha.ComputeHash(stream);
        }

        public static string HashHex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        public static string Relative(string root, string path)
        {
            string r = Path.GetFullPath(Short(root)).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(Short(path));
            if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(path + " is not inside " + root);
            return p.Substring(r.Length);
        }

        public static bool IsInside(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder))
                return false;
            string f = Path.GetFullPath(Short(folder)).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(Short(path)).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? bytes + " bytes" : value.ToString("0.0") + " " + units[unit];
        }

        /// <summary>Free space on the drive holding path, or null when it cannot be found out (for example a network share).</summary>
        public static long? FreeSpace(string path)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    internal sealed class BackupItem
    {
        public string Path;
        public string Name;
        public long Estimate;
        public bool IsWorkshop;
    }

    internal sealed class BackupResult
    {
        public string Folder;
        public int Files;
        public long Bytes;
        public readonly List<string> Items = new List<string>();
        public readonly List<string> SkippedLinks = new List<string>();
    }

    internal static class Backup
    {
        public const long SpaceMargin = 200L * 1024 * 1024;

        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PPG Mod Backups");

        /// <summary>What would be backed up, with a disk space estimate for each part.</summary>
        public static List<BackupItem> Plan(GameInfo game)
        {
            var items = new List<BackupItem>
            {
                new BackupItem { Path = game.ModsDir, Name = "Mods" },
                new BackupItem { Path = game.WorkshopDir, Name = "Workshop downloads (" + GameLocator.AppId + ")", IsWorkshop = true },
                new BackupItem { Path = game.ContraptionsDir, Name = "Contraptions" },
                new BackupItem { Path = System.IO.Path.Combine(game.GameDir, "externalactives"), Name = "externalactives" },
                new BackupItem { Path = System.IO.Path.Combine(game.GameDir, "config.json"), Name = "config.json" },
                new BackupItem { Path = game.AssemblyPath, Name = "Assembly-CSharp.dll" },
            };
            items.RemoveAll(i => i.Path == null || (!File.Exists(SafeCopy.Long(i.Path)) && !Directory.Exists(SafeCopy.Long(i.Path))));
            foreach (BackupItem item in items)
                item.Estimate = SafeCopy.DiskEstimate(item.Path);
            return items;
        }

        /// <summary>
        /// Copies the planned items into a new dated folder and verifies every file. If anything fails,
        /// the unfinished folder from this run (which only holds this run's copies) is removed and the error is rethrown.
        /// </summary>
        public static BackupResult Create(GameInfo game, List<BackupItem> items, string root)
        {
            foreach (string source in new[] { game.GameDir, game.WorkshopDir })
                if (SafeCopy.IsInside(root, source))
                    throw new IOException("The backup folder must not be inside " + source + ". Pick another place with --backup-root.");

            Directory.CreateDirectory(root);
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string folder = Path.Combine(root, stamp);
            for (int n = 2; Directory.Exists(folder); n++)
                folder = Path.Combine(root, stamp + "_" + n);
            Directory.CreateDirectory(folder);

            var result = new BackupResult { Folder = folder };
            var totals = new SafeCopy.Totals();
            try
            {
                foreach (BackupItem item in items)
                {
                    Ui.Dim("  copying " + item.Name + " (about " + SafeCopy.FormatSize(item.Estimate) + ") ...");
                    string target = Path.Combine(folder, item.Name);
                    if (Directory.Exists(SafeCopy.Long(item.Path)))
                        SafeCopy.CopyDirectory(item.Path, target, totals);
                    else
                        SafeCopy.CopyFile(item.Path, target, totals);
                    result.Items.Add(item.Name + "  <-  " + item.Path);
                }
            }
            catch (Exception)
            {
                try
                {
                    Directory.Delete(SafeCopy.Long(folder), recursive: true);
                }
                catch (Exception cleanupError)
                {
                    Ui.Warn("  The unfinished backup folder could not be removed: " + folder + " (" + cleanupError.Message + ")");
                }
                throw;
            }

            result.Files = totals.Files;
            result.Bytes = totals.Bytes;
            result.SkippedLinks.AddRange(totals.SkippedLinks);

            var info = new List<string>
            {
                "PPG Mod Enabler backup, made " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "Game folder: " + game.GameDir,
                "Game build: " + (game.BuildId ?? "unknown"),
                "",
                "Every file below was copied and then checked byte for byte (SHA-256).",
                "To restore something, copy it back to the path it came from.",
                "",
            };
            info.AddRange(result.Items);
            if (result.SkippedLinks.Count > 0)
            {
                info.Add("");
                info.Add("Folder links (junctions/symlinks) that were not followed:");
                info.AddRange(result.SkippedLinks);
            }
            File.WriteAllLines(Path.Combine(folder, "backup-info.txt"), info);
            return result;
        }
    }
}
