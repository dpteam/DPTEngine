//
// DPT.Engine - launcher host.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using DPT.Engine.Shared;

namespace DPT.Engine.Launcher
{
    /// <summary>
    /// The launcher window: an MSHTML view over the page in Assets\.
    ///
    /// The launcher's job is narrow - pick something and start it - so the whole
    /// interface is a page, and the C# side is a bridge plus the few things a page
    /// cannot do: enumerate files, start a process, talk to the master.
    ///
    /// Master calls run on a worker thread. A directory that is down must not
    /// freeze the window for four seconds while the socket times out, so every
    /// request is asynchronous and the answer arrives as a page event.
    /// </summary>
    internal sealed class LauncherForm : Form
    {
        private const string BridgeScheme = "dptengine://";

        private WebBrowser browser;
        private Process running;
        private string documentPath;

        private string masterHost = "";
        private int masterPort = MasterProtocol.DefaultPort;

        internal LauncherForm()
        {
            Text = Branding.LauncherName;
            ClientSize = new Size(1100, 760);
            MinimumSize = new Size(880, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x0B, 0x0D, 0x14);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            browser = new WebBrowser();
            browser.Dock = DockStyle.Fill;
            browser.IsWebBrowserContextMenuEnabled = false;
            browser.AllowWebBrowserDrop = false;
            browser.WebBrowserShortcutsEnabled = true;
            browser.ScriptErrorsSuppressed = true;
            browser.Navigating += OnNavigating;
            Controls.Add(browser);

            LoadSettings();
            LoadPage();
        }

        // ---------------------------------------------------------------- settings

        private string SettingsPath
        {
            get { return Path.Combine(PortablePaths.DataDirectory, "launcher.ini"); }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;

                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var trimmed = line.Trim();

                    if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';') continue;

                    var split = trimmed.IndexOf('=');
                    if (split <= 0) continue;

                    var key = trimmed.Substring(0, split).Trim().ToLowerInvariant();
                    var value = trimmed.Substring(split + 1).Trim();

                    if (key == "masterhost") masterHost = value;

                    if (key == "masterport")
                    {
                        int port;
                        if (int.TryParse(value, out port) && port > 0) masterPort = port;
                    }
                }
            }
            catch
            {
                // Settings are a convenience; a broken file must not stop the launcher.
            }
        }

        private void SaveSettings()
        {
            try
            {
                PortablePaths.EnsureParent(SettingsPath);

                var builder = new StringBuilder();
                builder.AppendLine("# DPT.Engine launcher settings");
                builder.AppendLine("masterHost=" + masterHost);
                builder.AppendLine("masterPort=" + masterPort);

                File.WriteAllText(SettingsPath, builder.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- page

        internal static string AssemblePage()
        {
            var html = ReadAsset("ui.html");

            html = html.Replace("/*__STYLE__*/", Brand(ReadAsset("theme.css")));
            html = html.Replace("/*__SCRIPT__*/", ReadAsset("app.js"));

            return Brand(html);
        }

        private static string Brand(string asset)
        {
            string primary, primary2, background, surface, text, onPrimary;
            Branding.GetPalette(Branding.Theme.Light, out primary, out primary2,
                out background, out surface, out text, out onPrimary);

            return asset
                .Replace("__BRAND_NAME__", Branding.Name)
                .Replace("__BRAND_TITLE__", Branding.LauncherName)
                .Replace("__BRAND_TAGLINE__", Branding.Tagline)
                .Replace("__BRAND_VERSION__", Branding.Version)
                .Replace("__BRAND_HOME__", Branding.HomeUrl)
                .Replace("__BRAND_PRIMARY__", primary)
                .Replace("__BRAND_PRIMARY2__", primary2)
                .Replace("__BRAND_BG__", background)
                .Replace("__BRAND_SURFACE__", surface)
                .Replace("__BRAND_TEXT__", text)
                .Replace("__BRAND_ONPRIMARY__", onPrimary)
                .Replace("__BRAND_DARK_PRIMARY__", Branding.DarkPrimary)
                .Replace("__BRAND_DARK_PRIMARY2__", Branding.DarkPrimary2)
                .Replace("__BRAND_DARK_BG__", Branding.DarkBackground)
                .Replace("__BRAND_DARK_SURFACE__", Branding.DarkSurface)
                .Replace("__BRAND_DARK_TEXT__", Branding.DarkText)
                .Replace("__BRAND_OLED_PRIMARY__", Branding.OledPrimary)
                .Replace("__BRAND_OLED_BG__", Branding.OledBackground)
                .Replace("__BRAND_OLED_TEXT__", Branding.OledText);
        }

        private static string ReadAsset(string name)
        {
            var assembly = typeof(LauncherForm).Assembly;

            using (var stream = assembly.GetManifestResourceStream("DPT.Engine.Launcher.Assets." + name))
            {
                if (stream == null) throw new InvalidOperationException("Missing embedded asset: " + name);

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private void LoadPage()
        {
            try
            {
                var directory = Path.Combine(PortablePaths.DataDirectory, "webview");
                Directory.CreateDirectory(directory);

                documentPath = Path.Combine(directory, "launcher.html");
                File.WriteAllText(documentPath, AssemblePage(), new UTF8Encoding(false));

                var tab = Environment.GetEnvironmentVariable("DPTENGINE_LAUNCHER_TAB");

                browser.Navigate(documentPath + (string.IsNullOrEmpty(tab) ? "" : "#" + tab));
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "Could not open the launcher interface:\n\n" + error.Message,
                    Branding.LauncherName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------------------------------------------------------- bridge

        private void OnNavigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            var url = e.Url == null ? "" : e.Url.AbsoluteUri;

            if (!url.StartsWith(BridgeScheme, StringComparison.OrdinalIgnoreCase)) return;

            e.Cancel = true;

            var verb = url.Substring(BridgeScheme.Length).Trim('/');

            var query = verb.IndexOf('?');
            if (query >= 0) verb = verb.Substring(0, query);

            var payload = TakePayload();

            try
            {
                HandleVerb(verb, payload);
            }
            catch (Exception error)
            {
                Send("error", "Launcher error: " + error.Message);
            }
        }

        private string TakePayload()
        {
            if (browser.Document == null) return "";

            try
            {
                var result = browser.Document.InvokeScript("dptengineTakePayload");
                return Convert.ToString(result) ?? "";
            }
            catch
            {
                return "";
            }
        }

        private void Send(string verb, string payload)
        {
            if (browser.Document == null) return;

            try
            {
                browser.Document.InvokeScript("dptReceive", new object[] { verb, payload ?? "" });
            }
            catch
            {
                // The document is navigating or has been replaced.
            }
        }

        /// <summary>
        /// Dispatches a page request.
        ///
        /// Case-insensitive on purpose: MSHTML lower-cases the authority of a URI,
        /// so a camel-case verb arrives flattened and a case-sensitive switch would
        /// silently ignore every one of them.
        /// </summary>
        private void HandleVerb(string verb, string payload)
        {
            var name = (verb ?? "").Trim().ToLowerInvariant();

            switch (name)
            {
                case "ready":
                    Send("settings", masterHost + "|" + masterPort);
                    Send("projects", DescribeProjects());
                    Send("hasruntime", File.Exists(RuntimePath) ? "1" : "0");
                    Send("running", running != null && !running.HasExited ? "1" : "0");
                    Send("status", "Ready.");
                    break;

                case "refresh":
                    RefreshProjects(payload);
                    break;

                case "launch":
                    Launch(payload);
                    break;

                case "editor":
                    LaunchEditor(payload);
                    break;

                case "setmaster":
                    SetMaster(payload);
                    break;

                case "openfolder":
                    OpenFolder();
                    break;

                default:
                    Send("error", "Unknown command: " + verb);
                    break;
            }
        }

        // ---------------------------------------------------------------- commands

        private string RuntimePath
        {
            get
            {
                return Path.Combine(PortablePaths.ExecutableDirectory,
                    Branding.RuntimeExecutable + ".exe");
            }
        }

        private string EditorPath
        {
            get
            {
                return Path.Combine(PortablePaths.ExecutableDirectory,
                    Branding.EditorExecutable + ".exe");
            }
        }

        /// <summary>Lists the .dptproj files next to the launcher, as a JSON array.</summary>
        private string DescribeProjects()
        {
            var array = JsonValue.NewArray();

            try
            {
                var directory = Path.Combine(PortablePaths.ExecutableDirectory, "Projects");

                if (!Directory.Exists(directory)) directory = PortablePaths.ExecutableDirectory;

                var files = Directory.GetFiles(directory, "*.dptproj");

                Array.Sort(files);

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    var entry = JsonValue.NewObject();

                    entry.Set("path", file);
                    entry.Set("name", Path.GetFileNameWithoutExtension(file));
                    entry.Set("bytes", (double)info.Length);

                    array.Add(entry);
                }
            }
            catch
            {
                // An unreadable folder simply produces an empty list.
            }

            return array.ToJson(false);
        }

        private void SetMaster(string payload)
        {
            // "host:port", or just "host".
            var text = (payload ?? "").Trim();

            if (text.Length == 0)
            {
                masterHost = "";
                SaveSettings();
                Send("status", "Master cleared.");
                return;
            }

            var colon = text.LastIndexOf(':');

            if (colon > 0)
            {
                int port;
                if (int.TryParse(text.Substring(colon + 1), out port) && port > 0)
                {
                    masterHost = text.Substring(0, colon);
                    masterPort = port;
                    SaveSettings();
                    Send("status", "Master set to " + masterHost + ":" + masterPort);
                    return;
                }
            }

            masterHost = text;
            SaveSettings();
            Send("status", "Master set to " + masterHost + ":" + masterPort);
        }

        /// <summary>
        /// Asks the master for its catalogue, on a worker thread.
        ///
        /// Blocking the UI thread on a socket would freeze the window for the whole
        /// connect timeout whenever the master is down, which is the common case for
        /// anyone who has not configured one.
        /// </summary>
        private void RefreshProjects(string payload)
        {
            var host = masterHost;
            var port = masterPort;

            if (string.IsNullOrEmpty(host))
            {
                Send("projects", "[]");
                Send("status", "No master configured.");
                return;
            }

            Send("status", "Contacting " + host + "...");

            var worker = new Thread(delegate()
            {
                string result;
                string status;

                try
                {
                    using (var client = new MasterClient(host, port, 5000))
                    {
                        var projects = client.ProjectList();
                        var array = JsonValue.NewArray();

                        for (var i = 0; i < projects.Length; i++)
                        {
                            var entry = JsonValue.NewObject();

                            entry.Set("id", projects[i].IdHex);
                            entry.Set("name", projects[i].Name);
                            entry.Set("author", projects[i].Author);
                            entry.Set("kind", projects[i].KindText);
                            entry.Set("url", projects[i].Url);
                            entry.Set("bytes", (double)projects[i].Bytes);
                            entry.Set("downloads", (double)projects[i].Downloads);

                            array.Add(entry);
                        }

                        result = array.ToJson(false);
                        status = projects.Length + " project(s) on " + host + ".";
                    }
                }
                catch (Exception error)
                {
                    result = "[]";
                    status = "Master unreachable: " + error.Message;
                }

                Post(delegate
                {
                    Send("projects", result);
                    Send("status", status);
                });
            });

            worker.IsBackground = true;
            worker.Name = "DPT.Engine.Launcher.Master";
            worker.Start();
        }

        private void Launch(string payload)
        {
            var runtime = RuntimePath;

            if (!File.Exists(runtime))
            {
                Send("error", "The runtime was not found next to the launcher:\n" + runtime);
                return;
            }

            if (running != null && !running.HasExited)
            {
                Send("status", "The engine is already running.");
                return;
            }

            try
            {
                var info = new ProcessStartInfo();
                info.FileName = runtime;
                info.UseShellExecute = false;
                info.WorkingDirectory = PortablePaths.ExecutableDirectory;

                if (!string.IsNullOrEmpty(payload))
                {
                    info.Arguments = "\"" + payload + "\"";
                }

                running = Process.Start(info);

                Send("running", "1");
                Send("status", "Engine started.");
            }
            catch (Exception error)
            {
                Send("error", "Could not start the engine: " + error.Message);
            }
        }

        private void LaunchEditor(string payload)
        {
            var editor = EditorPath;

            if (!File.Exists(editor))
            {
                Send("error", "The editor was not found next to the launcher:\n" + editor);
                return;
            }

            try
            {
                var info = new ProcessStartInfo();
                info.FileName = editor;
                info.UseShellExecute = false;
                info.WorkingDirectory = PortablePaths.ExecutableDirectory;

                if (!string.IsNullOrEmpty(payload))
                {
                    info.Arguments = "\"" + payload + "\"";
                }

                Process.Start(info);

                Send("status", "Editor started.");
            }
            catch (Exception error)
            {
                Send("error", "Could not start the editor: " + error.Message);
            }
        }

        private void OpenFolder()
        {
            try
            {
                Process.Start("explorer.exe", "\"" + PortablePaths.ExecutableDirectory + "\"");
            }
            catch (Exception error)
            {
                Send("error", "Could not open the folder: " + error.Message);
            }
        }

        /// <summary>Runs a delegate on the UI thread, because the page must be touched there.</summary>
        private void Post(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated) return;

            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // The window closed while a request was in flight.
            }
        }
    }
}
