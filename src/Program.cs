using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace PPGModEnabler
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Ui.Init();
            try
            {
                return App.Run(args);
            }
            catch (Exception ex)
            {
                Ui.Line();
                Ui.Bad("Something went wrong: " + ex.Message);
                Ui.Dim(ex.ToString());
                Ui.Line("Nothing was deleted. If a backup was made, its folder is listed above.");
                App.SaveLog();
                Ui.Pause();
                return 1;
            }
        }
    }

    internal static class App
    {
        private const string SupportedBuild = "25467926";

        private static string backupRoot = Backup.DefaultRoot;

        public static int Run(string[] args)
        {
            string gameArg = null, workshopArg = null, action = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a.ToLowerInvariant())
                {
                    case "--game": gameArg = next; i++; break;
                    case "--workshop": workshopArg = next; i++; break;
                    case "--backup-root": backupRoot = Path.GetFullPath(next); i++; break;
                    case "--action": action = next?.ToLowerInvariant(); i++; break;
                    case "--no-pause": Ui.NoPause = true; break;
                    default:
                        if (gameArg == null && Directory.Exists(a.Trim('"')))
                            gameArg = a; // a folder dragged onto the exe
                        break;
                }
            }

            Ui.Title("PPG Mod Enabler " + AssemblyPatcher.PatchVersion);
            Ui.Line("Switches C# mods back on in People Playground's safe-mode build (Sept 22 2026).");
            Ui.Line("It never deletes mods. It makes a checked backup before it changes anything.");
            Ui.Line();

            GameInfo game = LocateGame(gameArg, workshopArg);
            if (game == null)
            {
                Ui.Bad("People Playground was not found, so nothing was changed.");
                SaveLog();
                Ui.Pause();
                return 2;
            }

            Ui.Line("Game folder:       " + game.GameDir);
            Ui.Line("Steam build:       " + (game.BuildId ?? "unknown") + (game.BuildId == SupportedBuild ? " (the one this tool was made for)" : ""));
            Ui.Line("Workshop folder:   " + (game.WorkshopDir ?? "none found"));
            Ui.Line("Backups go to:     " + backupRoot);

            Inspection state = AssemblyPatcher.Inspect(game.AssemblyPath, game.ManagedDir);
            Ui.Line("Game file status:  " + state.Detail);
            Ui.Line();

            if (action == null)
            {
                Ui.Line("What do you want to do?");
                Ui.Line("  1) Switch mods on (backs up and checks your mods first)");
                Ui.Line("  2) Only check my mods for suspicious code (changes nothing)");
                Ui.Line("  3) Undo: put the original game file back");
                Ui.Line("  4) Quit");
                string choice = Ui.Ask("Type 1, 2, 3 or 4 and press Enter: ");
                action = choice == "1" ? "enable" : choice == "2" ? "scan" : choice == "3" ? "undo" : "quit";
            }

            int code;
            switch (action)
            {
                case "enable": code = Enable(game, state); break;
                case "scan": code = ScanOnly(game); break;
                case "undo": code = Undo(game, state); break;
                default: Ui.Line("Nothing was changed."); code = 0; break;
            }
            SaveLog();
            Ui.Pause();
            return code;
        }

        private static GameInfo LocateGame(string gameArg, string workshopArg)
        {
            string dir = gameArg;
            if (dir != null && !GameLocator.IsGameDir(dir.Trim().Trim('"')))
            {
                Ui.Warn("That folder does not look like People Playground: " + dir);
                dir = null;
            }
            if (dir == null)
            {
                dir = GameLocator.FindInstalledGame();
                if (dir != null)
                    Ui.Line("Found People Playground through Steam.");
            }
            while (dir == null)
            {
                Ui.Line("Could not find People Playground automatically.");
                Ui.Line("In Steam: right-click People Playground > Manage > Browse local files, then drag that");
                string typed = Ui.Ask("folder into this window and press Enter (or just press Enter to quit): ");
                if (string.IsNullOrEmpty(typed))
                    return null;
                typed = typed.Trim().Trim('"');
                if (GameLocator.IsGameDir(typed))
                    dir = typed;
                else
                    Ui.Warn("That folder does not contain People Playground.exe.");
            }
            return GameLocator.Describe(dir, workshopArg);
        }

        // ---------- 1) switch mods on ----------

        private static int Enable(GameInfo game, Inspection state)
        {
            switch (state.State)
            {
                case BuildState.Patched:
                    Ui.Good("Mods are already switched on in this game. Nothing to patch.");
                    if (Ui.YesNo("Check your mods for suspicious code anyway?", false))
                        return ScanOnly(game);
                    return 0;
                case BuildState.NotSafeMode:
                    Ui.Good("This game version is not in safe mode, so mods should already load. Nothing to patch.");
                    return 0;
                case BuildState.Unreadable:
                    Ui.Bad("The game file could not be read, so nothing was changed.");
                    Ui.Line("In Steam: right-click People Playground > Properties > Installed Files >");
                    Ui.Line("Verify integrity of game files. Then run this tool again.");
                    return 3;
                case BuildState.Unsupported:
                    Ui.Bad("This game version is not the one this tool knows (" + state.Detail + ").");
                    Ui.Line("To be safe, nothing was changed. The game has probably been updated; a new version of this tool would be needed.");
                    return 3;
            }

            if (!state.KnownFile)
            {
                Ui.Warn("This game file is not the exact one this tool was tested on (build " + SupportedBuild + "),");
                Ui.Warn("although its mod code looks the same. It is probably a newer game update.");
                if (!Ui.YesNo("Patch it anyway?", false))
                {
                    Ui.Line("Nothing was changed.");
                    return 0;
                }
            }

            Ui.Warn("Before you continue:");
            Ui.Line("Mods are small programs. Once they are switched on, any mod you load can do anything on");
            Ui.Line("this PC that you can do. That is how the September 2026 worm spread. This tool backs up");
            Ui.Line("your mods, points out mods that look risky, and leaves the game's own suspicious-code check");
            Ui.Line("switched on, but it cannot promise that a mod is safe. Only load mods you trust.");
            Ui.Line();
            if (!Ui.YesNo("Continue?", false))
            {
                Ui.Line("Nothing was changed.");
                return 0;
            }

            if (!WaitForGameClosed())
                return 4;

            // Step 1: backup.
            Ui.Line();
            Ui.Title("Step 1 of 4: backing up your mods");
            BackupResult backup = MakeBackup(game);
            if (backup == null)
                return 5;

            // Step 2: scan.
            Ui.Line();
            Ui.Title("Step 2 of 4: checking your mods");
            var problems = new List<string>();
            List<ModReport> local = ModScanner.ScanRoot(game.ModsDir, "Mods", problems);
            List<ModReport> workshop = ModScanner.ScanRoot(game.WorkshopDir, "Workshop", problems);
            string report = WriteReport(backup.Folder, local, workshop, problems);
            PrintReports("Mods folder (these are what the game loads)", local);
            PrintReports("Workshop downloads (the game no longer loads these by itself)", workshop);
            PrintProblems(problems);
            Ui.Line("Full report: " + report);

            List<ModReport> risky = local.Where(r => r.Risk == Risk.Suspicious && !r.SwitchedOff).ToList();
            if (risky.Count > 0)
            {
                Ui.Line();
                Ui.Warn(risky.Count + " mod(s) in your Mods folder look suspicious:");
                foreach (ModReport r in risky)
                    Ui.Line("  - " + r.Name);
                Ui.Line("Switching a mod off renames its mod.json so the game skips it. Nothing is deleted,");
                Ui.Line("and you can switch it back on later (option 3, or rename the file back).");
                if (Ui.YesNo("Switch these mods off?", true))
                {
                    foreach (ModReport r in risky)
                    {
                        try
                        {
                            ModScanner.SwitchOff(r);
                            Ui.Line("  switched off: " + r.Name);
                        }
                        catch (Exception ex)
                        {
                            Ui.Bad("  could NOT switch off " + r.Name + ": " + ex.Message);
                            Ui.Line("    The game will still load it. Move its folder out of Mods by hand if you do not trust it:");
                            Ui.Line("    " + r.Folder);
                        }
                    }
                }
            }

            // Step 3: optional copy of Workshop mods into the Mods folder.
            Ui.Line();
            Ui.Title("Step 3 of 4: Workshop mods");
            OfferWorkshopImport(game, workshop, local);

            // Step 4: patch.
            Ui.Line();
            Ui.Title("Step 4 of 4: switching the mod loader back on");
            if (!WaitForGameClosed())
                return 4;
            if (!PatchGame(game))
                return 6;

            Ui.Line();
            Ui.Good("Done. Mods are switched on.");
            Ui.Line("Start the game. Mods compile when the game starts (the first start can take a while).");
            Ui.Line("Open the Mods menu to switch single mods on or off, then load a map.");
            Ui.Line("If the game itself marks a mod as suspicious, think twice before pressing Accept risk.");
            Ui.Line();
            Ui.Line("To undo: run this tool again and pick 3, or in Steam: right-click People Playground >");
            Ui.Line("Properties > Installed Files > Verify integrity of game files.");
            Ui.Line("A Steam update of the game also undoes it; run this tool again afterwards if you want.");
            return 0;
        }

        /// <summary>Backs everything up, offering to leave out the Workshop downloads when space is short.</summary>
        private static BackupResult MakeBackup(GameInfo game)
        {
            List<BackupItem> items = Backup.Plan(game);
            long needed = items.Sum(i => i.Estimate) + Backup.SpaceMargin;
            long? free = SafeCopy.FreeSpace(backupRoot);
            Ui.Line("The backup needs about " + SafeCopy.FormatSize(needed) + (free.HasValue ? ", free space there: " + SafeCopy.FormatSize(free.Value) : "") + ".");

            if (free.HasValue && free.Value < needed)
            {
                BackupItem ws = items.FirstOrDefault(i => i.IsWorkshop);
                if (ws != null && free.Value >= needed - ws.Estimate)
                {
                    Ui.Warn("Not enough space to also back up the Workshop downloads (" + SafeCopy.FormatSize(ws.Estimate) + ").");
                    Ui.Line("Leaving them out is safe for this tool: it only copies from them and never changes them.");
                    if (!Ui.YesNo("Back up everything except the Workshop downloads?", false))
                    {
                        Ui.Line("Nothing was changed. Free up space, or run with --backup-root \"D:\\some folder\" to back up to another drive.");
                        return null;
                    }
                    items.Remove(ws);
                }
                else
                {
                    Ui.Bad("Not enough free space for the backup, so nothing was changed.");
                    Ui.Line("Free up space, or run with --backup-root \"D:\\some folder\" to back up to another drive.");
                    return null;
                }
            }
            else if (!free.HasValue)
            {
                Ui.Warn("Could not check the free space there; trying anyway.");
            }

            try
            {
                BackupResult backup = Backup.Create(game, items, backupRoot);
                Ui.Good("Backup done and checked: " + backup.Files + " files, " + SafeCopy.FormatSize(backup.Bytes));
                Ui.Line("  " + backup.Folder);
                foreach (string link in backup.SkippedLinks)
                    Ui.Warn("  not followed (link to another folder): " + link);
                return backup;
            }
            catch (Exception ex)
            {
                Ui.Bad("The backup failed, so nothing was changed: " + ex.Message);
                Ui.Line("The unfinished backup copy from this run was removed. Your files were not touched.");
                return null;
            }
        }

        private static bool PatchGame(GameInfo game)
        {
            string dll = game.AssemblyPath;
            string original = game.OriginalCopyPath;
            string temp = dll + ".ppgme-tmp";

            byte[] originalBytes;
            byte[] patched;
            try
            {
                originalBytes = File.ReadAllBytes(dll);
                if (AssemblyPatcher.Inspect(originalBytes, game.ManagedDir).State != BuildState.SafeMode)
                    throw new PatchException("the game file changed while this tool was running");
                patched = AssemblyPatcher.BuildPatched(originalBytes, game.ManagedDir);
            }
            catch (Exception ex)
            {
                Ui.Bad("Could not build the patch, so the game was not changed: " + ex.Message);
                return false;
            }

            try
            {
                // Keep the exact bytes that were checked as the unpatched file, for Undo.
                // The dated backup folder has another copy.
                WriteVerified(original, originalBytes);

                // Swap in the patched file in one step (File.Replace), so the game file is never half written.
                ClearReadOnly(dll);
                WriteVerified(temp, patched);
                File.Replace(temp, dll, null);
                if (!File.ReadAllBytes(dll).SequenceEqual(patched)
                    || AssemblyPatcher.Inspect(dll, game.ManagedDir).State != BuildState.Patched)
                    throw new IOException("the patched game file did not check out after writing");
            }
            catch (Exception ex)
            {
                Ui.Bad("Writing the patch failed: " + ex.Message);
                RestoreOriginal(game, originalBytes);
                TryDeleteOwnTemp(temp);
                return false;
            }

            Ui.Good("Game file patched and checked.");
            Ui.Line("  original saved as " + original);
            return true;
        }

        private static void RestoreOriginal(GameInfo game, byte[] originalBytes)
        {
            try
            {
                ClearReadOnly(game.AssemblyPath);
                File.WriteAllBytes(game.AssemblyPath, originalBytes);
                if (File.ReadAllBytes(game.AssemblyPath).SequenceEqual(originalBytes))
                {
                    Ui.Line("The original game file was put back.");
                    return;
                }
                Ui.Bad("Putting the original back did not check out.");
            }
            catch (Exception restoreError)
            {
                Ui.Bad("Putting the original back also failed: " + restoreError.Message);
            }
            Ui.Line("In Steam: right-click People Playground > Properties > Installed Files >");
            Ui.Line("Verify integrity of game files. That downloads the original file again.");
        }

        private static void WriteVerified(string path, byte[] bytes)
        {
            if (File.Exists(path))
                ClearReadOnly(path);
            File.WriteAllBytes(path, bytes);
            if (!File.ReadAllBytes(path).SequenceEqual(bytes))
                throw new IOException("could not write " + path + " correctly");
        }

        private static void ClearReadOnly(string path)
        {
            if (File.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }

        private static void TryDeleteOwnTemp(string temp)
        {
            try
            {
                if (File.Exists(temp))
                {
                    ClearReadOnly(temp);
                    File.Delete(temp);
                }
            }
            catch (Exception)
            {
                // Only our own temp file; a leftover does no harm.
            }
        }

        private static void OfferWorkshopImport(GameInfo game, List<ModReport> workshop, List<ModReport> local)
        {
            var candidates = new List<(ModReport Mod, string Target)>();
            foreach (ModReport mod in workshop.Where(w => !w.SwitchedOff).OrderBy(w => w.Folder.Length))
            {
                try
                {
                    // A mod.json nested inside another Workshop mod is copied along with that mod.
                    if (candidates.Any(c => SafeCopy.IsInside(mod.Folder, c.Mod.Folder)))
                        continue;
                    bool alreadyLocal = local.Any(l => string.Equals(l.Name, mod.Name, StringComparison.OrdinalIgnoreCase)
                                                    && string.Equals(l.Author, mod.Author, StringComparison.OrdinalIgnoreCase));
                    if (alreadyLocal)
                        continue;
                    string itemId = SafeCopy.Relative(game.WorkshopDir, mod.Folder).Split('\\', '/')[0];
                    string target = Path.Combine(game.ModsDir, SafeFolderName(mod.Name) + " [" + itemId + "]");
                    if (Directory.Exists(SafeCopy.Long(target)))
                    {
                        // Something is there but it is not a mod the game or this tool recognises: most likely an
                        // unfinished copy from an earlier run. It has no live mod.json, so the game ignores it.
                        Ui.Warn("  " + mod.Name + ": an unfinished copy from an earlier run is in the way:");
                        Ui.Line("    " + target);
                        Ui.Line("    The game ignores it. Delete that folder and run this tool again to copy the mod.");
                        continue;
                    }
                    candidates.Add((mod, target));
                }
                catch (Exception ex)
                {
                    Ui.Bad("  could not prepare " + mod.Name + ": " + ex.Message);
                }
            }

            if (candidates.Count == 0)
            {
                Ui.Line("No Workshop mods need copying (none found, or they are already in your Mods folder).");
                return;
            }

            Ui.Line("The safe-mode game no longer loads Workshop mods by itself. To use them, they have to be");
            Ui.Line("copied into the Mods folder. The originals stay where they are. Mods that look");
            Ui.Line("suspicious are copied switched off, so they will not run until you switch them on.");
            Ui.Line();
            foreach (var (mod, _) in candidates)
                Ui.Line("  [" + mod.Label + "] " + mod.Name);
            Ui.Line();

            long needed = candidates.Sum(c => SafeCopy.DiskEstimate(c.Mod.Folder)) + Backup.SpaceMargin;
            long? free = SafeCopy.FreeSpace(game.ModsDir);
            if (free.HasValue && free.Value < needed)
            {
                Ui.Warn("Not enough free space on the game drive to copy them (need about " + SafeCopy.FormatSize(needed)
                    + ", have " + SafeCopy.FormatSize(free.Value) + "). Skipped.");
                return;
            }

            if (!Ui.YesNo("Copy these " + candidates.Count + " Workshop mod(s) into your Mods folder?", false))
            {
                Ui.Line("Skipped. You can run this tool again later to copy them.");
                return;
            }

            foreach (var (mod, target) in candidates)
            {
                bool switchOff = mod.Risk == Risk.Suspicious;
                try
                {
                    // mod.json files are copied last. Until then the game ignores the folder, so a copy that
                    // fails halfway can never leave a mod that loads. Suspicious mods get theirs switched off.
                    var metas = new List<string>();
                    SafeCopy.CopyDirectory(mod.Folder, target, holdBack: ModScanner.MetaFile, heldBack: metas);
                    foreach (string meta in metas)
                    {
                        string dest = Path.Combine(target, SafeCopy.Relative(mod.Folder, meta));
                        SafeCopy.CopyFile(meta, switchOff ? dest + ModScanner.SwitchedOffSuffix : dest);
                    }
                    if (switchOff)
                        Ui.Warn("  copied (switched off, looks suspicious): " + mod.Name);
                    else
                        Ui.Line("  copied: " + mod.Name);
                }
                catch (Exception ex)
                {
                    Ui.Bad("  could not copy " + mod.Name + ": " + ex.Message);
                    Ui.Line("    The unfinished copy has no working mod.json, so the game ignores it:");
                    Ui.Line("    " + target);
                }
            }
        }

        // ---------- 2) scan only ----------

        private static int ScanOnly(GameInfo game)
        {
            Ui.Title("Checking your mods (nothing is changed)");
            var problems = new List<string>();
            List<ModReport> local = ModScanner.ScanRoot(game.ModsDir, "Mods", problems);
            List<ModReport> workshop = ModScanner.ScanRoot(game.WorkshopDir, "Workshop", problems);
            PrintReports("Mods folder (these are what the game loads)", local);
            PrintReports("Workshop downloads", workshop);
            PrintProblems(problems);
            Directory.CreateDirectory(backupRoot);
            string report = WriteReport(backupRoot, local, workshop, problems);
            Ui.Line("Full report: " + report);
            return 0;
        }

        // ---------- 3) undo ----------

        private static int Undo(GameInfo game, Inspection state)
        {
            int code = 0;
            string original = game.OriginalCopyPath;
            Inspection saved = File.Exists(original) ? AssemblyPatcher.Inspect(original, game.ManagedDir) : null;
            bool savedIsGood = saved != null && saved.State == BuildState.SafeMode;

            if (state.State == BuildState.Patched || state.State == BuildState.Unreadable)
            {
                if (!savedIsGood)
                {
                    Ui.Warn("The saved original game file is missing or not what it should be, so it was not used.");
                    Ui.Line("In Steam: right-click People Playground > Properties > Installed Files >");
                    Ui.Line("Verify integrity of game files. That puts the original file back.");
                    code = 7;
                }
                else
                {
                    string question = state.State == BuildState.Unreadable
                        ? "The game file looks damaged. Put the saved original back?"
                        : "Put the original game file back (mods will stop loading)?";
                    if (WaitForGameClosed() && Ui.YesNo(question, true))
                    {
                        byte[] bytes = File.ReadAllBytes(original);
                        string temp = game.AssemblyPath + ".ppgme-tmp";
                        try
                        {
                            WriteVerified(temp, bytes);
                            ClearReadOnly(game.AssemblyPath);
                            if (File.Exists(game.AssemblyPath))
                                File.Replace(temp, game.AssemblyPath, null);
                            else
                                File.Move(temp, game.AssemblyPath);
                        }
                        finally
                        {
                            TryDeleteOwnTemp(temp);
                        }
                        if (File.ReadAllBytes(game.AssemblyPath).SequenceEqual(bytes))
                        {
                            Ui.Good("The original game file is back. Mods are switched off again (safe mode).");
                        }
                        else
                        {
                            Ui.Bad("The file did not copy correctly. Use Steam's Verify integrity of game files.");
                            code = 7;
                        }
                    }
                }
            }
            else
            {
                Ui.Line("The game file is not patched by this tool, so there is nothing to put back.");
            }

            List<ModReport> off = ModScanner.ScanRoot(game.ModsDir, "Mods", null).Where(r => r.SwitchedOff).ToList();
            if (off.Count > 0)
            {
                Ui.Line();
                Ui.Line(off.Count + " mod(s) were switched off by this tool:");
                foreach (ModReport r in off)
                    Ui.Line("  [" + r.Label + "] " + r.Name);
                if (Ui.YesNo("Switch them back on?", false))
                {
                    foreach (ModReport r in off)
                    {
                        try
                        {
                            if (ModScanner.SwitchOn(r))
                                Ui.Line("  switched on: " + r.Name);
                            else
                                Ui.Warn("  left as is (a mod.json already exists there): " + r.Name);
                        }
                        catch (Exception ex)
                        {
                            Ui.Bad("  could not switch on " + r.Name + ": " + ex.Message);
                        }
                    }
                }
            }
            Ui.Line("Mods copied from the Workshop by this tool stay in your Mods folder. They are yours to keep.");
            return code;
        }

        // ---------- helpers ----------

        private static bool WaitForGameClosed()
        {
            while (Process.GetProcessesByName("People Playground").Length > 0 || Process.GetProcessesByName("PPGModCompiler").Length > 0)
            {
                Ui.Warn("People Playground is running. Close the game first.");
                string answer = Ui.Ask("Press Enter when it is closed (or type q to stop): ");
                if (answer == null || answer.Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    Ui.Line("Stopped. Nothing more was changed.");
                    return false;
                }
            }
            return true;
        }

        private static void PrintReports(string title, List<ModReport> reports)
        {
            Ui.Line();
            Ui.Title(title + ": " + reports.Count + " mod(s)");
            if (reports.Count == 0)
                return;
            foreach (ModReport r in reports.OrderByDescending(x => x.Risk).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                ConsoleColor color = r.Risk == Risk.Suspicious ? ConsoleColor.Red : r.Risk == Risk.Check ? ConsoleColor.Yellow : ConsoleColor.Green;
                string off = r.SwitchedOff ? " (switched off)" : "";
                Ui.Colored(color, "  [" + r.Label + "] " + r.Name + (r.Author.Length > 0 ? "  by " + r.Author : "") + off);
                foreach (string reason in r.Reasons)
                    Ui.Line("        - " + reason);
            }
            int ok = reports.Count(r => r.Risk == Risk.Ok);
            int check = reports.Count(r => r.Risk == Risk.Check);
            int bad = reports.Count(r => r.Risk == Risk.Suspicious);
            Ui.Line("  " + ok + " OK, " + check + " worth a look, " + bad + " suspicious.");
        }

        private static void PrintProblems(List<string> problems)
        {
            if (problems.Count == 0)
                return;
            Ui.Line();
            Ui.Warn("Some places could not be checked:");
            foreach (string p in problems)
                Ui.Line("  - " + p);
        }

        private static string WriteReport(string folder, List<ModReport> local, List<ModReport> workshop, List<string> problems)
        {
            var sb = new StringBuilder();
            sb.AppendLine("PPG Mod Enabler mod check, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("This is a text scan of mod source code. It can miss well hidden malware.");
            sb.AppendLine("SUSPICIOUS = do not load unless you know why the mod needs it. CHECK = common in mods, worth a look.");
            foreach (var (title, list) in new[] { ("Mods folder", local), ("Workshop downloads", workshop) })
            {
                sb.AppendLine();
                sb.AppendLine("== " + title + " (" + list.Count + ") ==");
                foreach (ModReport r in list.OrderByDescending(x => x.Risk).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    sb.AppendLine("[" + r.Label + "] " + r.Name + (r.Author.Length > 0 ? "  by " + r.Author : "") + (r.SwitchedOff ? "  (switched off)" : ""));
                    sb.AppendLine("  folder: " + r.Folder);
                    foreach (string reason in r.Reasons)
                        sb.AppendLine("  - " + reason);
                    foreach (string note in r.Notes)
                        sb.AppendLine("  note: " + note);
                }
            }
            if (problems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== Could not be checked ==");
                foreach (string p in problems)
                    sb.AppendLine("  - " + p);
            }
            string path = Path.Combine(folder, "mod-check-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
            File.WriteAllText(path, sb.ToString());
            return path;
        }

        private static string SafeFolderName(string name)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            string clean = new string((name ?? "").Select(c => bad.Contains(c) ? ' ' : c).ToArray());
            clean = string.Join(" ", clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
            if (clean.Length > 60)
                clean = clean.Substring(0, 60).Trim('.', ' ');
            return clean.Length == 0 ? "Workshop mod" : clean;
        }

        public static void SaveLog()
        {
            try
            {
                Directory.CreateDirectory(backupRoot);
                string path = Path.Combine(backupRoot, "PPG-Mod-Enabler-log_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
                File.WriteAllText(path, Ui.Transcript);
            }
            catch (Exception)
            {
                // The log is a convenience; failing to write it must not hide the real result.
            }
        }
    }
}
