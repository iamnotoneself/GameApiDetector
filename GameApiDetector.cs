using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GameApiDetector
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--scan", StringComparison.OrdinalIgnoreCase))
            {
                CliScan.Run();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "游戏图形 API 检测器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm());
        }
    }

    internal static class Native
    {
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const int LIST_MODULES_ALL = 0x03;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EnumProcessModulesEx(IntPtr process, IntPtr[] modules,
            int bufferSize, out int bytesNeeded, int filter);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetModuleFileNameEx(IntPtr process, IntPtr module,
            StringBuilder fileName, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool IsWow64Process(IntPtr process, out bool wow64);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags,
            StringBuilder exeName, ref int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void SetCueBanner(IntPtr handle, string text)
        {
            SendMessage(handle, 0x1501, (IntPtr)1, text);
        }
    }

    internal static class UiTheme
    {
        public static readonly Color Accent = Color.FromArgb(13, 110, 105);
        public static readonly Color AccentHover = Color.FromArgb(9, 92, 88);
        public static readonly Color AccentDown = Color.FromArgb(6, 74, 71);
        public static readonly Color Surface = Color.FromArgb(247, 249, 251);
        public static readonly Color Border = Color.FromArgb(226, 232, 240);
        public static readonly Color TextMuted = Color.FromArgb(100, 116, 139);

        public static void ApplyIcon(Form form)
        {
            if (form == null) return;
            try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
        }

        public static void Primary(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentDown;
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.Cursor = Cursors.Hand;
        }

        public static void Secondary(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Surface;
            button.FlatAppearance.MouseDownBackColor = Border;
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(30, 41, 59);
            button.Cursor = Cursors.Hand;
        }
    }

    internal sealed class GameInfo
    {
        public string Name = "";
        public string Platform = "";
        public string Root = "";
        public string ExePath = "";
    }

    internal static class GameLibrary
    {
        private static readonly object Sync = new object();
        private static List<GameInfo> games;

        public static List<GameInfo> All
        {
            get
            {
                lock (Sync)
                {
                    if (games == null)
                    {
                        List<GameInfo> list = new List<GameInfo>();
                        try { LoadSteam(list); }
                        catch { }
                        try { LoadEpic(list); }
                        catch { }
                        try { LoadGog(list); }
                        catch { }
                        try { LoadWindowsGames(list); }
                        catch { }
                        try { LoadEa(list); }
                        catch { }
                        try { LoadUbisoft(list); }
                        catch { }
                        try { LoadBattleNet(list); }
                        catch { }
                        try { LoadXbox(list); }
                        catch { }
                        try { AddSavedGames(list); }
                        catch { }
                        games = Deduplicate(list);
                    }
                    return games;
                }
            }
        }

        public static void Reload()
        {
            lock (Sync)
            {
                games = null;
            }
        }

        private static void AddSavedGames(List<GameInfo> list)
        {
            List<GameInfo> saved = SavedGames.Load();
            for (int i = 0; i < saved.Count; i++)
            {
                GameInfo game = saved[i];
                if (game.Root.Length == 0) continue;
                list.Add(game);
            }
        }

        public static void WarmUp()
        {
            List<GameInfo> list = All;
            if (list == null) return;
        }

        public static GameInfo Match(string exeFullPath)
        {
            if (exeFullPath == null || exeFullPath.Length == 0) return null;
            List<GameInfo> list = All;

            for (int i = 0; i < list.Count; i++)
            {
                GameInfo game = list[i];
                if (game.ExePath.Length == 0) continue;
                if (string.Equals(game.ExePath, exeFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return game;
                }
            }

            GameInfo best = null;
            for (int i = 0; i < list.Count; i++)
            {
                GameInfo game = list[i];
                if (game.Root.Length == 0) continue;
                if (best != null && game.Root.Length <= best.Root.Length) continue;
                if (IsUnderDirectory(exeFullPath, game.Root)) best = game;
            }
            return best;
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            string dir = directory;
            if (dir.Length == 0) return false;
            if (!dir.EndsWith("\\")) dir = dir + "\\";
            if (path.Length < dir.Length) return false;
            return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }

        private static List<GameInfo> Deduplicate(List<GameInfo> list)
        {
            List<GameInfo> result = new List<GameInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                GameInfo game = list[i];
                if (game.Root.Length == 0) continue;
                string key = game.Root;
                if (!seen.Add(key)) continue;
                result.Add(game);
            }
            return result;
        }

        private static string EnsureDir(string path)
        {
            if (path == null) return "";
            string value = path.Trim().Trim('"');
            value = value.Replace('/', '\\');
            if (value.Length == 0) return "";
            while (value.Length > 3 && value.EndsWith("\\")) value = value.Substring(0, value.Length - 1);
            if (!value.EndsWith("\\")) value = value + "\\";
            return value;
        }

        private static string FirstMatch(string text, string pattern)
        {
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string UnescapeVdf(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    char next = value[i + 1];
                    if (next == '\\' || next == '"')
                    {
                        builder.Append(next);
                        i++;
                        continue;
                    }
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static string UnescapeJson(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(c);
                    continue;
                }
                char next = value[i + 1];
                i++;
                if (next == 'n') builder.Append('\n');
                else if (next == 'r') builder.Append('\r');
                else if (next == 't') builder.Append('\t');
                else if (next == 'u' && i + 4 < value.Length)
                {
                    int code = 0;
                    try { code = Convert.ToInt32(value.Substring(i + 1, 4), 16); }
                    catch { code = 0; }
                    if (code > 0) builder.Append((char)code);
                    i += 4;
                }
                else builder.Append(next);
            }
            return builder.ToString();
        }

        private static string GetString(RegistryKey key, string name)
        {
            try
            {
                object value = key.GetValue(name);
                if (value == null) return "";
                return Convert.ToString(value).Trim();
            }
            catch
            {
                return "";
            }
        }

        private static void LoadSteam(List<GameInfo> list)
        {
            string steamPath = "";
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (key != null) steamPath = GetString(key, "SteamPath");
            }
            if (steamPath.Length == 0)
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Wow6432Node\Valve\Steam"))
                {
                    if (key != null) steamPath = GetString(key, "SteamPath");
                }
            }
            if (steamPath.Length == 0) return;

            steamPath = EnsureDir(steamPath);
            if (steamPath.Length == 0) return;

            List<string> libraries = new List<string>();
            libraries.Add(steamPath);
            string vdf = Path.Combine(Path.Combine(steamPath, "steamapps"), "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                try
                {
                    string text = File.ReadAllText(vdf);
                    MatchCollection matches = Regex.Matches(text, "\"path\"\\s*\"([^\"]*)\"",
                        RegexOptions.IgnoreCase);
                    foreach (Match match in matches)
                    {
                        string library = EnsureDir(UnescapeVdf(match.Groups[1].Value));
                        if (library.Length == 0) continue;
                        bool exists = false;
                        for (int i = 0; i < libraries.Count; i++)
                        {
                            if (string.Equals(libraries[i], library, StringComparison.OrdinalIgnoreCase))
                            {
                                exists = true;
                                break;
                            }
                        }
                        if (!exists) libraries.Add(library);
                    }
                }
                catch { }
            }

            for (int i = 0; i < libraries.Count; i++)
            {
                try { LoadSteamLibrary(list, libraries[i]); }
                catch { }
            }
        }

        private static void LoadSteamLibrary(List<GameInfo> list, string library)
        {
            string steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) return;
            string common = Path.Combine(steamApps, "common");
            string[] manifests;
            try { manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf"); }
            catch { return; }

            for (int i = 0; i < manifests.Length; i++)
            {
                try
                {
                    string text = File.ReadAllText(manifests[i]);
                    string name = UnescapeVdf(FirstMatch(text, "\"name\"\\s*\"([^\"]*)\""));
                    string installDir = UnescapeVdf(FirstMatch(text, "\"installdir\"\\s*\"([^\"]*)\""));
                    if (installDir.Length == 0) continue;

                    string root = EnsureDir(Path.Combine(common, installDir));
                    if (root.Length == 0 || !Directory.Exists(root)) continue;

                    GameInfo game = new GameInfo();
                    game.Name = name.Length > 0 ? name : installDir;
                    game.Platform = "Steam";
                    game.Root = root;
                    list.Add(game);
                }
                catch { }
            }
        }

        private static void LoadEpic(List<GameInfo> list)
        {
            string programData = Environment.GetEnvironmentVariable("ProgramData");
            if (programData == null || programData.Length == 0) programData = @"C:\ProgramData";
            string dir = Path.Combine(programData, Path.Combine("Epic",
                Path.Combine("EpicGamesLauncher", Path.Combine("Data", "Manifests"))));
            if (!Directory.Exists(dir)) return;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.item"); }
            catch { return; }

            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    string text = File.ReadAllText(files[i]);
                    string name = UnescapeJson(FirstMatch(text,
                        "\"DisplayName\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""));
                    string installLocation = UnescapeJson(FirstMatch(text,
                        "\"InstallLocation\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""));
                    string launchExecutable = UnescapeJson(FirstMatch(text,
                        "\"LaunchExecutable\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""));
                    if (installLocation.Length == 0) continue;

                    string root = EnsureDir(installLocation);
                    if (root.Length == 0 || !Directory.Exists(root)) continue;

                    GameInfo game = new GameInfo();
                    game.Name = name.Length > 0 ? name : Path.GetFileName(
                        installLocation.TrimEnd('\\'));
                    game.Platform = "Epic";
                    game.Root = root;

                    if (launchExecutable.Length > 0)
                    {
                        try { game.ExePath = Path.Combine(root, launchExecutable); }
                        catch { game.ExePath = ""; }
                    }
                    list.Add(game);
                }
                catch { }
            }
        }

        private static void LoadGog(List<GameInfo> list)
        {
            RegistryKey baseKey = null;
            RegistryKey gamesKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                gamesKey = baseKey.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
                if (gamesKey == null)
                {
                    if (baseKey != null) baseKey.Close();
                    baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                    gamesKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                }
                if (gamesKey == null) return;

                string[] names;
                try { names = gamesKey.GetSubKeyNames(); }
                catch { return; }

                for (int i = 0; i < names.Length; i++)
                {
                    RegistryKey sub = null;
                    try
                    {
                        sub = gamesKey.OpenSubKey(names[i]);
                        if (sub == null) continue;

                        string gameName = GetString(sub, "gameName");
                        string path = GetString(sub, "path");
                        string exe = GetString(sub, "exe");
                        if (path.Length == 0) continue;

                        GameInfo game = new GameInfo();
                        game.Name = gameName.Length > 0 ? gameName : names[i];
                        game.Platform = "GOG";
                        game.Root = EnsureDir(path);
                        if (game.Root.Length == 0) continue;

                        if (exe.Length > 0)
                        {
                            try
                            {
                                if (exe.Length > 1 && exe[1] == ':') game.ExePath = exe;
                                else game.ExePath = Path.Combine(game.Root, exe.TrimStart('\\'));
                            }
                            catch { game.ExePath = ""; }
                        }
                        list.Add(game);
                    }
                    catch { }
                    finally
                    {
                        if (sub != null) sub.Close();
                    }
                }
            }
            finally
            {
                if (gamesKey != null) gamesKey.Close();
                if (baseKey != null) baseKey.Close();
            }
        }

        private static void LoadWindowsGames(List<GameInfo> list)
        {
            RegistryKey children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children == null) return;
            try
            {
                string[] names;
                try { names = children.GetSubKeyNames(); }
                catch { return; }

                for (int i = 0; i < names.Length; i++)
                {
                    RegistryKey sub = null;
                    try
                    {
                        sub = children.OpenSubKey(names[i]);
                        if (sub == null) continue;

                        string exePath = GetString(sub, "MatchedExeFullPath");
                        if (exePath.Length == 0) continue;
                        exePath = exePath.Replace('/', '\\');
                        if (!File.Exists(exePath)) continue;
                        if (IsNonGameExe(exePath)) continue;

                        string root;
                        try { root = EnsureDir(Path.GetDirectoryName(exePath)); }
                        catch { continue; }
                        if (root.Length == 0) continue;

                        GameInfo game = new GameInfo();
                        game.Platform = "Windows";
                        game.ExePath = exePath;
                        game.Root = root;
                        game.Name = DescribeExecutable(exePath);
                        list.Add(game);
                    }
                    catch { }
                    finally
                    {
                        if (sub != null) sub.Close();
                    }
                }
            }
            finally
            {
                children.Close();
            }
        }

        private static readonly string[] NonGameExe =
        {
            "java", "javaw", "python", "pythonw", "node", "chrome", "msedge",
            "firefox", "iexplore", "explorer", "notepad", "cmd", "powershell",
            "code", "devenv", "spotify", "vlc", "obs64", "discord", "werfault",
            "rundll32", "msiexec", "steam", "steamwebhelper", "epicgameslauncher"
        };

        private static bool IsNonGameExe(string exePath)
        {
            string stem = "";
            try { stem = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant(); }
            catch { return false; }

            for (int i = 0; i < NonGameExe.Length; i++)
            {
                if (stem == NonGameExe[i]) return true;
            }
            return false;
        }

        private static void LoadEa(List<GameInfo> list)
        {
            LoadRegistryRoots(list, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\EA Games",
                "EA", "Install Dir", "DisplayName", "");
            LoadRegistryRoots(list, RegistryView.Registry32, @"SOFTWARE\EA Games",
                "EA", "Install Dir", "DisplayName", "");
        }

        private static void LoadUbisoft(List<GameInfo> list)
        {
            LoadRegistryRoots(list, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs",
                "Ubisoft", "InstallDir", "", "");
            LoadRegistryRoots(list, RegistryView.Registry32, @"SOFTWARE\Ubisoft\Launcher\Installs",
                "Ubisoft", "InstallDir", "", "");
        }

        private static void LoadBattleNet(List<GameInfo> list)
        {
            LoadRegistryRoots(list, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Blizzard Entertainment",
                "Battle.net", "InstallPath", "", "");
            LoadRegistryRoots(list, RegistryView.Registry32, @"SOFTWARE\Blizzard Entertainment",
                "Battle.net", "InstallPath", "", "");
        }

        private static void LoadXbox(List<GameInfo> list)
        {
            LoadXboxUninstall(list, RegistryView.Registry64);
            LoadXboxUninstall(list, RegistryView.Registry32);

            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    string name;
                    try { name = drives[i].RootDirectory.FullName; }
                    catch { continue; }
                    if (name.Length == 0) continue;
                    string root = Path.Combine(name, "XboxGames");
                    if (!Directory.Exists(root)) continue;
                    LoadSubFolders(list, root, "Xbox");
                }
            }
            catch { }
        }

        private static void LoadXboxUninstall(List<GameInfo> list, RegistryView view)
        {
            RegistryKey baseKey = null;
            RegistryKey key = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                key = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key == null) return;

                string[] names;
                try { names = key.GetSubKeyNames(); }
                catch { return; }

                for (int i = 0; i < names.Length; i++)
                {
                    RegistryKey sub = null;
                    try
                    {
                        sub = key.OpenSubKey(names[i]);
                        if (sub == null) continue;
                        string location = GetString(sub, "InstallLocation");
                        if (location.Length == 0) continue;
                        if (location.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                            location.IndexOf("\\XboxGames\\", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }
                        string gameName = GetString(sub, "DisplayName");
                        GameInfo game = MakeGame(gameName.Length > 0 ? gameName : names[i], "Xbox",
                            location, "");
                        if (game != null) list.Add(game);
                    }
                    catch { }
                    finally
                    {
                        if (sub != null) sub.Close();
                    }
                }
            }
            finally
            {
                if (key != null) key.Close();
                if (baseKey != null) baseKey.Close();
            }
        }

        private static void LoadRegistryRoots(List<GameInfo> list, RegistryView view, string subPath,
            string platform, string dirValue, string nameValue, string requiredFragment)
        {
            RegistryKey baseKey = null;
            RegistryKey key = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                key = baseKey.OpenSubKey(subPath);
                if (key == null) return;

                string[] names;
                try { names = key.GetSubKeyNames(); }
                catch { return; }

                for (int i = 0; i < names.Length; i++)
                {
                    RegistryKey sub = null;
                    try
                    {
                        sub = key.OpenSubKey(names[i]);
                        if (sub == null) continue;

                        string dir = GetString(sub, dirValue);
                        if (dir.Length == 0) continue;
                        if (requiredFragment.Length > 0 &&
                            dir.IndexOf(requiredFragment, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        string gameName = nameValue.Length > 0 ? GetString(sub, nameValue) : "";
                        if (gameName.Length == 0 && !IsGuidLike(names[i])) gameName = names[i];

                        GameInfo game = MakeGame(gameName, platform, dir, "");
                        if (game != null) list.Add(game);
                    }
                    catch { }
                    finally
                    {
                        if (sub != null) sub.Close();
                    }
                }
            }
            finally
            {
                if (key != null) key.Close();
                if (baseKey != null) baseKey.Close();
            }
        }

        private static void LoadSubFolders(List<GameInfo> list, string root, string platform)
        {
            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch { return; }

            for (int i = 0; i < folders.Length; i++)
            {
                string folder = folders[i];
                string leaf = Path.GetFileName(folder);
                if (leaf.Length == 0) continue;
                if (string.Equals(leaf, "Content", StringComparison.OrdinalIgnoreCase)) continue;

                string target = folder;
                try
                {
                    string content = Path.Combine(folder, "Content");
                    if (Directory.Exists(content)) target = content;
                }
                catch { }

                GameInfo game = MakeGame(leaf, platform, target, "");
                if (game != null) list.Add(game);
            }
        }

        private static GameInfo MakeGame(string name, string platform, string rootPath, string exePath)
        {
            string root;
            try { root = EnsureDir(rootPath); }
            catch { return null; }
            if (root.Length == 0) return null;
            if (!Directory.Exists(root)) return null;

            GameInfo game = new GameInfo();
            game.Name = name != null && name.Length > 0 ? name.Trim() : Path.GetFileName(root.TrimEnd('\\'));
            game.Platform = platform;
            game.Root = root;
            if (exePath != null && exePath.Length > 0) game.ExePath = exePath;
            return game;
        }

        private static bool IsGuidLike(string value)
        {
            if (value == null || value.Length < 32) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
                          (c >= 'A' && c <= 'F') || c == '-' || c == '{' || c == '}';
                if (!ok) return false;
            }
            return true;
        }

        private static string DescribeExecutable(string exePath)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);
                if (info.FileDescription != null && info.FileDescription.Trim().Length > 0)
                {
                    return info.FileDescription.Trim();
                }
            }
            catch { }

            try { return Path.GetFileNameWithoutExtension(exePath); }
            catch { return exePath; }
        }
    }

    internal static class SavedGames
    {
        private static readonly object Sync = new object();
        private static int countCache = -1;

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameApiDetector");
                return Path.Combine(dir, "games.tsv");
            }
        }

        public static List<GameInfo> Load()
        {
            List<GameInfo> list = new List<GameInfo>();
            lock (Sync)
            {
                string file = FilePath;
                if (!File.Exists(file)) return list;

                string[] lines;
                try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                catch { return list; }

                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line == null || line.Length == 0) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;

                    string platform = parts[0].Trim();
                    string name = parts[1].Trim();
                    string exe = parts[2].Trim();
                    if (exe.Length == 0 || !seen.Add(exe)) continue;
                    if (!File.Exists(exe)) continue;

                    GameInfo game = new GameInfo();
                    game.Name = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(exe);
                    game.Platform = platform.Length > 0 ? platform : "本地";
                    game.ExePath = exe;
                    game.Root = EnsureDirectory(exe);
                    list.Add(game);
                }
            }
            return list;
        }

        public static bool Add(string exePath, string name, string platform)
        {
            if (exePath == null || exePath.Length == 0) return false;
            string exe = exePath.Trim();
            if (exe.Length == 0 || !File.Exists(exe)) return false;

            lock (Sync)
            {
                List<GameInfo> list = Load();
                bool changed = false;
                bool found = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!string.Equals(list[i].ExePath, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    found = true;
                    if (name.Length > 0 && list[i].Name != name)
                    {
                        list[i].Name = name;
                        changed = true;
                    }
                    if (platform.Length > 0 && list[i].Platform != platform)
                    {
                        list[i].Platform = platform;
                        changed = true;
                    }
                    break;
                }

                if (!found)
                {
                    GameInfo game = new GameInfo();
                    game.Name = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(exe);
                    game.Platform = platform.Length > 0 ? platform : "本地";
                    game.ExePath = exe;
                    game.Root = EnsureDirectory(exe);
                    list.Add(game);
                    changed = true;
                }

                if (changed) WriteAll(list);
                return changed;
            }
        }

        public static bool Remove(string exePath)
        {
            if (exePath == null || exePath.Length == 0) return false;
            lock (Sync)
            {
                List<GameInfo> list = Load();
                for (int i = 0; i < list.Count; i++)
                {
                    if (!string.Equals(list[i].ExePath, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                    list.RemoveAt(i);
                    WriteAll(list);
                    return true;
                }
            }
            return false;
        }

        public static int Count()
        {
            lock (Sync)
            {
                if (countCache < 0) countCache = Load().Count;
                return countCache;
            }
        }

        private static void WriteAll(List<GameInfo> list)
        {
            string file = FilePath;
            try
            {
                string dir = Path.GetDirectoryName(file);
                if (dir.Length > 0 && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < list.Count; i++)
                {
                    GameInfo game = list[i];
                    builder.Append(game.Platform.Replace('\t', ' ')).Append('\t');
                    builder.Append(game.Name.Replace('\t', ' ')).Append('\t');
                    builder.Append(game.ExePath);
                    builder.Append("\r\n");
                }
                File.WriteAllText(file, builder.ToString(), new UTF8Encoding(false));
                countCache = list.Count;
            }
            catch { }
        }

        private static string EnsureDirectory(string exePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(exePath);
                if (dir == null || dir.Length == 0) return "";
                return dir.TrimEnd('\\') + "\\";
            }
            catch { return ""; }
        }
    }

    internal sealed class ProcEntry
    {
        public int Pid;
        public int RunPid;
        public string Name = "";
        public string Title = "";
        public string Path = "";
        public string Arch = "未知";
        public string Api = "未检测到";
        public string KeyModules = "";
        public string[] GfxModules = new string[0];
        public int ModuleCount;
        public string Error = "";
        public bool HasWindow;
        public string GameName = "";
        public string Platform = "";
        public bool IsGame;
        public bool GameKnown;
        public string Dxvk = "";

        public bool HasGraphics
        {
            get
            {
                return Api != "未检测到" && Api != "无法读取" && Api.Length > 0;
            }
        }

        public bool Running
        {
            get { return RunPid > 0; }
        }
    }

    internal static class GameColumns
    {
        public static string GameText(ProcEntry entry)
        {
            if (entry == null || !entry.IsGame) return "";
            if (entry.GameKnown)
            {
                return entry.GameName.Length > 0 ? entry.GameName : entry.Name;
            }
            return "未知游戏 (" + entry.Name + ")";
        }

        public static string PlatformText(ProcEntry entry)
        {
            if (entry == null || !entry.IsGame) return "";
            return entry.Platform.Length > 0 ? entry.Platform : "未知";
        }
    }

    internal static class GameExe
    {
        private static readonly string[] Noise =
        {
            "unins", "uninstall", "setup", "install", "vcredist", "dxsetup", "directx",
            "dotnet", "crash", "report", "updater", "update", "helper", "activation",
            "eula", "touchup", "launcher", "host64", "feed"
        };

        private static readonly string[] SkipDirs =
        {
            "_commonredist", "redist", "directx", "support", "docs", "tools"
        };

        public static string Locate(GameInfo game)
        {
            if (game == null) return "";
            if (game.ExePath.Length > 0 && File.Exists(game.ExePath)) return game.ExePath;

            string root = game.Root;
            if (root.Length == 0 || !Directory.Exists(root)) return "";

            string found = Search(root, game.Name, 0);
            return found;
        }

        private static string Search(string dir, string gameName, int depth)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe"); }
            catch { files = new string[0]; }

            if (files.Length > 0)
            {
                string target = Normalize(gameName);
                string folder = Normalize(new DirectoryInfo(dir).Name);
                string best = "";
                long bestSize = -1;
                for (int i = 0; i < files.Length; i++)
                {
                    string file = files[i];
                    string stem = Normalize(Path.GetFileNameWithoutExtension(file));
                    if (IsNoise(stem)) continue;

                    long size = 0;
                    try { size = new FileInfo(file).Length; }
                    catch { }

                    if (target.Length > 0 && stem == target) return file;
                    if (folder.Length > 0 && stem == folder) return file;
                    if (size > bestSize)
                    {
                        bestSize = size;
                        best = file;
                    }
                }

                if (best.Length > 0) return best;
            }

            if (depth >= 2) return "";

            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); }
            catch { return ""; }

            for (int i = 0; i < dirs.Length; i++)
            {
                string name = Path.GetFileName(dirs[i]).ToLowerInvariant();
                for (int j = 0; j < SkipDirs.Length; j++)
                {
                    if (name == SkipDirs[j]) { name = null; break; }
                }
                if (name == null) continue;

                string child = Search(dirs[i], gameName, depth + 1);
                if (child.Length > 0) return child;
            }
            return "";
        }

        private static bool IsNoise(string stem)
        {
            for (int i = 0; i < Noise.Length; i++)
            {
                if (stem.IndexOf(Noise[i], StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static string Normalize(string value)
        {
            if (value == null) return "";
            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }
    }

    internal static class DxvkProbe
    {
        public const string MarkerName = ".dxvk_by_gad.txt";

        private static readonly string[] Candidates = new string[]
        {
            "d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll"
        };

        public static bool IsDxvkFile(string path)
        {
            try
            {
                if (path == null || !File.Exists(path)) return false;
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                if (info.CompanyName != null &&
                    info.CompanyName.IndexOf("DXVK", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (info.ProductName != null &&
                    info.ProductName.IndexOf("DXVK", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }

        public static List<string> InstalledFiles(string gameDir)
        {
            List<string> result = new List<string>();
            if (gameDir == null || gameDir.Length == 0) return result;
            for (int i = 0; i < Candidates.Length; i++)
            {
                string path;
                try { path = Path.Combine(gameDir, Candidates[i]); }
                catch { continue; }
                if (IsDxvkFile(path)) result.Add(Candidates[i]);
            }
            return result;
        }

        public static string Probe(string exePath)
        {
            string dir = SafeDirectory(exePath);
            if (dir.Length == 0) return "";
            List<string> files = InstalledFiles(dir);
            if (files.Count == 0) return "";
            return "DXVK（" + string.Join(" + ", files.ToArray()) + "）";
        }

        public static bool IsInstalled(string exePath)
        {
            return Probe(exePath).Length > 0;
        }

        public static string SafeDirectory(string exePath)
        {
            try
            {
                if (exePath == null || exePath.Length == 0) return "";
                string dir = Path.GetDirectoryName(exePath);
                return dir == null ? "" : dir;
            }
            catch { return ""; }
        }

        public static Dictionary<string, string> ReadMarker(string gameDir)
        {
            Dictionary<string, string> records = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                string marker = Path.Combine(gameDir, MarkerName);
                if (!File.Exists(marker)) return records;
                string[] lines = File.ReadAllLines(marker);
                for (int i = 0; i < lines.Length; i++)
                {
                    string[] parts = lines[i].Split('|');
                    if (parts.Length == 2 && parts[0].Length > 0) records[parts[0]] = parts[1];
                }
            }
            catch { }
            return records;
        }

        public static void WriteMarker(string gameDir, Dictionary<string, string> records)
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> pair in records)
                {
                    lines.Add(pair.Key + "|" + pair.Value);
                }
                File.WriteAllLines(Path.Combine(gameDir, MarkerName), lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        public static int Restore(string gameDir, out List<string> errors)
        {
            errors = new List<string>();
            if (gameDir == null || gameDir.Length == 0 || !Directory.Exists(gameDir)) return 0;

            Dictionary<string, string> records = ReadMarker(gameDir);
            int restored = 0;

            foreach (KeyValuePair<string, string> pair in records)
            {
                string dst = Path.Combine(gameDir, pair.Key);
                string bak = dst + ".dxvk_bak";
                try
                {
                    if (File.Exists(bak))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(bak, dst);
                        restored++;
                    }
                    else if (File.Exists(dst) && IsDxvkFile(dst))
                    {
                        File.Delete(dst);
                        restored++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(pair.Key + "：" + ex.Message);
                }
            }

            for (int i = 0; i < Candidates.Length; i++)
            {
                string name = Candidates[i];
                if (records.ContainsKey(name)) continue;
                try
                {
                    string dst = Path.Combine(gameDir, name);
                    if (!IsDxvkFile(dst)) continue;
                    string bak = dst + ".dxvk_bak";
                    if (File.Exists(bak))
                    {
                        File.Delete(dst);
                        File.Move(bak, dst);
                    }
                    else
                    {
                        File.Delete(dst);
                    }
                    restored++;
                }
                catch (Exception ex)
                {
                    errors.Add(name + "：" + ex.Message);
                }
            }

            try { File.Delete(Path.Combine(gameDir, MarkerName)); }
            catch { }

            return restored;
        }
    }

    internal static class GfxScanner
    {
        private static readonly HashSet<string> GraphicsModules = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "vulkan-1.dll",
            "d3d12.dll", "d3d12core.dll",
            "d3d11.dll", "d3d11on12.dll",
            "d3d10.dll", "d3d10_1.dll",
            "d3d9.dll", "d3d9on12.dll",
            "opengl32.dll",
            "nvoglv64.dll", "nvoglv32.dll",
            "atioglxx.dll", "atio6axx.dll",
            "ig7icd64.dll", "ig7icd32.dll",
            "ig9icd64.dll", "ig9icd32.dll",
            "ig10icd64.dll", "ig10icd32.dll"
        };

        public static string ReadPath(Process process)
        {
            return GetProcessPath(process);
        }

        public static ProcEntry Inspect(Process process)
        {
            ProcEntry entry = new ProcEntry();
            entry.Pid = process.Id;
            try { entry.Name = process.ProcessName; }
            catch { entry.Name = "?"; }
            try { entry.Title = process.MainWindowTitle; }
            catch { entry.Title = ""; }
            try { entry.HasWindow = process.MainWindowHandle != IntPtr.Zero; }
            catch { entry.HasWindow = false; }

            entry.Path = GetProcessPath(process);
            entry.Arch = GetBitness(entry.Pid);

            string[] files;
            string error;
            if (!TryGetModuleNames(entry.Pid, out files, out error))
            {
                entry.Error = error;
                entry.Api = "无法读取";
                ApplyGameInfo(entry);
                return entry;
            }

            entry.ModuleCount = files.Length;
            entry.GfxModules = files
                .Where(delegate(string f) { return GraphicsModules.Contains(f); })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(delegate(string f) { return f; }, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            DetectApi(entry);
            ApplyGameInfo(entry);
            return entry;
        }

        private static readonly HashSet<string> NonGameProcessNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamwebhelper", "steamerrorreporter", "steamservice", "gameoverlayui",
            "epicgameslauncher", "epicwebhelper", "galaxyclient", "galaxyclientservice",
            "battle.net", "agent", "origin", "eaapp", "eadesktop", "ubisoftconnect", "upc",
            "ubisoftgamelauncher", "chrome", "msedge", "msedgewebview2", "firefox", "opera",
            "brave", "iexplore", "vivaldi", "safari", "code", "devenv", "explorer", "dwm",
            "searchapp", "searchhost", "shellexperiencehost", "startmenuexperiencehost",
            "applicationframehost", "teams", "ms-teams", "discord", "spotify", "vlc",
            "potplayer", "potplayermini64", "potplayermini32", "obs64", "obs32", "obs",
            "photoshop", "illustrator", "wps", "wpspdf", "et", "wpsoffice", "winword",
            "excel", "powerpnt", "outlook", "onenote", "notepad", "notepad++", "cmd",
            "powershell", "pwsh", "windowsterminal", "wt", "conhost", "alacritty",
            "sublime_text", "pycharm64", "idea64", "devenv.exe", "taskmgr", "regedit",
            "mmc", "control", "rundll32", "svchost", "dllhost", "fontdrvhost",
            "csrss", "winlogon", "lsass", "services", "smss", "wininit", "spoolsv",
            "textinputhost", "securityhealthsystray", "ctfmon", "runtimebroker",
            "nvidia share", "nvcontainer", "radeonsoftware", "afterburner",
            "msiexec", "update", "updater", "crashpad_handler", "dxdiag", "wmplayer"
        };

        private static void ApplyGameInfo(ProcEntry entry)
        {
            if (entry.Path.Length == 0) return;

            GameInfo game = null;
            try { game = GameLibrary.Match(entry.Path); }
            catch { }

            if (game != null)
            {
                entry.IsGame = true;
                entry.GameKnown = true;
                entry.GameName = game.Name.Length > 0 ? game.Name : entry.Name;
                entry.Platform = game.Platform;
                entry.Dxvk = DxvkProbe.Probe(entry.Path);
                return;
            }

            if (IsLikelyGame(entry))
            {
                entry.IsGame = true;
                entry.GameKnown = false;
                entry.GameName = "未知游戏";
                entry.Dxvk = DxvkProbe.Probe(entry.Path);
            }
        }

        private static readonly string[] NonGameSuffixes =
        {
            "tray", "helper", "service", "updater", "broker", "daemon", "agent"
        };

        private static bool HasNonGameSuffix(string name)
        {
            for (int i = 0; i < NonGameSuffixes.Length; i++)
            {
                if (name.EndsWith(NonGameSuffixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsLikelyGame(ProcEntry entry)
        {
            if (!entry.HasGraphics) return false;
            if (!entry.HasWindow && string.IsNullOrEmpty(entry.Title)) return false;

            string name = entry.Name;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 4);
            }
            if (NonGameProcessNames.Contains(name)) return false;
            if (HasNonGameSuffix(name)) return false;

            string path = entry.Path;
            if (path.Length == 0) return true;

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && IsUnderDirectory(path, windows)) return false;

            if (path.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (IsUnderDirectory(path, localAppData)) return false;
            if (IsUnderDirectory(path, roamingAppData)) return false;

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string commonFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            string commonFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);

            if (IsUnderDirectory(path, commonFiles)) return false;
            if (IsUnderDirectory(path, commonFilesX86)) return false;
            if (IsUnderDirectory(path, Path.Combine(programFiles, "WindowsApps"))) return false;
            if (IsUnderDirectory(path, Path.Combine(programFilesX86, "WindowsApps"))) return false;
            if (IsUnderDirectory(path, Path.Combine(programFiles, "Microsoft"))) return false;
            if (IsUnderDirectory(path, Path.Combine(programFilesX86, "Microsoft"))) return false;
            if (IsUnderDirectory(path, Path.Combine(programFiles, "Common Files"))) return false;
            if (IsUnderDirectory(path, Path.Combine(programFilesX86, "Common Files"))) return false;

            return true;
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            if (path.Length == 0 || directory.Length == 0) return false;
            string dir = directory;
            if (!dir.EndsWith("\\")) dir = dir + "\\";
            if (path.Length < dir.Length) return false;
            return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }

        private static void DetectApi(ProcEntry entry)
        {
            bool vulkan = Has(entry.GfxModules, "vulkan-1.dll");
            bool d12 = Has(entry.GfxModules, "d3d12.dll") || Has(entry.GfxModules, "d3d12core.dll");
            bool d11 = Has(entry.GfxModules, "d3d11.dll") || Has(entry.GfxModules, "d3d11on12.dll");
            bool gl = Has(entry.GfxModules, "opengl32.dll");
            bool d10 = Has(entry.GfxModules, "d3d10.dll") || Has(entry.GfxModules, "d3d10_1.dll");
            bool d9 = Has(entry.GfxModules, "d3d9.dll") || Has(entry.GfxModules, "d3d9on12.dll");

            List<string> apis = new List<string>();
            if (vulkan) apis.Add("Vulkan");
            if (d12) apis.Add("DirectX 12");
            if (d11) apis.Add("DirectX 11");
            if (gl) apis.Add("OpenGL");
            if (d10) apis.Add("DirectX 10");
            if (d9) apis.Add("DirectX 9");

            if (apis.Count == 0)
            {
                entry.Api = "未检测到";
                entry.KeyModules = string.Join(", ", entry.GfxModules);
                return;
            }

            entry.Api = apis[0];
            if (apis.Count > 1)
            {
                entry.Api = apis[0] + "（另见 " + string.Join(" / ",
                    apis.Skip(1).ToArray()) + "）";
            }
            entry.KeyModules = string.Join(", ", entry.GfxModules);
        }

        private static bool Has(string[] files, string name)
        {
            for (int i = 0; i < files.Length; i++)
            {
                if (string.Equals(files[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool TryGetModuleNames(int pid, out string[] names, out string error)
        {
            names = new string[0];
            error = "";
            IntPtr handle = Native.OpenProcess(
                Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
            if (handle == IntPtr.Zero)
            {
                error = "无法打开进程（请尝试以管理员身份运行，或进程已退出）";
                return false;
            }

            try
            {
                int needed;
                Native.EnumProcessModulesEx(handle, null, 0, out needed, Native.LIST_MODULES_ALL);
                if (needed <= 0)
                {
                    error = "无法枚举进程模块";
                    return false;
                }

                IntPtr[] modules = new IntPtr[needed / IntPtr.Size];
                if (!Native.EnumProcessModulesEx(handle, modules, needed, out needed,
                        Native.LIST_MODULES_ALL))
                {
                    error = "无法枚举进程模块（权限不足）";
                    return false;
                }

                List<string> list = new List<string>(modules.Length);
                StringBuilder buffer = new StringBuilder(1024);
                for (int i = 0; i < modules.Length; i++)
                {
                    buffer.Length = 0;
                    if (Native.GetModuleFileNameEx(handle, modules[i], buffer, buffer.Capacity) > 0)
                    {
                        string file = Path.GetFileName(buffer.ToString());
                        if (!string.IsNullOrEmpty(file)) list.Add(file);
                    }
                }

                names = list.ToArray();
                return true;
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }

        private static string GetProcessPath(Process process)
        {
            try { return process.MainModule.FileName; }
            catch { }

            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
            if (handle == IntPtr.Zero) return "";
            try
            {
                StringBuilder buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (Native.QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    return buffer.ToString();
                }
                return "";
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }

        private static string GetBitness(int pid)
        {
            if (!Environment.Is64BitOperatingSystem) return "32 位";
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return "未知";
            try
            {
                bool wow64;
                if (Native.IsWow64Process(handle, out wow64)) return wow64 ? "32 位" : "64 位";
                return "未知";
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }
    }

    internal static class CliScan
    {
        public static void Run()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("GameApiDetector 图形 API 扫描结果  " +
                              DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("PID\t进程\t架构\t图形 API\t关键模块\t读取状态\t路径\t游戏\t平台");

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (Exception ex)
            {
                report.AppendLine("枚举进程失败：" + ex.Message);
                processes = new Process[0];
            }

            foreach (Process process in processes)
            {
                try
                {
                    ProcEntry entry = GfxScanner.Inspect(process);
                    if (entry.ModuleCount == 0 && entry.Error.Length == 0) continue;
                    report.AppendLine(string.Join("\t", new string[]
                    {
                        entry.Pid.ToString(),
                        entry.Name,
                        entry.Arch,
                        entry.Api,
                        entry.KeyModules,
                        entry.Error,
                        entry.Path,
                        GameColumns.GameText(entry),
                        GameColumns.PlatformText(entry)
                    }));
                }
                catch (Exception ex)
                {
                    report.AppendLine(string.Join("\t", new string[]
                    {
                        process.Id.ToString(), "?", "未知", "无法读取", "", ex.Message, "", "", ""
                    }));
                }
                finally
                {
                    process.Dispose();
                }
            }

            string file = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Application.ExecutablePath), "scan_result.txt");
            File.WriteAllText(file, report.ToString(), new UTF8Encoding(true));
        }
    }

    internal sealed class MainForm : Form
    {
        private ListView list;
        private TextBox txtSearch;
        private TextBox txtDetail;
        private Button btnScan;
        private Button btnVulkan;
        private Button btnDxvk;
        private Button btnLocal;
        private Button btnLaunchGame;
        private Label lblStatus;
        private Timer watchTimer;
        private BackgroundWorker worker;

        private readonly Dictionary<int, ProcEntry> cache = new Dictionary<int, ProcEntry>();
        private readonly Dictionary<int, ListViewItem> rows = new Dictionary<int, ListViewItem>();
        private readonly Font apiFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        private readonly HashSet<int> preexistingPids = new HashSet<int>();
        private bool scanning;
        private int scannedCount;

        private bool watching;
        private string watchExeName = "";
        private string watchExePath = "";
        private string watchDirectory = "";
        private DateTime watchStart;
        private DateTime watchUntil;

        public MainForm()
        {
            Text = "游戏图形 API 检测器";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 700);
            MinimumSize = new Size(900, 520);
            Font = new Font("Microsoft YaHei UI", 9F);
            UiTheme.ApplyIcon(this);
            BuildUi();
            Shown += delegate { StartScan(); };
            FormClosing += delegate
            {
                watchTimer.Stop();
                if (worker.IsBusy) worker.CancelAsync();
            };
        }

        private void BuildUi()
        {
            BackColor = Color.White;

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 58;
            top.BackColor = Color.White;

            int x = 12;
            btnScan = MakeToolButton("扫描游戏", 96, ref x);
            UiTheme.Primary(btnScan);
            btnScan.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            btnScan.Click += delegate { StartScan(); };

            btnVulkan = MakeToolButton("以 Vulkan 启动", 136, ref x);
            btnVulkan.Enabled = false;
            btnVulkan.Click += delegate { LaunchSelected(); };

            btnDxvk = MakeToolButton("安装 DXVK", 104, ref x);
            btnDxvk.Enabled = false;
            btnDxvk.Click += delegate { InstallDxvk(); };

            x += 6;
            btnLocal = MakeToolButton("本地游戏", 92, ref x);
            btnLocal.Click += delegate { ManageLocalGames(); };

            btnLaunchGame = MakeToolButton("启动游戏", 92, ref x);
            btnLaunchGame.Enabled = false;
            btnLaunchGame.Click += delegate { LaunchGame(); };

            txtSearch = new TextBox();
            txtSearch.Width = 220;
            txtSearch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            txtSearch.Location = new Point(900, 17);
            txtSearch.BorderStyle = BorderStyle.FixedSingle;
            txtSearch.TextChanged += delegate { RebuildList(); };
            txtSearch.HandleCreated += delegate
            {
                Native.SetCueBanner(txtSearch.Handle, "搜索游戏名 / 平台 / API");
            };

            top.Resize += delegate
            {
                txtSearch.Left = top.ClientSize.Width - txtSearch.Width - 12;
            };

            top.Controls.Add(btnScan);
            top.Controls.Add(btnVulkan);
            top.Controls.Add(btnDxvk);
            top.Controls.Add(btnLocal);
            top.Controls.Add(btnLaunchGame);
            top.Controls.Add(txtSearch);

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.GridLines = false;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Color.White;
            list.Font = new Font("Microsoft YaHei UI", 9.5F);
            ImageList rowHeight = new ImageList();
            rowHeight.ImageSize = new Size(1, 26);
            list.SmallImageList = rowHeight;
            list.Columns.Add("游戏", 190, HorizontalAlignment.Left);
            list.Columns.Add("平台", 78, HorizontalAlignment.Left);
            list.Columns.Add("运行状态", 76, HorizontalAlignment.Left);
            list.Columns.Add("图形 API", 170, HorizontalAlignment.Left);
            list.Columns.Add("DXVK", 150, HorizontalAlignment.Left);
            list.Columns.Add("安装目录", 270, HorizontalAlignment.Left);
            list.Columns.Add("可执行文件", 240, HorizontalAlignment.Left);
            list.SelectedIndexChanged += delegate { ShowSelectedDetail(); };
            list.DoubleClick += delegate
            {
                if (list.SelectedItems.Count > 0)
                {
                    ProcEntry entry = (ProcEntry)list.SelectedItems[0].Tag;
                    RescanEntry(entry);
                }
            };

            txtDetail = new TextBox();
            txtDetail.Dock = DockStyle.Fill;
            txtDetail.Multiline = true;
            txtDetail.ReadOnly = true;
            txtDetail.ScrollBars = ScrollBars.Vertical;
            txtDetail.BorderStyle = BorderStyle.None;
            txtDetail.Font = new Font("Consolas", 9.5F);
            txtDetail.BackColor = UiTheme.Surface;
            ShowDefaultDetail();

            Panel detailPanel = new Panel();
            detailPanel.Dock = DockStyle.Fill;
            detailPanel.BackColor = UiTheme.Surface;
            detailPanel.Padding = new Padding(12, 8, 12, 8);
            detailPanel.Controls.Add(txtDetail);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterWidth = 6;
            split.BackColor = UiTheme.Border;
            split.Panel1.BackColor = Color.White;
            split.Panel1.Controls.Add(list);
            split.Panel2.BackColor = UiTheme.Surface;
            split.Panel2.Controls.Add(detailPanel);

            lblStatus = new Label();
            lblStatus.Dock = DockStyle.Bottom;
            lblStatus.Height = 30;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            lblStatus.Padding = new Padding(14, 0, 14, 0);
            lblStatus.BackColor = Color.White;
            lblStatus.ForeColor = UiTheme.TextMuted;
            lblStatus.Text = "正在准备…";

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(lblStatus);

            Shown += delegate
            {
                try { split.SplitterDistance = 380; }
                catch { }
            };

            watchTimer = new Timer();
            watchTimer.Interval = 2000;
            watchTimer.Tick += delegate { WatchTick(); };

            worker = new BackgroundWorker();
            worker.WorkerReportsProgress = true;
            worker.WorkerSupportsCancellation = true;
            worker.DoWork += WorkerDoWork;
            worker.ProgressChanged += WorkerProgressChanged;
            worker.RunWorkerCompleted += WorkerCompleted;
        }

        private Button MakeToolButton(string text, int width, ref int x)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = width;
            button.Height = 30;
            button.Location = new Point(x, 14);
            UiTheme.Secondary(button);
            x += width + 8;
            return button;
        }

        private void StartScan()
        {
            if (scanning) return;
            scanning = true;
            scannedCount = 0;
            cache.Clear();
            rows.Clear();
            list.Items.Clear();
            txtDetail.Text = "正在扫描本地游戏库，请稍候…";
            btnScan.Enabled = false;
            UpdateStatus("正在扫描本地游戏");
            worker.RunWorkerAsync();
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bg = (BackgroundWorker)sender;

            List<GameInfo> games = new List<GameInfo>();
            try { games = GameLibrary.All; }
            catch { }

            List<Process> processes = new List<Process>();
            Dictionary<string, List<Process>> byName =
                new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Process[] all = Process.GetProcesses();
                for (int i = 0; i < all.Length; i++)
                {
                    Process process = all[i];
                    processes.Add(process);
                    string name = "";
                    try { name = process.ProcessName; }
                    catch { continue; }

                    List<Process> bucket;
                    if (!byName.TryGetValue(name, out bucket))
                    {
                        bucket = new List<Process>();
                        byName[name] = bucket;
                    }
                    bucket.Add(process);
                }
            }
            catch { }

            try
            {
                int index = 0;
                for (int i = 0; i < games.Count; i++)
                {
                    if (bg.CancellationPending) break;

                    GameInfo game = games[i];
                    if (game.Root.Length == 0 || !Directory.Exists(game.Root)) continue;

                    ProcEntry entry = new ProcEntry();
                    entry.IsGame = true;
                    entry.GameKnown = true;
                    entry.Platform = game.Platform;
                    entry.Path = GameExe.Locate(game);

                    if (entry.Path.Length > 0)
                    {
                        entry.Name = Path.GetFileNameWithoutExtension(entry.Path);
                        entry.GameName = game.Name.Length > 0 ? game.Name : entry.Name;
                        entry.Arch = "—";
                        entry.Api = "未运行";
                        try { entry.Dxvk = DxvkProbe.Probe(entry.Path); }
                        catch { entry.Dxvk = ""; }
                    }
                    else
                    {
                        entry.GameName = game.Name.Length > 0
                            ? game.Name
                            : Path.GetFileNameWithoutExtension(game.Root.TrimEnd('\\'));
                        entry.Name = entry.GameName;
                        entry.Arch = "—";
                        entry.Api = "缺少可执行文件";
                        entry.Error = "未能在安装目录中找到游戏主程序，可点击「本地游戏」手动添加";
                    }

                    if (entry.Path.Length == 0 && entry.Platform != "本地") continue;

                    entry.Pid = -1 - index;
                    index++;

                    if (entry.Path.Length > 0 && entry.Name.Length > 0)
                    {
                        List<Process> bucket;
                        if (byName.TryGetValue(entry.Name, out bucket))
                        {
                            for (int j = 0; j < bucket.Count; j++)
                            {
                                Process process = bucket[j];
                                string path = "";
                                try { path = GfxScanner.ReadPath(process); }
                                catch { path = ""; }
                                if (path.Length > 0 &&
                                    !string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                try
                                {
                                    ProcEntry live = GfxScanner.Inspect(process);
                                    entry.RunPid = process.Id;
                                    MergeLive(entry, live);
                                }
                                catch { }
                                break;
                            }
                        }
                    }

                    bg.ReportProgress(0, entry);
                }
            }
            finally
            {
                for (int i = 0; i < processes.Count; i++)
                {
                    try { processes[i].Dispose(); }
                    catch { }
                }
            }
        }

        private static void MergeLive(ProcEntry entry, ProcEntry live)
        {
            entry.Api = live.Api;
            entry.Arch = live.Arch;
            entry.Title = live.Title;
            entry.KeyModules = live.KeyModules;
            entry.GfxModules = live.GfxModules;
            entry.ModuleCount = live.ModuleCount;
            entry.HasWindow = live.HasWindow;
            entry.Error = live.Error;
            if (live.Dxvk.Length > 0) entry.Dxvk = live.Dxvk;
            if (live.Path.Length > 0) entry.Path = live.Path;
        }

        private static void ResetLive(ProcEntry entry)
        {
            entry.RunPid = 0;
            entry.Api = "未运行";
            entry.Arch = "—";
            entry.Title = "";
            entry.KeyModules = "";
            entry.GfxModules = new string[0];
            entry.ModuleCount = 0;
            entry.HasWindow = false;
            entry.Error = "";
        }

        private void WorkerProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            ProcEntry entry = e.UserState as ProcEntry;
            if (entry == null) return;
            scannedCount++;
            cache[entry.Pid] = entry;
            if (ShowEntry(entry)) AddOrUpdateRow(entry);
            if (scannedCount % 10 == 0) UpdateStatus("正在扫描本地游戏");
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            scanning = false;
            btnScan.Enabled = true;

            if (!e.Cancelled)
            {
                int added = 0;
                foreach (ProcEntry entry in cache.Values)
                {
                    if (entry.Path.Length == 0 || !File.Exists(entry.Path)) continue;
                    if (entry.Platform.Length > 0 && entry.Platform != "本地") continue;
                    string name = entry.GameName.Length > 0 ? entry.GameName : entry.Name;
                    if (SavedGames.Add(entry.Path, name, "本地")) added++;
                }
                if (added > 0) GameLibrary.Reload();
            }

            RebuildList();
            ShowDefaultDetail();
            UpdateStatus(e.Cancelled ? "扫描已取消" : "扫描完成");
        }

        private void ShowDefaultDetail()
        {
            txtDetail.Text = "列表直接来自本地游戏库：Steam / Epic / GOG / EA / Ubisoft / Battle.net / Xbox，" +
                             "以及「本地游戏」中手动添加的第三方游戏。\r\n" +
                             "扫描只按游戏主程序路径匹配是否正在运行，不会把其他系统进程当作游戏展示。\r\n\r\n" +
                             "查看图形 API：先启动游戏，再点击「扫描游戏」；" +
                             "运行中的游戏会显示实际加载的图形 API，未运行的显示「未运行」。\r\n" +
                             "判定方式：读取该游戏进程已加载的图形运行库。\r\n" +
                             "  Vulkan      -> vulkan-1.dll\r\n" +
                             "  DirectX 12  -> d3d12.dll / d3d12core.dll\r\n" +
                             "  DirectX 11  -> d3d11.dll\r\n" +
                             "  OpenGL      -> opengl32.dll\r\n" +
                             "  DirectX 9   -> d3d9.dll\r\n\r\n" +
                             "若游戏已在运行却显示「未运行」，请确认主程序路径是否被正确识别；" +
                             "可在「本地游戏」中手动添加游戏 exe。\r\n" +
                             "若游戏以管理员身份运行导致显示「无法读取」，请右键本程序选择「以管理员身份运行」。\r\n\r\n" +
                             "想改用 Vulkan：选中游戏，点击顶部「以 Vulkan 启动」，用 -vulkan 等启动参数重开游戏并自动检测。\r\n" +
                             "该方式不修改游戏文件；仅当游戏本身支持 Vulkan 时有效。\r\n\r\n" +
                             "游戏原生不支持 Vulkan：选中游戏后点击「安装 DXVK」，把 d3d8/9/10core/11.dll + dxgi.dll 复制到游戏目录（自动备份原文件）。\r\n" +
                             "仅适用于 DX8/9/10/11 游戏，DX12 无效；联机反作弊游戏请勿使用。";
        }

        private void RescanEntry(ProcEntry entry)
        {
            if (entry == null) return;

            int pid = entry.RunPid;
            if (pid <= 0) pid = FindRunningPid(entry.Path, entry.Name);
            if (pid <= 0)
            {
                ResetLive(entry);
                AddOrUpdateRow(entry);
                ShowEntryDetail(entry);
                UpdateStatus("该游戏当前未运行，可点击「启动游戏」后重新扫描");
                return;
            }

            try
            {
                Process process = Process.GetProcessById(pid);
                try
                {
                    ProcEntry live = GfxScanner.Inspect(process);
                    entry.RunPid = pid;
                    MergeLive(entry, live);
                    AddOrUpdateRow(entry);
                    ShowEntryDetail(entry);
                    UpdateStatus("已重新检测 " + entry.GameName + "（" + entry.Api + "）");
                }
                finally
                {
                    process.Dispose();
                }
            }
            catch (Exception ex)
            {
                ResetLive(entry);
                AddOrUpdateRow(entry);
                ShowEntryDetail(entry);
                UpdateStatus("检测失败：" + ex.Message);
            }
        }

        private static int FindRunningPid(string exePath, string exeName)
        {
            if (exeName == null || exeName.Length == 0) return 0;
            Process[] processes;
            try { processes = Process.GetProcessesByName(exeName); }
            catch { return 0; }

            try
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    string path = "";
                    try { path = GfxScanner.ReadPath(processes[i]); }
                    catch { path = ""; }
                    if (exePath.Length > 0 && path.Length > 0 &&
                        !string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    return processes[i].Id;
                }
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try { processes[i].Dispose(); }
                    catch { }
                }
            }
            return 0;
        }

        private void LaunchSelected()
        {
            if (list.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先在列表中选择要启动的游戏。", "以 Vulkan 启动",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ProcEntry entry = (ProcEntry)list.SelectedItems[0].Tag;
            if (entry.Path.Length == 0 || !File.Exists(entry.Path))
            {
                MessageBox.Show("未找到该游戏的可执行文件，请先在「本地游戏」中手动添加。",
                    "以 Vulkan 启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (LaunchDialog dialog = new LaunchDialog(entry))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                if (dialog.KillExisting)
                {
                    try
                    {
                        int oldPid = entry.RunPid;
                        if (oldPid <= 0) oldPid = FindRunningPid(entry.Path, entry.Name);
                        if (oldPid > 0)
                        {
                            Process old = Process.GetProcessById(oldPid);
                            old.Kill();
                            old.WaitForExit(3000);
                            old.Dispose();
                        }
                        ResetLive(entry);
                        AddOrUpdateRow(entry);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("结束原进程失败：" + ex.Message +
                            "\r\n请手动关闭游戏后再试。", "以 Vulkan 启动",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                preexistingPids.Clear();
                Process[] existing = Process.GetProcesses();
                foreach (Process process in existing)
                {
                    preexistingPids.Add(process.Id);
                    process.Dispose();
                }

                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = dialog.ExePath;
                info.Arguments = dialog.Arguments;
                info.WorkingDirectory = dialog.WorkingDirectory.Length > 0
                    ? dialog.WorkingDirectory
                    : Path.GetDirectoryName(dialog.ExePath);
                info.UseShellExecute = true;

                try
                {
                    Process.Start(info);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("启动失败：" + ex.Message +
                        "\r\n可尝试右键以管理员身份运行本程序。", "以 Vulkan 启动",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (dialog.AutoDetect)
                {
                    watchExeName = Path.GetFileNameWithoutExtension(dialog.ExePath);
                    watchExePath = dialog.ExePath;
                    watchDirectory = Path.GetDirectoryName(dialog.ExePath);
                    watchStart = DateTime.Now;
                    watchUntil = watchStart.AddSeconds(30);
                    watching = true;
                    watchTimer.Start();
                    UpdateStatus("已启动 " + watchExeName + "，等待 Vulkan 生效…");
                }
                else
                {
                    UpdateStatus("已启动 " + Path.GetFileName(dialog.ExePath));
                }
            }
        }

        private void LaunchGame()
        {
            if (list.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先在列表中选择要启动的游戏。", "启动游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ProcEntry entry = (ProcEntry)list.SelectedItems[0].Tag;
            if (entry.Path.Length == 0 || !File.Exists(entry.Path))
            {
                MessageBox.Show("未找到该游戏的可执行文件，请先在「本地游戏」中手动添加。",
                    "启动游戏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (entry.RunPid > 0)
            {
                MessageBox.Show("该游戏已经在运行。若要重新检测，请点击「扫描游戏」。",
                    "启动游戏", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = entry.Path;
                info.WorkingDirectory = Path.GetDirectoryName(entry.Path);
                info.UseShellExecute = true;
                Process.Start(info);
                UpdateStatus("已启动 " + entry.GameName + "，进入游戏后点击「扫描游戏」检测图形 API");
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message, "启动游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void InstallDxvk()
        {
            if (list.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先在列表中选择要安装 DXVK 的游戏。", "安装 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ProcEntry entry = (ProcEntry)list.SelectedItems[0].Tag;
            if (entry.Path.Length == 0 || !File.Exists(entry.Path))
            {
                MessageBox.Show("无法获取该进程的可执行文件路径，请以管理员身份运行本程序后重试。",
                    "安装 DXVK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (DxvkProbe.IsInstalled(entry.Path))
            {
                RestoreDxvk(entry);
                return;
            }

            using (DxvkDialog dialog = new DxvkDialog(entry))
            {
                dialog.ShowDialog(this);
            }
        }

        private void RestoreDxvk(ProcEntry entry)
        {
            string dir = DxvkProbe.SafeDirectory(entry.Path);
            if (dir.Length == 0)
            {
                MessageBox.Show("无法确定游戏目录。", "还原 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (IsProcessRunning(entry.Name))
            {
                MessageBox.Show("游戏仍在运行，请先关闭游戏后再还原。", "还原 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show("将从以下目录移除 DXVK DLL，并还原备份的原文件：\r\n" + dir +
                    "\r\n\r\n继续？", "还原 DXVK",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            List<string> errors;
            int restored = DxvkProbe.Restore(dir, out errors);

            StringBuilder text = new StringBuilder();
            text.Append(restored > 0 ? "已还原 " + restored + " 个文件，游戏将回到原生 Direct3D。"
                                     : "未找到可还原的 DXVK 文件。");
            if (errors.Count > 0) text.Append("\r\n失败：" + string.Join("；", errors.ToArray()));
            MessageBox.Show(text.ToString(), "还原 DXVK", MessageBoxButtons.OK, MessageBoxIcon.Information);

            try { entry.Dxvk = DxvkProbe.Probe(entry.Path); }
            catch { entry.Dxvk = ""; }
            RescanEntry(entry);
        }

        private static bool IsProcessRunning(string name)
        {
            if (name == null || name.Length == 0) return false;
            Process[] processes = Process.GetProcessesByName(name);
            try
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try { if (!processes[i].HasExited) return true; }
                    catch { }
                }
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try { processes[i].Dispose(); }
                    catch { }
                }
            }
            return false;
        }

        private void WatchTick()
        {
            if (!watching) return;

            if (DateTime.Now > watchUntil)
            {
                watching = false;
                watchTimer.Stop();
                UpdateStatus("30 秒内未检测到 Vulkan；游戏可能不支持该参数，或需要从平台启动");
                return;
            }

            Process[] processes = Process.GetProcesses();
            foreach (Process process in processes)
            {
                try
                {
                    if (preexistingPids.Contains(process.Id)) continue;

                    DateTime started;
                    try { started = process.StartTime; }
                    catch { continue; }
                    if (started < watchStart.AddSeconds(-3)) continue;

                    ProcEntry entry = GfxScanner.Inspect(process);

                    bool sameName = string.Equals(process.ProcessName, watchExeName,
                        StringComparison.OrdinalIgnoreCase);
                    bool sameDir = entry.Path.Length > 0 && watchDirectory.Length > 0 &&
                                   entry.Path.StartsWith(watchDirectory + Path.DirectorySeparatorChar,
                                       StringComparison.OrdinalIgnoreCase);

                    if (entry.HasGraphics && (sameName || sameDir))
                    {
                        ProcEntry game = FindCachedEntry(entry.Path);
                        if (game != null)
                        {
                            game.RunPid = process.Id;
                            MergeLive(game, entry);
                            AddOrUpdateRow(game);
                            SelectRow(game.Pid);
                        }

                        if (entry.Api.StartsWith("Vulkan"))
                        {
                            watching = false;
                            watchTimer.Stop();
                            UpdateStatus("Vulkan 已生效：" + entry.Name + "（" + entry.KeyModules + "）");
                            return;
                        }
                        UpdateStatus("已启动 " + entry.Name + "，当前 API：" + entry.Api + "，继续等待…");
                    }
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private ProcEntry FindCachedEntry(string exePath)
        {
            if (exePath == null || exePath.Length == 0) return null;
            foreach (ProcEntry entry in cache.Values)
            {
                if (entry.Path.Length > 0 &&
                    string.Equals(entry.Path, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }

        private void SelectRow(int pid)
        {
            ListViewItem item;
            if (rows.TryGetValue(pid, out item))
            {
                item.Selected = true;
                item.Focused = true;
                list.EnsureVisible(item.Index);
                ShowEntryDetail((ProcEntry)item.Tag);
            }
        }

        private bool ShowEntry(ProcEntry entry)
        {
            if (!entry.IsGame) return false;

            string query = txtSearch.Text.Trim();
            if (query.Length == 0) return true;

            string haystack = GameColumns.GameText(entry) + " " + entry.Platform + " " +
                              entry.Api + " " + entry.Path + " " + entry.Name;
            return haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AddOrUpdateRow(ProcEntry entry)
        {
            string status = entry.RunPid > 0 ? "运行中" : (entry.Path.Length > 0 ? "未运行" : "缺少文件");
            string dir = "";
            try { if (entry.Path.Length > 0) dir = Path.GetDirectoryName(entry.Path); }
            catch { dir = ""; }

            ListViewItem item;
            if (!rows.TryGetValue(entry.Pid, out item))
            {
                item = new ListViewItem(GameColumns.GameText(entry));
                item.Tag = entry;
                item.SubItems.Add(GameColumns.PlatformText(entry));
                item.SubItems.Add(status);
                item.SubItems.Add(entry.Api);
                item.SubItems.Add(entry.Dxvk);
                item.SubItems.Add(dir);
                item.SubItems.Add(entry.Path);
                item.UseItemStyleForSubItems = false;
                rows[entry.Pid] = item;
                list.Items.Add(item);
            }
            else
            {
                item.Tag = entry;
                item.Text = GameColumns.GameText(entry);
                item.SubItems[1].Text = GameColumns.PlatformText(entry);
                item.SubItems[2].Text = status;
                item.SubItems[3].Text = entry.Api;
                item.SubItems[4].Text = entry.Dxvk;
                item.SubItems[5].Text = dir;
                item.SubItems[6].Text = entry.Path;
            }

            Color apiColor = GetApiColor(entry.Api);
            item.ForeColor = SystemColors.ControlText;
            for (int i = 0; i < item.SubItems.Count; i++)
            {
                item.SubItems[i].ForeColor = SystemColors.ControlText;
            }
            item.SubItems[3].ForeColor = apiColor;
            item.SubItems[3].Font = apiFont;
            if (entry.Error.Length > 0)
            {
                item.SubItems[3].ForeColor = Color.Firebrick;
                item.ToolTipText = entry.Error;
            }
            if (entry.Dxvk.Length > 0) item.SubItems[4].ForeColor = Color.SeaGreen;
            if (entry.RunPid > 0) item.SubItems[2].ForeColor = Color.SeaGreen;

            if (item.Selected) ShowEntryDetail(entry);
        }

        private void RemoveRow(int pid)
        {
            ListViewItem item;
            if (rows.TryGetValue(pid, out item))
            {
                list.Items.Remove(item);
                rows.Remove(pid);
            }
        }

        private void RebuildList()
        {
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                rows.Clear();
                List<ProcEntry> visible = new List<ProcEntry>();
                foreach (ProcEntry entry in cache.Values)
                {
                    if (ShowEntry(entry)) visible.Add(entry);
                }
                visible.Sort(CompareEntries);
                foreach (ProcEntry entry in visible) AddOrUpdateRow(entry);
            }
            finally
            {
                list.EndUpdate();
            }
            UpdateStatus("就绪");
        }

        private static int CompareEntries(ProcEntry a, ProcEntry b)
        {
            if (a.RunPid > 0 != b.RunPid > 0) return a.RunPid > 0 ? -1 : 1;

            int byGame = string.Compare(
                a.GameName.Length > 0 ? a.GameName : a.Name,
                b.GameName.Length > 0 ? b.GameName : b.Name,
                StringComparison.OrdinalIgnoreCase);
            if (byGame != 0) return byGame;

            return string.Compare(a.Platform, b.Platform, StringComparison.OrdinalIgnoreCase);
        }

        private static Color GetApiColor(string api)
        {
            if (api.StartsWith("Vulkan")) return Color.FromArgb(113, 62, 199);
            if (api.StartsWith("DirectX 12")) return Color.FromArgb(25, 105, 210);
            if (api.StartsWith("DirectX 11")) return Color.FromArgb(20, 140, 80);
            if (api.StartsWith("OpenGL")) return Color.FromArgb(196, 102, 0);
            if (api.StartsWith("DirectX")) return Color.FromArgb(120, 120, 120);
            if (api == "无法读取" || api == "缺少可执行文件") return Color.Firebrick;
            if (api == "未运行") return Color.DimGray;
            return Color.DimGray;
        }

        private void ShowSelectedDetail()
        {
            if (list.SelectedItems.Count == 0)
            {
                btnVulkan.Enabled = false;
                btnDxvk.Enabled = false;
                btnLaunchGame.Enabled = false;
                btnDxvk.Text = "安装 DXVK";
                txtDetail.Text = "选中一个游戏查看详情。\r\n\r\n" +
                                 "列表来自本地游戏库（平台 + 已保存的本地游戏），不再以系统进程作为列表来源。\r\n" +
                                 "判定方式：游戏运行后，读取其进程已加载的图形运行库。\r\n" +
                                 "Vulkan = vulkan-1.dll，DX12 = d3d12.dll，DX11 = d3d11.dll，" +
                                 "OpenGL = opengl32.dll，DX9 = d3d9.dll。";
                return;
            }
            ProcEntry entry = (ProcEntry)list.SelectedItems[0].Tag;
            bool hasExe = entry.Path.Length > 0 && File.Exists(entry.Path);
            btnVulkan.Enabled = hasExe;
            btnDxvk.Enabled = hasExe;
            btnLaunchGame.Enabled = hasExe;
            btnDxvk.Text = entry.Dxvk.Length > 0 ? "还原 DXVK" : "安装 DXVK";
            ShowEntryDetail(entry);
        }

        private void ManageLocalGames()
        {
            bool changed;
            using (LocalGamesDialog dialog = new LocalGamesDialog())
            {
                dialog.ShowDialog(this);
                changed = dialog.Changed;
            }
            if (changed)
            {
                GameLibrary.Reload();
                StartScan();
            }
        }

        private void ShowEntryDetail(ProcEntry entry)
        {
            string status = entry.RunPid > 0
                ? "运行中（PID " + entry.RunPid + "）"
                : (entry.Path.Length > 0 ? "未运行" : "缺少可执行文件");

            StringBuilder text = new StringBuilder();
            text.AppendLine("游戏：" + entry.GameName + "（平台：" +
                            (entry.Platform.Length > 0 ? entry.Platform : "未知") + "）");
            text.AppendLine("状态：" + status);
            text.AppendLine("主程序：" + (entry.Path.Length > 0 ? entry.Path : "（未找到）"));
            if (entry.RunPid > 0)
            {
                text.AppendLine("进程：" + entry.Name + "    架构：" + entry.Arch);
                text.AppendLine("窗口：" + (entry.Title.Length > 0 ? entry.Title : "（无主窗口）"));
            }
            text.AppendLine("检测 API：" + entry.Api);
            text.AppendLine("DXVK 状态：" + (entry.Dxvk.Length > 0 ? entry.Dxvk + "（Vulkan 已启用）" : "未安装"));
            if (entry.Error.Length > 0) text.AppendLine("说明：" + entry.Error);
            text.AppendLine();
            if (entry.RunPid > 0)
            {
                text.AppendLine("图形相关模块：");
                if (entry.GfxModules.Length == 0)
                {
                    text.AppendLine("  无");
                }
                else
                {
                    foreach (string module in entry.GfxModules) text.AppendLine("  " + module);
                }
                text.AppendLine();
                text.AppendLine("模块总数：" + entry.ModuleCount);
                text.AppendLine();
                text.AppendLine("说明：按已加载模块判定，优先级 Vulkan > DX12 > DX11 > OpenGL > DX10 > DX9；");
                text.AppendLine("      同时出现多个 API 时，括号内会标注副 API。");
            }
            else
            {
                text.AppendLine("图形 API 需要游戏运行后才能检测。");
                text.AppendLine("请点击顶部「启动游戏」，进入游戏后点击「扫描游戏」。");
            }
            txtDetail.Text = text.ToString();
        }

        private void UpdateStatus(string prefix)
        {
            int games = 0;
            int running = 0;
            foreach (ProcEntry entry in cache.Values)
            {
                games++;
                if (entry.RunPid > 0) running++;
            }
            lblStatus.Text = prefix + "  |  本地游戏 " + games + " 个（运行中 " + running +
                             "）  |  本地已保存 " + SavedGames.Count() +
                             " 个地址  |  双击游戏可重新检测";
        }
    }


    internal sealed class LocalGamesDialog : Form
    {
        public bool Changed;

        private ListView lsv;
        private Label lblState;

        public LocalGamesDialog()
        {
            Text = "本地游戏（第三方 / 独立游戏）";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 420);
            MinimumSize = new Size(620, 340);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            UiTheme.ApplyIcon(this);
            BackColor = Color.White;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 52;
            bottom.Width = ClientSize.Width;

            Button btnAdd = new Button();
            btnAdd.Text = "添加游戏…";
            btnAdd.Location = new Point(10, 12);
            btnAdd.Width = 100;
            btnAdd.Height = 28;
            UiTheme.Primary(btnAdd);
            btnAdd.Click += delegate { AddGame(); };

            Button btnRemove = new Button();
            btnRemove.Text = "移除选中";
            btnRemove.Location = new Point(116, 12);
            btnRemove.Width = 90;
            btnRemove.Height = 28;
            UiTheme.Secondary(btnRemove);
            btnRemove.Click += delegate { RemoveGame(); };

            Button btnLaunch = new Button();
            btnLaunch.Text = "启动选中";
            btnLaunch.Location = new Point(212, 12);
            btnLaunch.Width = 90;
            btnLaunch.Height = 28;
            UiTheme.Secondary(btnLaunch);
            btnLaunch.Click += delegate { LaunchGame(); };

            Button btnClose = new Button();
            btnClose.Text = "关闭";
            btnClose.Location = new Point(308, 12);
            btnClose.Width = 80;
            btnClose.Height = 28;
            UiTheme.Secondary(btnClose);
            btnClose.DialogResult = DialogResult.Cancel;

            lblState = new Label();
            lblState.AutoSize = true;
            lblState.Location = new Point(404, 18);

            bottom.Controls.Add(btnAdd);
            bottom.Controls.Add(btnRemove);
            bottom.Controls.Add(btnLaunch);
            bottom.Controls.Add(btnClose);
            bottom.Controls.Add(lblState);

            lsv = new ListView();
            lsv.Dock = DockStyle.Fill;
            lsv.View = View.Details;
            lsv.FullRowSelect = true;
            lsv.MultiSelect = false;
            lsv.HideSelection = false;
            lsv.BorderStyle = BorderStyle.None;
            lsv.Font = new Font("Microsoft YaHei UI", 9.5F);
            ImageList rowHeight = new ImageList();
            rowHeight.ImageSize = new Size(1, 24);
            lsv.SmallImageList = rowHeight;
            lsv.Columns.Add("游戏", 180);
            lsv.Columns.Add("平台", 90);
            lsv.Columns.Add("可执行文件地址", 460);
            lsv.DoubleClick += delegate { LaunchGame(); };

            Controls.Add(lsv);
            Controls.Add(bottom);
            CancelButton = btnClose;

            LoadList();
        }

        private void LoadList()
        {
            List<GameInfo> list = SavedGames.Load();
            lsv.BeginUpdate();
            try
            {
                lsv.Items.Clear();
                for (int i = 0; i < list.Count; i++)
                {
                    GameInfo game = list[i];
                    ListViewItem item = new ListViewItem(game.Name);
                    item.SubItems.Add(game.Platform);
                    item.SubItems.Add(game.ExePath);
                    item.Tag = game;
                    lsv.Items.Add(item);
                }
            }
            finally
            {
                lsv.EndUpdate();
            }
            lblState.Text = "已保存 " + list.Count + " 个游戏地址";
        }

        private void AddGame()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择游戏可执行文件";
                dialog.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string exe = dialog.FileName;
                string name = "";
                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                    if (info.FileDescription != null) name = info.FileDescription.Trim();
                }
                catch { }
                if (name.Length == 0) name = Path.GetFileNameWithoutExtension(exe);

                if (SavedGames.Add(exe, name, "本地"))
                {
                    Changed = true;
                    LoadList();
                }
            }
        }

        private void RemoveGame()
        {
            if (lsv.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先选择要移除的游戏。", "本地游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            GameInfo game = (GameInfo)lsv.SelectedItems[0].Tag;
            if (MessageBox.Show("从已保存列表移除以下游戏？\r\n" + game.ExePath +
                    "\r\n\r\n（不会删除游戏文件）", "本地游戏",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            if (SavedGames.Remove(game.ExePath))
            {
                Changed = true;
                LoadList();
            }
        }

        private void LaunchGame()
        {
            if (lsv.SelectedItems.Count == 0) return;
            GameInfo game = (GameInfo)lsv.SelectedItems[0].Tag;
            if (!File.Exists(game.ExePath))
            {
                MessageBox.Show("文件不存在：" + game.ExePath, "本地游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = game.ExePath;
                info.WorkingDirectory = Path.GetDirectoryName(game.ExePath);
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：" + ex.Message, "本地游戏",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class LaunchDialog : Form
    {
        private TextBox txtPath;
        private TextBox txtWorkDir;
        private ComboBox cboPreset;
        private TextBox txtArgs;
        private CheckBox chkKill;
        private CheckBox chkAuto;
        private Button btnCopy;
        private Button btnLaunch;
        private Button btnCancel;

        public string ExePath { get { return txtPath.Text.Trim(); } }
        public string WorkingDirectory { get { return txtWorkDir.Text.Trim(); } }
        public string Arguments { get { return txtArgs.Text.Trim(); } }
        public bool KillExisting { get { return chkKill.Checked; } }
        public bool AutoDetect { get { return chkAuto.Checked; } }

        public LaunchDialog(ProcEntry entry)
        {
            Text = "以 Vulkan 启动 - " + entry.Name;
            ClientSize = new Size(600, 424);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9F);
            UiTheme.ApplyIcon(this);
            BackColor = Color.White;

            Label lblPath = new Label();
            lblPath.Text = "程序：";
            lblPath.AutoSize = true;
            lblPath.Location = new Point(16, 24);
            Controls.Add(lblPath);

            txtPath = new TextBox();
            txtPath.ReadOnly = true;
            txtPath.Location = new Point(110, 21);
            txtPath.Width = 464;
            txtPath.Text = entry.Path;
            Controls.Add(txtPath);

            Label lblDir = new Label();
            lblDir.Text = "工作目录：";
            lblDir.AutoSize = true;
            lblDir.Location = new Point(16, 64);
            Controls.Add(lblDir);

            txtWorkDir = new TextBox();
            txtWorkDir.Location = new Point(110, 61);
            txtWorkDir.Width = 464;
            try { txtWorkDir.Text = Path.GetDirectoryName(entry.Path); }
            catch { txtWorkDir.Text = ""; }
            Controls.Add(txtWorkDir);

            Label lblPreset = new Label();
            lblPreset.Text = "参数预设：";
            lblPreset.AutoSize = true;
            lblPreset.Location = new Point(16, 104);
            Controls.Add(lblPreset);

            cboPreset = new ComboBox();
            cboPreset.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPreset.Location = new Point(110, 101);
            cboPreset.Width = 260;
            cboPreset.Items.Add("-vulkan（通用写法）");
            cboPreset.Items.Add("-force-vulkan（Unity 等引擎）");
            cboPreset.Items.Add("--vulkan（部分 Linux 移植游戏）");
            cboPreset.Items.Add("自定义（手动输入）");
            cboPreset.SelectedIndex = 0;
            cboPreset.SelectedIndexChanged += delegate { ApplyPreset(); };
            Controls.Add(cboPreset);

            Label lblArgs = new Label();
            lblArgs.Text = "启动参数：";
            lblArgs.AutoSize = true;
            lblArgs.Location = new Point(16, 144);
            Controls.Add(lblArgs);

            txtArgs = new TextBox();
            txtArgs.Location = new Point(110, 141);
            txtArgs.Width = 348;
            txtArgs.Text = "-vulkan";
            Controls.Add(txtArgs);

            btnCopy = new Button();
            btnCopy.Text = "复制参数";
            btnCopy.Location = new Point(466, 140);
            btnCopy.Width = 108;
            btnCopy.Height = 26;
            UiTheme.Secondary(btnCopy);
            btnCopy.Click += delegate
            {
                try { Clipboard.SetText(txtArgs.Text); }
                catch { }
            };
            Controls.Add(btnCopy);

            Label lblHint = new Label();
            lblHint.Location = new Point(16, 180);
            lblHint.Size = new Size(558, 118);
            lblHint.ForeColor = Color.DimGray;
            lblHint.Text = "说明：\r\n" +
                           "1. 只有游戏本身支持 Vulkan 时该方法才会生效，例如《赛博朋克2077》《DOOM》《荒野大镖客2》《无主之地3》等。\r\n" +
                           "2. 参数写法因引擎而异，可依次尝试 -vulkan / -force-vulkan / --vulkan。\r\n" +
                           "3. Steam / Epic / Xbox 游戏建议在平台「启动项」中填写参数，本对话框负责直接启动 exe。\r\n" +
                           "4. 本方式不修改任何游戏文件，可随时回退。\r\n" +
                           "5. 若首轮未生效，可清除游戏设置缓存或在平台启动项中指定参数后再试。";
            Controls.Add(lblHint);

            chkKill = new CheckBox();
            chkKill.Text = "结束当前已运行的该进程后再启动";
            chkKill.Location = new Point(110, 304);
            chkKill.AutoSize = true;
            Controls.Add(chkKill);

            chkAuto = new CheckBox();
            chkAuto.Text = "启动后自动检测 30 秒，确认 Vulkan 是否生效";
            chkAuto.Location = new Point(110, 330);
            chkAuto.AutoSize = true;
            chkAuto.Checked = true;
            Controls.Add(chkAuto);

            btnLaunch = new Button();
            btnLaunch.Text = "启动";
            btnLaunch.Location = new Point(378, 372);
            btnLaunch.Width = 96;
            btnLaunch.Height = 30;
            UiTheme.Primary(btnLaunch);
            btnLaunch.Click += delegate { ConfirmLaunch(); };
            Controls.Add(btnLaunch);

            btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(478, 372);
            btnCancel.Width = 96;
            btnCancel.Height = 30;
            UiTheme.Secondary(btnCancel);
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = btnLaunch;
            CancelButton = btnCancel;
        }

        private void ApplyPreset()
        {
            if (cboPreset.SelectedIndex == 0) txtArgs.Text = "-vulkan";
            else if (cboPreset.SelectedIndex == 1) txtArgs.Text = "-force-vulkan";
            else if (cboPreset.SelectedIndex == 2) txtArgs.Text = "--vulkan";
        }

        private void ConfirmLaunch()
        {
            if (ExePath.Length == 0 || !File.Exists(ExePath))
            {
                MessageBox.Show("程序文件不存在，请确认路径是否正确。", "以 Vulkan 启动",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }

    internal sealed class DxvkDialog : Form
    {
        private const string DxvkRoot = @"D:\BaiduNetdiskDownload\不用但是有用\dxvk-3.1.1";
        private const string MarkerName = ".dxvk_by_gad.txt";
        private static readonly string[] DxvkFiles =
        {
            "d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "dxgi.dll"
        };

        private readonly ProcEntry entry;
        private readonly bool is32Bit;
        private TextBox txtGameDir;
        private TextBox txtSrcDir;
        private Button btnBrowse;
        private CheckedListBox lstFiles;
        private Label lblState;
        private Button btnInstall;
        private Button btnRestore;
        private Button btnClose;

        public DxvkDialog(ProcEntry entry)
        {
            this.entry = entry;
            is32Bit = entry.Arch.IndexOf("32", StringComparison.Ordinal) >= 0;

            Text = "安装 DXVK（Direct3D 转 Vulkan） - " + entry.Name;
            ClientSize = new Size(620, 478);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Microsoft YaHei UI", 9F);
            UiTheme.ApplyIcon(this);
            BackColor = Color.White;

            Label lblGame = new Label();
            lblGame.Text = "游戏目录：";
            lblGame.AutoSize = true;
            lblGame.Location = new Point(16, 22);
            Controls.Add(lblGame);

            txtGameDir = new TextBox();
            txtGameDir.ReadOnly = true;
            txtGameDir.Location = new Point(106, 19);
            txtGameDir.Width = 498;
            try { txtGameDir.Text = Path.GetDirectoryName(entry.Path); }
            catch { txtGameDir.Text = ""; }
            Controls.Add(txtGameDir);

            Label lblSrc = new Label();
            lblSrc.Text = "DXVK 目录：";
            lblSrc.AutoSize = true;
            lblSrc.Location = new Point(16, 60);
            Controls.Add(lblSrc);

            txtSrcDir = new TextBox();
            txtSrcDir.Location = new Point(106, 57);
            txtSrcDir.Width = 398;
            txtSrcDir.Text = GetDefaultSource();
            Controls.Add(txtSrcDir);

            btnBrowse = new Button();
            btnBrowse.Text = "浏览…";
            btnBrowse.Location = new Point(510, 56);
            btnBrowse.Width = 94;
            btnBrowse.Height = 26;
            UiTheme.Secondary(btnBrowse);
            btnBrowse.Click += delegate { BrowseSource(); };
            Controls.Add(btnBrowse);

            Label lblFiles = new Label();
            lblFiles.Text = "复制到游戏根目录的文件（DX11 需 d3d11.dll + dxgi.dll，DX9 需 d3d9.dll）：";
            lblFiles.AutoSize = true;
            lblFiles.Location = new Point(16, 96);
            Controls.Add(lblFiles);

            lstFiles = new CheckedListBox();
            lstFiles.Location = new Point(106, 118);
            lstFiles.Size = new Size(498, 108);
            lstFiles.CheckOnClick = true;
            lstFiles.Font = new Font("Microsoft YaHei UI", 9F);
            foreach (string file in DxvkFiles) lstFiles.Items.Add(file, true);
            Controls.Add(lstFiles);

            Label lblWarn = new Label();
            lblWarn.Location = new Point(16, 234);
            lblWarn.Size = new Size(588, 44);
            lblWarn.ForeColor = Color.Firebrick;
            lblWarn.Text = "警告：使用反作弊的联机游戏（EAC / BattlEye / VAC 等）请勿安装，可能被判定为篡改客户端。\r\n" +
                           "DXVK 只能把 DirectX 8/9/10/11 转为 Vulkan，DX12 游戏无效。";
            Controls.Add(lblWarn);

            Label lblInfo = new Label();
            lblInfo.Location = new Point(16, 282);
            lblInfo.Size = new Size(588, 76);
            lblInfo.ForeColor = Color.DimGray;
            lblInfo.Text = "说明：\r\n" +
                           "1. 同名原文件会自动备份为 *.dxvk_bak，可用「恢复原版」还原。\r\n" +
                           "2. 安装前请关闭游戏；安装后重启游戏，再扫描确认是否显示 Vulkan。\r\n" +
                           "3. Steam / Epic 的「验证文件完整性」会覆盖这些 DLL，需要重新安装。\r\n" +
                           "4. 需要显卡驱动支持 Vulkan（GTX 1650 已支持）；32 位游戏请选 x32 目录。";
            Controls.Add(lblInfo);

            lblState = new Label();
            lblState.Location = new Point(16, 360);
            lblState.Size = new Size(588, 36);
            lblState.ForeColor = Color.FromArgb(20, 110, 60);
            Controls.Add(lblState);

            btnInstall = new Button();
            btnInstall.Text = "安装到游戏目录";
            btnInstall.Location = new Point(250, 410);
            btnInstall.Width = 140;
            btnInstall.Height = 32;
            UiTheme.Primary(btnInstall);
            btnInstall.Click += delegate { Install(); };
            Controls.Add(btnInstall);

            btnRestore = new Button();
            btnRestore.Text = "恢复原版";
            btnRestore.Location = new Point(398, 410);
            btnRestore.Width = 110;
            btnRestore.Height = 32;
            UiTheme.Secondary(btnRestore);
            btnRestore.Click += delegate { Restore(); };
            Controls.Add(btnRestore);

            btnClose = new Button();
            btnClose.Text = "关闭";
            btnClose.Location = new Point(516, 410);
            btnClose.Width = 88;
            btnClose.Height = 32;
            UiTheme.Secondary(btnClose);
            btnClose.DialogResult = DialogResult.Cancel;
            Controls.Add(btnClose);

            CancelButton = btnClose;
        }

        private string GetDefaultSource()
        {
            string sub = is32Bit ? "x32" : "x64";
            string path = Path.Combine(DxvkRoot, sub);
            return Directory.Exists(path) ? path : DxvkRoot;
        }

        private void BrowseSource()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择 DXVK 的 " + (is32Bit ? "x32" : "x64") + " 目录";
                if (Directory.Exists(txtSrcDir.Text))
                {
                    dialog.SelectedPath = txtSrcDir.Text;
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    txtSrcDir.Text = dialog.SelectedPath;
                }
            }
        }

        private void Install()
        {
            string gameDir = txtGameDir.Text.Trim();
            string srcDir = txtSrcDir.Text.Trim();
            if (!Directory.Exists(gameDir))
            {
                MessageBox.Show("游戏目录不存在。", "安装 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!Directory.Exists(srcDir))
            {
                MessageBox.Show("DXVK 目录不存在，请重新选择。", "安装 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            List<string> files = new List<string>();
            foreach (object item in lstFiles.CheckedItems)
            {
                files.Add(item.ToString());
            }
            if (files.Count == 0)
            {
                MessageBox.Show("请至少勾选一个要复制的 DLL。", "安装 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool needsDxgi = files.Contains("d3d11.dll") || files.Contains("d3d10core.dll");
            if (needsDxgi && !files.Contains("dxgi.dll"))
            {
                MessageBox.Show("DX10 / DX11 游戏必须同时复制 dxgi.dll，请勾选后再安装。", "安装 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (is32Bit && srcDir.TrimEnd('\\').EndsWith("x64", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("当前游戏是 32 位进程，应使用 x32 目录的 DLL。", "安装 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (IsGameRunning())
            {
                MessageBox.Show("游戏仍在运行，请先关闭游戏后再安装。", "安装 DXVK",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DialogResult confirm = MessageBox.Show(
                "即将向以下目录写入 DLL：\r\n" + gameDir +
                "\r\n\r\n已存在的同名文件会自动备份为 *.dxvk_bak。\r\n" +
                "请确认该游戏没有使用反作弊。\r\n\r\n继续安装？",
                "安装 DXVK", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            Dictionary<string, string> records = ReadMarker(gameDir);
            List<string> installed = new List<string>();
            List<string> errors = new List<string>();

            foreach (string file in files)
            {
                string src = Path.Combine(srcDir, file);
                string dst = Path.Combine(gameDir, file);
                string bak = dst + ".dxvk_bak";
                try
                {
                    if (!File.Exists(src))
                    {
                        errors.Add(file + "：源文件不存在");
                        continue;
                    }
                    bool hadBackup = File.Exists(bak);
                    if (File.Exists(dst) && !hadBackup)
                    {
                        File.Move(dst, bak);
                        hadBackup = true;
                    }
                    File.Copy(src, dst, true);
                    installed.Add(file);
                    records[file] = hadBackup ? "1" : "0";
                }
                catch (Exception ex)
                {
                    errors.Add(file + "：" + ex.Message);
                }
            }

            WriteMarker(gameDir, records);

            StringBuilder text = new StringBuilder();
            text.Append("已安装 " + installed.Count + " 个文件。");
            if (installed.Count > 0)
            {
                text.Append("\r\n请重启游戏，再扫描确认是否显示 Vulkan。");
            }
            if (errors.Count > 0)
            {
                text.Append("\r\n失败：" + string.Join("；", errors.ToArray()));
            }
            lblState.Text = text.ToString();
            MessageBox.Show(text.ToString(), "安装 DXVK",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Restore()
        {
            string gameDir = txtGameDir.Text.Trim();
            if (!Directory.Exists(gameDir))
            {
                MessageBox.Show("游戏目录不存在。", "恢复原版",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string marker = Path.Combine(gameDir, MarkerName);
            if (!File.Exists(marker) && DxvkProbe.InstalledFiles(gameDir).Count == 0)
            {
                MessageBox.Show("游戏目录中未发现 DXVK 文件，无需还原。", "恢复原版",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (IsGameRunning())
            {
                MessageBox.Show("游戏仍在运行，请先关闭游戏后再恢复。", "恢复原版",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show("将删除已安装的 DXVK DLL，并用 *.dxvk_bak 还原原文件。\r\n继续？",
                    "恢复原版", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            List<string> errors;
            int restored = DxvkProbe.Restore(gameDir, out errors);

            string result = "已恢复 " + restored + " 个文件。";
            if (errors.Count > 0) result += "\r\n失败：" + string.Join("；", errors.ToArray());
            lblState.Text = result;
            MessageBox.Show(result, "恢复原版",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private bool IsGameRunning()
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(entry.Path);
                Process[] processes = Process.GetProcessesByName(name);
                try
                {
                    foreach (Process process in processes)
                    {
                        try
                        {
                            if (!process.HasExited) return true;
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
                catch { }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, string> ReadMarker(string gameDir)
        {
            Dictionary<string, string> records = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            string marker = Path.Combine(gameDir, MarkerName);
            if (!File.Exists(marker)) return records;
            try
            {
                foreach (string line in File.ReadAllLines(marker))
                {
                    string[] parts = line.Split('|');
                    if (parts.Length == 2 && parts[0].Length > 0)
                    {
                        records[parts[0]] = parts[1];
                    }
                }
            }
            catch { }
            return records;
        }

        private static void WriteMarker(string gameDir, Dictionary<string, string> records)
        {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> pair in records)
            {
                lines.Add(pair.Key + "|" + pair.Value);
            }
            File.WriteAllLines(Path.Combine(gameDir, MarkerName), lines.ToArray(), Encoding.UTF8);
        }
    }
}
