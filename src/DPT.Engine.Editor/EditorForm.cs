//
// DPT.Engine - editor host.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

using DPT.Engine.Shared;

namespace DPT.Engine.Editor
{
    /// <summary>
    /// The editor window: an MSHTML view of the page in Assets\.
    ///
    /// The editor's interface is real HTML/CSS/JS rather than a tree of WinForms
    /// controls, which is the same choice the launcher makes and for the same
    /// reason: the layout lives in files a designer can edit, and the same page
    /// can later be served over HTTP so the editor works remotely. MSHTML ships
    /// with every supported Windows, so this costs no dependency.
    ///
    /// The page is written to disk and navigated to as a file rather than assigned
    /// through DocumentText. A document at about:blank does not get the host object
    /// on every Windows version, and the bridge below depends on it being a real
    /// origin.
    ///
    /// The bridge is navigation-based, not COM: the page navigates to
    /// <c>dptengine://verb</c>, the host cancels that navigation and reads the
    /// payload back out of the document. That works on Windows XP, where the
    /// managed ObjectForScripting route needs a newer MSHTML than the OS has.
    /// </summary>
    internal sealed class EditorForm : Form
    {
        /// <summary>Private URL scheme the page uses to call into the host.</summary>
        private const string BridgeScheme = "dptengine://";

        private readonly string projectPath;

        private WebBrowser browser;
        private string documentPath;

        /// <summary>
        /// The project currently open, which Save writes over.
        ///
        /// Mutable because Open and Save both change it: the field starts at the
        /// path passed on the command line and follows whatever the user opens.
        /// </summary>
        private string currentProjectPath;

        internal EditorForm(string projectPath)
        {
            this.projectPath = projectPath;
            currentProjectPath = projectPath;

            Text = Branding.EditorName;
            ClientSize = new Size(1280, 800);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x0B, 0x0D, 0x14);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // An icon is decoration; a missing one must not stop the editor.
            }

            browser = new WebBrowser();
            browser.Dock = DockStyle.Fill;
            browser.IsWebBrowserContextMenuEnabled = false;
            browser.AllowWebBrowserDrop = false;
            browser.WebBrowserShortcutsEnabled = true;
            browser.ScriptErrorsSuppressed = false;
            browser.Navigating += OnNavigating;
            Controls.Add(browser);

            LoadPage();
        }

        // ---------------------------------------------------------------- page

        /// <summary>
        /// Merges the three embedded assets into one document.
        ///
        /// Keeping CSS and script in separate files during development keeps them
        /// readable; at runtime the page is written to disk as one file, because
        /// MSHTML's handling of relative sub-resources from a generated document is
        /// inconsistent across versions.
        /// </summary>
        internal static string AssemblePage()
        {
            var html = ReadAsset("editor.html");

            html = html.Replace("/*__STYLE__*/", Brand(ReadAsset("theme.css")));
            html = html.Replace("/*__SCRIPT__*/", ReadAsset("app.js"));

            return Brand(html);
        }

        /// <summary>
        /// Substitutes the project's name and colours into a page asset, so a fork
        /// that renames itself does not have to hand-edit an embedded file.
        /// </summary>
        private static string Brand(string asset)
        {
            string primary, primary2, background, surface, text, onPrimary;
            Branding.GetPalette(Branding.Theme.Light, out primary, out primary2,
                out background, out surface, out text, out onPrimary);

            return asset
                .Replace("__BRAND_NAME__", Branding.Name)
                .Replace("__BRAND_TITLE__", Branding.EditorName)
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
            var assembly = typeof(EditorForm).Assembly;

            using (var stream = assembly.GetManifestResourceStream("DPT.Engine.Editor.Assets." + name))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Missing embedded asset: " + name);
                }

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
                var directory = Path.Combine(PortablePaths.DataDirectory, "editor");
                Directory.CreateDirectory(directory);

                documentPath = Path.Combine(directory, "editor.html");
                File.WriteAllText(documentPath, AssemblePage(), new UTF8Encoding(false));

                // DPTENGINE_EDITOR_TAB lets a shortcut open straight to a panel,
                // which is also what a deep link from the launcher uses.
                var tab = Environment.GetEnvironmentVariable("DPTENGINE_EDITOR_TAB");

                browser.Navigate(documentPath + (string.IsNullOrEmpty(tab) ? "" : "#" + tab));
            }
            catch (Exception error)
            {
                MessageBox.Show(this,
                    "Could not open the editor interface:\n\n" + error.Message,
                    Branding.EditorName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------------------------------------------------------- bridge

        /// <summary>
        /// The page calls the host by navigating to dptengine://verb. The
        /// navigation is cancelled and the verb dispatched; the payload is read back
        /// out of the page because it can be arbitrarily long.
        /// </summary>
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
                Send("error", "Editor error: " + error.Message);
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

        /// <summary>Host to page.</summary>
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
        /// Single entry point for everything the page asks for.
        ///
        /// The comparison is case-insensitive and that is not defensive coding:
        /// MSHTML lower-cases the authority part of a URI, so
        /// <c>dptengine://saveProject</c> arrives as <c>saveproject</c>. A
        /// case-sensitive switch silently ignores every camel-case verb.
        /// </summary>
        private void HandleVerb(string verb, string payload)
        {
            var name = (verb ?? "").Trim().ToLowerInvariant();

            switch (name)
            {
                case "ready":
                    // The page announces itself; answer with what it needs to boot.
                    Send("ready", "");
                    Send("status", "Ready.");
                    break;

                // The page's verbs, exactly as app.js spells them. A mismatch here
                // is silent - the host simply never answers - so the two files have
                // to be kept in step by hand.
                case "save":
                    SaveProject(payload);
                    break;

                case "open":
                    OpenProject(payload);
                    break;

                // Kept as aliases: a future page version may use the longer names,
                // and answering both costs nothing.
                case "saveproject":
                    SaveProject(payload);
                    break;

                case "openproject":
                    OpenProject(payload);
                    break;

                case "play":
                    PlayProject(payload);
                    break;

                case "status":
                    Send("status", payload ?? "");
                    break;

                default:
                    Send("error", "Unknown command: " + verb);
                    break;
            }
        }

        // ---------------------------------------------------------------- commands

        /// <summary>
        /// Opens a project and hands the text to the page.
        ///
        /// The page owns the format: the host only reads the file. That keeps the
        /// browser and the runtime from drifting apart, because there is exactly one
        /// implementation of the format on the editing side.
        /// </summary>
        private void OpenProject(string payload)
        {
            var path = payload;

            if (string.IsNullOrEmpty(path))
            {
                using (var dialog = new OpenFileDialog())
                {
                    dialog.Title = "Open project";
                    dialog.Filter = "DPT.Engine project (*.dptproj)|*.dptproj|All files (*.*)|*.*";

                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                    path = dialog.FileName;
                }
            }

            if (!File.Exists(path))
            {
                Send("error", "No such file: " + path);
                return;
            }

            try
            {
                currentProjectPath = path;

                var text = File.ReadAllText(path, Encoding.UTF8);

                Send("project", text);
                Send("status", "Opened " + Path.GetFileName(path));
            }
            catch (Exception error)
            {
                Send("error", "Could not read the project: " + error.Message);
            }
        }

        /// <summary>
        /// Writes the project the page sends.
        ///
        /// A path from the page wins when it is absolute, so a page that tracks its
        /// own file name round-trips correctly; otherwise the dialog decides.
        /// </summary>
        private void SaveProject(string payload)
        {
            var path = currentProjectPath;

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                // Saving over the open file is the common case; no dialog.
            }
            else
            {
                using (var dialog = new SaveFileDialog())
                {
                    dialog.Title = "Save project";
                    dialog.Filter = "DPT.Engine project (*.dptproj)|*.dptproj";
                    dialog.DefaultExt = "dptproj";

                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                    path = dialog.FileName;
                }
            }

            try
            {
                currentProjectPath = path;

                File.WriteAllText(path, payload ?? "", new UTF8Encoding(false));

                Send("saved", path);
                Send("status", "Saved " + Path.GetFileName(path));
            }
            catch (Exception error)
            {
                Send("error", "Could not save: " + error.Message);
            }
        }

        /// <summary>
        /// Launches the runtime on the project the editor has open.
        ///
        /// The page sends the project text; it is written to a temporary file and
        /// the runtime is started on it. That means "play" always runs exactly what
        /// is on screen, saved or not - which is the behaviour an editor needs.
        /// </summary>
        private void PlayProject(string payload)
        {
            try
            {
                var runtime = Path.Combine(PortablePaths.ExecutableDirectory,
                    Branding.RuntimeExecutable + ".exe");

                if (!File.Exists(runtime))
                {
                    Send("error", "The runtime was not found next to the editor: " + runtime);
                    return;
                }

                var directory = Path.Combine(PortablePaths.DataDirectory, "Preview");
                Directory.CreateDirectory(directory);

                var preview = Path.Combine(directory, "preview.dptproj");
                File.WriteAllText(preview, payload ?? "", new UTF8Encoding(false));

                var info = new System.Diagnostics.ProcessStartInfo();
                info.FileName = runtime;
                info.Arguments = "\"" + preview + "\"";
                info.UseShellExecute = false;
                info.WorkingDirectory = PortablePaths.ExecutableDirectory;

                System.Diagnostics.Process.Start(info);

                Send("status", "Preview launched.");
            }
            catch (Exception error)
            {
                Send("error", "Could not start the runtime: " + error.Message);
            }
        }
    }
}
