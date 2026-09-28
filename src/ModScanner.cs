using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PPGModEnabler
{
    internal enum Risk
    {
        Ok = 0,
        Check = 1,
        Suspicious = 2,
    }

    internal sealed class ModReport
    {
        public string Name;
        public string Author;
        public string Folder;
        public string MetaPath;      // mod.json, or the switched-off copy of it
        public string Source;        // "Mods" or "Workshop"
        public bool SwitchedOff;     // mod.json was renamed by this tool
        public Risk Risk;
        public readonly SortedSet<string> Reasons = new SortedSet<string>();
        public readonly SortedSet<string> Notes = new SortedSet<string>();

        public void Flag(Risk level, string reason)
        {
            if (level > Risk)
                Risk = level;
            Reasons.Add(reason);
        }

        public string Label => Risk == Risk.Suspicious ? "SUSPICIOUS" : Risk == Risk.Check ? "CHECK" : "OK";
    }

    /// <summary>
    /// A heuristic scan of mod source code. It reads files as text and never runs or compiles anything.
    /// It can miss well hidden malware; it is a second opinion next to the game's own check, not a guarantee.
    /// </summary>
    internal static class ModScanner
    {
        public const string MetaFile = "mod.json";
        public const string SwitchedOffSuffix = ".switched-off-by-ppg-mod-enabler";

        // The worm appeared on 21 September 2026 (evening, European time). Anything written from the start
        // of that day (UTC) could have been touched by it.
        public static readonly DateTime WormStartUtc = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

        private static readonly string[] ProgramExtensions =
            { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".scr", ".com", ".msi", ".hta", ".lnk", ".reg" };

        private sealed class Rule
        {
            public Risk Level;
            public Regex Pattern;
            public string Reason;
            public Rule(Risk level, string pattern, string reason)
            {
                Level = level;
                Pattern = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                Reason = reason;
            }
        }

        // Suspicious rules run on the whole text; Check rules run on the code with comments removed.
        // String contents are kept in both.
        private static readonly Rule[] CodeRules =
        {
            new Rule(Risk.Suspicious, @"\bProcess\s*\.\s*Start\b|\bProcessStartInfo\b|System\.Diagnostics\.Process\b",
                "can start other programs"),
            new Rule(Risk.Suspicious, @"\b(WebClient|HttpClient|HttpWebRequest|WebRequest|UnityWebRequest|TcpClient|UdpClient|TcpListener|ClientWebSocket|WebSocket|NetworkStream|Socket)\b|\bDns\s*\.|\bnew\s+WWW\b|\bSystem\.Net\b",
                "can use the internet"),
            new Rule(Risk.Suspicious, @"\bSteamworks\s*\.\s*Ugc\b|\bUgc\s*\.\s*(Editor|Query)\b|\bSubmitAsync\b|\bSteamUGC\b|\bSteamRemoteStorage\b|\bSteamUser\b|\bSteamFriends\b",
                "talks to the Steam Workshop or your Steam account directly (the worm spread this way)"),
            new Rule(Risk.Suspicious, @"(?i)discord(app)?\.com/api|/webhooks/|leveldb|local\s*storage|\.ldb\b|[\\/]discord[\\/]",
                "touches Discord data or Discord webhooks (the worm read Discord data)"),
            new Rule(Risk.Suspicious, @"(?i)\.vdf\b|\bssfn|steam[\\/]+(config|userdata)",
                "mentions Steam account files"),
            new Rule(Risk.Suspicious, @"\bAssembly\s*\.\s*(Load|LoadFrom|LoadFile|UnsafeLoadFrom|LoadWithPartialName)\s*\(|\bAppDomain\b|\bSystem\.Reflection\.Emit\b|\b(DynamicMethod|AssemblyBuilder|ILGenerator|CSharpCodeProvider|CodeDomProvider)\b",
                "loads or generates hidden code while running"),
            new Rule(Risk.Suspicious, @"\bDllImport\b|\bextern\s+static\b|\bstatic\s+extern\b|\bMarshal\s*\.|\bInteropServices\b|(?i)\b(kernel32|ntdll|user32|advapi32|shell32)\b",
                "calls Windows functions directly"),
            new Rule(Risk.Suspicious, @"\bMicrosoft\.Win32\b|\bRegistry(Key)?\s*\.",
                "reads or writes the Windows registry"),
            new Rule(Risk.Suspicious, @"\bEnvironment\s*\.\s*(GetFolderPath|GetEnvironmentVariables?|ExpandEnvironmentVariables|SetEnvironmentVariable|UserName|UserDomainName|MachineName)\b|\bSpecialFolder\b|%(APPDATA|LOCALAPPDATA|USERPROFILE|TEMP)%|(?i)\bappdata\b",
                "looks at folders outside the game (like your user folders)"),
            new Rule(Risk.Suspicious, @"\bFromBase64String\b|\bBinaryFormatter\b|\bFormatterServices\b|\bDeflateStream\b|\bGZipStream\b",
                "decodes hidden or packed data"),
            new Rule(Risk.Suspicious, @"\bGetType\s*\(\s*@?""\s*(System|Microsoft|Mono)\.",
                "looks up system functions by name, a way to hide what the code does"),
            new Rule(Risk.Check, @"\b(File|Directory)\s*\.\s*(Delete|Move|Replace|WriteAll\w*|AppendAll\w*|Copy|Create\w*|Open\w*|SetAttributes)\b|\bFileStream\b|\bStreamWriter\b|\bFileInfo\b|\bDirectoryInfo\b",
                "reads or writes files"),
            new Rule(Risk.Check, @"(?i)https?://",
                "contains a web address"),
            new Rule(Risk.Check, @"\bSteamClient\b",
                "reads Steam info (like your Steam name)"),
            new Rule(Risk.Check, @"\bApplication\s*\.\s*(Quit|OpenURL)\b",
                "can open web links or close the game"),
        };

        private static readonly Regex BigBlob = new Regex(@"[A-Za-z0-9+/]{400,}={0,2}", RegexOptions.Compiled);
        private static readonly Regex Reflection = new Regex(@"\bHarmony\b|\bBindingFlags\b|\.GetMethods?\s*\(|\.GetField\s*\(|\bType\s*\.\s*GetType\s*\(|\bInvokeMember\b", RegexOptions.Compiled);

        // Plain namespace imports ("using System.Net;") say nothing on their own; aliases and "using static" are kept.
        private static readonly Regex UsingDirective = new Regex(@"(?m)^\s*using\s+(?!static\b)[A-Za-z_][\w.]*\s*;", RegexOptions.Compiled);

        // Risky function names written as text, which only matters when the code also uses reflection.
        private static readonly Regex RiskyNameString = new Regex(@"""(Load|LoadFile|LoadFrom|UnsafeLoadFrom|ReadAllBytes|WriteAllBytes|Delete|DownloadData|DownloadString|DownloadFile|UploadData|UploadString|UploadFile|Start)""", RegexOptions.Compiled);
        // The game reads mod.json with Newtonsoft.Json, which matches property names case-insensitively.
        private static readonly Regex JsonName = new Regex("\"Name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex JsonAuthor = new Regex("\"Author\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex JsonScripts = new Regex("\"Scripts\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex JsonString = new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        private static readonly Regex UnicodeEscape = new Regex(@"\\u([0-9A-Fa-f]{4})|\\U([0-9A-Fa-f]{8})", RegexOptions.Compiled);

        private const long MaxCodeFileBytes = 4L * 1024 * 1024;

        /// <summary>
        /// Finds every mod (a folder with mod.json, or one this tool switched off) under root. Folders that
        /// cannot be read, and folder links, are added to problems instead of stopping the scan.
        /// </summary>
        public static List<ModReport> ScanRoot(string root, string source, List<string> problems)
        {
            var reports = new List<ModReport>();
            if (root == null || !Directory.Exists(SafeCopy.Long(root)))
                return reports;

            var metas = new List<string>();
            SafeCopy.Walk(root,
                file =>
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals(MetaFile, StringComparison.OrdinalIgnoreCase)
                        || name.Equals(MetaFile + SwitchedOffSuffix, StringComparison.OrdinalIgnoreCase))
                        metas.Add(SafeCopy.Short(file));
                },
                link => problems?.Add("not checked, it is a link to another folder (the game may still load mods from it): " + link),
                (dir, ex) => problems?.Add("could not read " + dir + " (" + ex.Message + ")"));

            foreach (string meta in metas.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string folder = Path.GetDirectoryName(meta);
                // A folder with both files is still switched on as far as the game is concerned.
                if (meta.EndsWith(SwitchedOffSuffix, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(SafeCopy.Long(Path.Combine(folder, MetaFile))))
                    continue;
                try
                {
                    reports.Add(ScanMod(folder, meta, source));
                }
                catch (Exception ex)
                {
                    problems?.Add("could not check " + folder + " (" + ex.Message + ")");
                }
            }
            return reports;
        }

        public static ModReport ScanMod(string folder, string metaPath, string source)
        {
            var report = new ModReport
            {
                Folder = folder,
                MetaPath = metaPath,
                Source = source,
                SwitchedOff = metaPath.EndsWith(SwitchedOffSuffix, StringComparison.OrdinalIgnoreCase),
                Name = Path.GetFileName(folder),
                Author = "",
            };

            string json = null;
            try
            {
                json = File.ReadAllText(SafeCopy.Long(metaPath));
                Match name = JsonName.Match(json);
                if (name.Success && name.Groups[1].Value.Trim().Length > 0)
                    report.Name = JsonUnescape(name.Groups[1].Value);
                Match author = JsonAuthor.Match(json);
                if (author.Success)
                    report.Author = JsonUnescape(author.Groups[1].Value);
            }
            catch (Exception ex)
            {
                report.Flag(Risk.Check, "mod.json could not be read (" + ex.Message + ")");
            }

            var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The game compiles the files listed under "Scripts", whatever their extension, resolved against the
            // mod folder, and it does not check that they stay inside that folder. Check each listed file as code.
            if (json != null)
            {
                foreach (string script in ListedScripts(json))
                {
                    string full;
                    try
                    {
                        full = Path.GetFullPath(Path.Combine(folder, script));
                    }
                    catch (Exception)
                    {
                        report.Flag(Risk.Check, "lists a script path that is not valid (" + script + ")");
                        continue;
                    }
                    if (!SafeCopy.IsInside(full, folder))
                        report.Flag(Risk.Suspicious, "compiles code from outside its own folder (" + script + ")");
                    if (File.Exists(SafeCopy.Long(full)))
                        ScanFile(report, full, scanned, asCode: true);
                }
            }

            SafeCopy.Walk(folder,
                file => ScanFile(report, file, scanned, asCode: Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)),
                link => report.Flag(Risk.Check, "contains a link to another folder, which was not checked (" + link + ")"),
                (dir, ex) => report.Flag(Risk.Suspicious, "part of the mod could not be read, so it could not be checked (" + dir + ")"));
            return report;
        }

        private static void ScanFile(ModReport report, string file, HashSet<string> scanned, bool asCode)
        {
            if (!scanned.Add(Path.GetFullPath(SafeCopy.Short(file))))
                return;

            FileInfo info;
            try
            {
                info = new FileInfo(SafeCopy.Long(file));
                if (!info.Exists)
                    return;
            }
            catch (Exception ex)
            {
                report.Flag(Risk.Check, "a file could not be read (" + ex.Message + ")");
                return;
            }

            string ext = info.Extension.ToLowerInvariant();

            // mod.json is skipped: the game itself rewrites it whenever a mod is switched on or compiled.
            bool isMeta = info.Name.Equals(MetaFile, StringComparison.OrdinalIgnoreCase)
                || info.Name.Equals(MetaFile + SwitchedOffSuffix, StringComparison.OrdinalIgnoreCase);
            if (!isMeta && info.LastWriteTimeUtc >= WormStartUtc)
                report.Flag(Risk.Suspicious, "has files changed on or after 21 Sept 2026, when the worm spread");

            if (ProgramExtensions.Contains(ext))
                report.Flag(Risk.Suspicious, "contains a program or script file (" + ext + ")");
            else if (ext == ".dll")
                report.Flag(Risk.Suspicious, "ships compiled code (.dll). The game cannot load it by itself, so mods that ship one usually load it with tricks that skip the game's safety check");

            if (!asCode)
                return;
            if (info.Length > MaxCodeFileBytes)
            {
                report.Flag(Risk.Suspicious, "has a very large code file (" + info.Name + ", " + SafeCopy.FormatSize(info.Length) + "), which can hide code");
                return;
            }

            string raw;
            try
            {
                raw = File.ReadAllText(SafeCopy.Long(file));
            }
            catch (Exception ex)
            {
                report.Flag(Risk.Suspicious, "a code file could not be read, so it could not be checked (" + ex.Message + ")");
                return;
            }

            string text = UsingDirective.Replace(DecodeUnicodeEscapes(raw), "");
            string code = StripComments(text);
            foreach (Rule rule in CodeRules)
            {
                // Serious rules look at the whole text, comments included, so tricks that confuse the comment
                // stripper cannot hide code from them. Milder rules skip comments to keep the noise down.
                string target = rule.Level == Risk.Suspicious ? text : code;
                if (rule.Pattern.IsMatch(target))
                    report.Flag(rule.Level, rule.Reason);
            }
            if (BigBlob.IsMatch(text))
                report.Flag(Risk.Suspicious, "contains a large block of encoded data");
            if (Reflection.IsMatch(text))
            {
                report.Notes.Add("uses reflection or Harmony (normal for bigger mods, but it can hide what code does)");
                if (RiskyNameString.IsMatch(text))
                    report.Flag(Risk.Suspicious, "calls risky functions by name through reflection (for example loading or reading files)");
            }
        }

        private static IEnumerable<string> ListedScripts(string json)
        {
            Match list = JsonScripts.Match(json);
            if (!list.Success)
                yield break;
            foreach (Match item in JsonString.Matches(list.Groups[1].Value))
                yield return JsonUnescape(item.Groups[1].Value);
        }

        /// <summary>C# allows \u escapes even in names (Process), so decode them before matching.</summary>
        private static string DecodeUnicodeEscapes(string text)
        {
            return UnicodeEscape.Replace(text, m =>
            {
                string hex = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                try
                {
                    return char.ConvertFromUtf32(Convert.ToInt32(hex, 16));
                }
                catch (ArgumentException)
                {
                    return m.Value;
                }
            });
        }

        /// <summary>Stops the game from loading a mod by renaming its mod.json. Nothing is deleted.</summary>
        public static void SwitchOff(ModReport mod)
        {
            if (mod.SwitchedOff)
                return;
            string target = mod.MetaPath + SwitchedOffSuffix;
            if (File.Exists(SafeCopy.Long(target)))
                throw new IOException("a file named " + Path.GetFileName(target) + " is already there");
            File.Move(SafeCopy.Long(mod.MetaPath), SafeCopy.Long(target));
            mod.MetaPath = target;
            mod.SwitchedOff = true;
        }

        /// <summary>Undoes <see cref="SwitchOff"/>. Returns false when a mod.json already exists there.</summary>
        public static bool SwitchOn(ModReport mod)
        {
            if (!mod.SwitchedOff)
                return true;
            string target = Path.Combine(mod.Folder, MetaFile);
            if (File.Exists(SafeCopy.Long(target)))
                return false;
            File.Move(SafeCopy.Long(mod.MetaPath), SafeCopy.Long(target));
            mod.MetaPath = target;
            mod.SwitchedOff = false;
            return true;
        }

        /// <summary>Removes // and /* */ comments while leaving string and char literals intact.</summary>
        public static string StripComments(string src)
        {
            var sb = new StringBuilder(src.Length);
            int i = 0;
            while (i < src.Length)
            {
                char c = src[i];
                char next = i + 1 < src.Length ? src[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    while (i < src.Length && src[i] != '\n')
                        i++;
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? src.Length : end + 2;
                    sb.Append(' ');
                    continue;
                }

                bool verbatim = (c == '@' && next == '"') || (c == '$' && next == '@' && i + 2 < src.Length && src[i + 2] == '"')
                    || (c == '@' && next == '$' && i + 2 < src.Length && src[i + 2] == '"');
                if (verbatim)
                {
                    int quote = src.IndexOf('"', i);
                    sb.Append(src, i, quote - i + 1);
                    i = quote + 1;
                    while (i < src.Length)
                    {
                        if (src[i] == '"' && i + 1 < src.Length && src[i + 1] == '"')
                        {
                            sb.Append("\"\"");
                            i += 2;
                            continue;
                        }
                        sb.Append(src[i]);
                        if (src[i++] == '"')
                            break;
                    }
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    char close = c;
                    sb.Append(c);
                    i++;
                    while (i < src.Length && src[i] != '\n')
                    {
                        char d = src[i];
                        sb.Append(d);
                        i++;
                        if (d == '\\' && i < src.Length)
                        {
                            sb.Append(src[i]);
                            i++;
                            continue;
                        }
                        if (d == close)
                            break;
                    }
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string JsonUnescape(string s)
        {
            try
            {
                return Regex.Unescape(s).Trim();
            }
            catch (ArgumentException)
            {
                return s.Trim();
            }
        }
    }
}
