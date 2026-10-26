using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CodexTokenHud
{
    internal sealed class RuntimeConfiguration
    {
        internal string Folder, DataDirectory, CodexHome, Pythonw;
        internal bool CornerMode;
        internal bool ResetAlerts = true;
        internal string CornerMonitor = "";
        internal double CornerOpacity = 0.78;
        internal string CornerMotion = "system";
        internal string Runtime { get { return Path.Combine(DataDirectory, "runtime"); } }
        internal string Layout { get { return Path.Combine(DataDirectory, "user-layout.json"); } }

        internal static RuntimeConfiguration Load(string folder)
        {
            folder = Path.GetFullPath(folder);
            string customData = Environment.GetEnvironmentVariable("CODEX_TOKEN_HUD_HOME");
            var result = new RuntimeConfiguration { Folder = folder,
                DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(customData) ?
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-token-hud") : customData) };
            var settings = Json.Read(Path.Combine(folder, "settings.json"));
            var userSettings = Json.Read(Path.Combine(result.DataDirectory, "settings.json"));
            if (!File.Exists(Path.Combine(result.DataDirectory, "settings.json")) && string.IsNullOrWhiteSpace(customData))
                userSettings = Json.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTokenHud", "settings.json"));
            foreach (var item in userSettings) settings[item.Key] = item.Value;
            result.CornerMode = !Json.Text(settings, "corner_mode").Equals("False", StringComparison.OrdinalIgnoreCase);
            result.ResetAlerts = !Json.Text(settings, "reset_alerts").Equals("False", StringComparison.OrdinalIgnoreCase);
            result.CornerMonitor = Json.Text(settings, "corner_monitor");
            result.CornerOpacity = Math.Max(0.45, Math.Min(1.0, Json.Number(settings, "corner_opacity") ?? 0.78));
            string motion = Json.Text(settings, "corner_motion").ToLowerInvariant();
            result.CornerMotion = motion == "on" || motion == "off" ? motion : "system";
            string home = Json.Text(settings, "codex_home");
            if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            result.CodexHome = Path.GetFullPath(Environment.ExpandEnvironmentVariables(home));
            string python = Json.Text(settings, "pythonw");
            if (!string.IsNullOrWhiteSpace(python))
            {
                python = Environment.ExpandEnvironmentVariables(python);
                result.Pythonw = Path.GetFullPath(Path.IsPathRooted(python) ? python : Path.Combine(folder, python));
            }
            else
            {
                var candidates = new List<string> { Path.Combine(folder, "python", "pythonw.exe") };
                foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                    if (!string.IsNullOrWhiteSpace(path))
                        try { candidates.Add(Path.Combine(path.Trim('"'), "pythonw.exe")); } catch (ArgumentException) { }
                result.Pythonw = candidates.Find(File.Exists);
            }
            return result;
        }

        internal void PrepareData()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(Runtime);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_TOKEN_HUD_HOME")) &&
                DataDirectory == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-token-hud"))
            {
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTokenHud");
                foreach (string name in new[] { "settings.json", "user-layout.json", "runtime\\manual-binding.json",
                    "runtime\\pinned-layout.json", "runtime\\range-result.json" })
                {
                    string source = Path.Combine(legacy, name), destination = Path.Combine(DataDirectory, name);
                    if (File.Exists(source) && !File.Exists(destination))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        try { File.Copy(source, destination, false); } catch (IOException) { if (!File.Exists(destination)) throw; }
                    }
                }
            }
            // Preserve existing installations' layout once, without publishing or moving their files.
            string oldLayout = Path.Combine(Folder, "user-layout.json");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_TOKEN_HUD_HOME")) &&
                !File.Exists(Layout) && File.Exists(oldLayout)) File.Copy(oldLayout, Layout);
        }

        internal string CheckPython()
        {
            if (string.IsNullOrEmpty(Pythonw) || !File.Exists(Pythonw))
                throw new InvalidOperationException("Python runtime not found. Extract the full portable ZIP, or set pythonw in settings.json. / 请解压完整便携包，或配置 Python 3.10+。");
            string consolePython = Path.Combine(Path.GetDirectoryName(Pythonw), "python.exe");
            if (!File.Exists(consolePython)) throw new InvalidOperationException("python.exe is missing next to pythonw.exe.");
            using (var process = Process.Start(new ProcessStartInfo(consolePython,
                "-I -X utf8 -c \"import sqlite3, ctypes, json, sys; print('.'.join(map(str, sys.version_info[:3])))\"") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                if (!process.WaitForExit(5000)) { process.Kill(); throw new InvalidOperationException("Python runtime check timed out."); }
                string version = process.StandardOutput.ReadToEnd().Trim();
                Version parsed;
                if (process.ExitCode != 0 || !Version.TryParse(version, out parsed) || parsed.Major != 3 || parsed.Minor < 10)
                    throw new InvalidOperationException("Python 3.10+ with sqlite3 is required.");
                return version;
            }
        }

        internal static string Quote(string argument)
        {
            // Escape for Windows CommandLineToArgvW / C runtime; no shell is involved.
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }

        internal void ConfigureAppFollowing(bool enabled)
        {
            string args = "-I -X utf8 " + Quote(Path.Combine(Folder, "app_follow.py")) +
                (enabled ? " --enable" : " --disable") + " --windows-exe " + Quote(Path.Combine(Folder, "CodexTokenHud.exe")) +
                (CornerMode ? " --resident" : "") +
                " --config " + Quote(Path.Combine(DataDirectory, "app-follow", "config.json")) +
                " --runtime " + Quote(Runtime) + " --codex-home " + Quote(CodexHome);
            using (var process = Process.Start(new ProcessStartInfo(Pythonw, args) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = Folder, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                var errors = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { process.Kill(); throw new InvalidOperationException("Application-follow setup timed out."); }
                if (process.ExitCode != 0) throw new InvalidOperationException(errors.Result);
            }
        }

        internal void SaveCornerSettings()
        {
            var settings = Json.Read(Path.Combine(DataDirectory, "settings.json"));
            settings["corner_mode"] = CornerMode;
            settings["corner_monitor"] = CornerMonitor;
            settings["corner_opacity"] = CornerOpacity;
            settings["corner_motion"] = CornerMotion;
            settings["reset_alerts"] = ResetAlerts;
            Json.Write(Path.Combine(DataDirectory, "settings.json"), settings);
        }

        internal bool HasLegacyLogonStartup()
        {
            object service = null, root = null, task = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                ((dynamic)service).Connect();
                root = ((dynamic)service).GetFolder("\\");
                task = ((dynamic)root).GetTask("CodexTokenHud-" + Environment.UserName);
                dynamic action = ((dynamic)task).Definition.Actions[1];
                string path = action.Path, arguments = action.Arguments;
                string oldConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTokenHud", "app-follow", "config.json");
                bool oldWatcher = string.Equals(path, Pythonw, StringComparison.OrdinalIgnoreCase) &&
                    arguments.Contains(Path.Combine(Folder, "app_follow.py")) && arguments.Contains(oldConfig);
                return ((dynamic)task).Enabled && (oldWatcher || string.Equals(path,
                    Path.Combine(Folder, "CodexTokenHud.exe"), StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
            finally
            {
                foreach (object item in new[] { task, root, service })
                    if (item != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(item);
            }
        }
    }
}
