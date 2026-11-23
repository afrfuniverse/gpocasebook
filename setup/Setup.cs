using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GPOCasebook.Setup
{
    internal static class Install
    {
        public static string Version { get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }
        public const string Product = "GPOCasebook";
        private const string App = Product + ".exe";
        private const string Setup = Product + "-Setup.exe";
        private const string Marker = ".gpocasebook-install";
        private const string Owner = "GPOCasebook per-user installation";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product;
        private static readonly string[] InstalledFiles = { App, Setup, "LICENSE.txt", Marker };
        public static string Folder { get { return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product); } }
        public static string Archive { get { return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product, "Archive"); } }
        public static string Exe { get { return Path.Combine(Folder, App); } }
        private static string Self { get { return Assembly.GetExecutingAssembly().Location; } }

        public static void Apply(bool desktop) { Apply(Folder, desktop, true); }

        private static void Apply(string folder, bool desktop, bool integrate)
        {
            string parent = Path.GetDirectoryName(folder);
            Directory.CreateDirectory(parent);
            using (Acquire(parent))
            {
                bool existing = Directory.Exists(folder);
                if (existing) CheckOwned(folder);
                string stage = NextFolder(parent);
                string previous = NextFolder(parent);
                bool hadDesktop = integrate && File.Exists(DesktopLink);
                Directory.CreateDirectory(stage);
                try
                {
                    File.WriteAllText(Path.Combine(stage, Marker), Owner);
                    Extract(App, Path.Combine(stage, App));
                    Extract("LICENSE.txt", Path.Combine(stage, "LICENSE.txt"));
                    File.Copy(Self, Path.Combine(stage, Setup));
                    if (existing) Directory.Move(folder, previous);
                    try
                    {
                        Directory.Move(stage, folder);
                        if (integrate) Register(desktop);
                    }
                    catch
                    {
                        if (Directory.Exists(folder)) CleanOwned(folder);
                        if (existing) Directory.Move(previous, folder);
                        if (integrate)
                        {
                            try { if (existing) Register(hadDesktop); else Unregister(); }
                            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is COMException) { }
                        }
                        throw;
                    }
                    TryClean(previous);
                }
                finally { TryClean(stage); }
            }
        }

        public static void Remove() { Remove(Folder, true); }

        private static void Remove(string folder, bool integrate)
        {
            if (!Directory.Exists(folder))
            {
                if (integrate) Unregister();
                return;
            }
            string parent = Path.GetDirectoryName(folder);
            using (Acquire(parent))
            {
                CheckOwned(folder);
                string staged = NextFolder(parent);
                bool hadDesktop = integrate && File.Exists(DesktopLink);
                Directory.Move(folder, staged);
                try { if (integrate) Unregister(); }
                catch
                {
                    Directory.Move(staged, folder);
                    if (integrate)
                    {
                        try { Register(hadDesktop); }
                        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is COMException) { }
                    }
                    throw;
                }
                TryClean(staged);
            }
        }

        private static FileStream Acquire(string parent)
        {
            return new FileStream(Path.Combine(parent, ".gpocasebook-setup.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }

        private static string NextFolder(string parent) { return Path.Combine(parent, ".gpocasebook-" + Guid.NewGuid().ToString("N")); }

        private static void CheckOwned(string folder)
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The install folder is a link. Choose an ordinary Windows profile folder.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (!InstalledFiles.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase) ||
                    (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("The install folder contains an unexpected entry. Review it before continuing.");
            }
            string marker = Path.Combine(folder, Marker);
            if (!File.Exists(marker) || new FileInfo(marker).Length > 128 || File.ReadAllText(marker) != Owner)
                throw new IOException("The install folder is occupied by an unrecognized installation.");
        }

        private static void CleanOwned(string folder)
        {
            CheckOwned(folder);
            foreach (string name in InstalledFiles) File.Delete(Path.Combine(folder, name));
            Directory.Delete(folder);
        }

        private static void TryClean(string folder)
        {
            if (!Directory.Exists(folder)) return;
            try { CleanOwned(folder); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Extract(string name, string path)
        {
            using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (source == null) throw new InvalidDataException("The setup package is missing " + name + ".");
                source.CopyTo(destination);
                destination.Flush(true);
            }
        }

        private static string StartLink { get { return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product + ".lnk"); } }
        private static string DesktopLink { get { return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Product + ".lnk"); } }

        private static void Register(bool desktop)
        {
            Shortcut(StartLink);
            if (desktop) Shortcut(DesktopLink);
            else File.Delete(DesktopLink);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", Product);
                key.SetValue("Publisher", "Alireza Foroughifar");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("InstallLocation", Folder);
                key.SetValue("DisplayIcon", Exe);
                key.SetValue("UninstallString", "\"" + Path.Combine(Folder, Setup) + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void Unregister()
        {
            File.Delete(StartLink);
            File.Delete(DesktopLink);
            Registry.CurrentUser.DeleteSubKey(UninstallKey, false);
        }

        private static void Shortcut(string path)
        {
            string parent = Path.GetDirectoryName(path);
            Directory.CreateDirectory(parent);
            string stage = Path.Combine(parent, ".gpocasebook-" + Guid.NewGuid().ToString("N") + ".lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
            dynamic shortcut = null;
            try
            {
                shortcut = shell.CreateShortcut(stage);
                shortcut.TargetPath = Exe;
                shortcut.WorkingDirectory = Folder;
                shortcut.Description = "Review and archive Windows Group Policy evidence";
                shortcut.Save();
                if (File.Exists(path)) File.Replace(stage, path, null);
                else File.Move(stage, path);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
                File.Delete(stage);
            }
        }

        public static bool FromInstalledFolder()
        { return String.Equals(Path.GetDirectoryName(Self), Folder, StringComparison.OrdinalIgnoreCase); }

        public static void RelaunchRemover()
        {
            string temporary = Path.Combine(Path.GetTempPath(), "GPOCasebook-remove-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Self, temporary);
            using (var process = Process.GetCurrentProcess())
                Process.Start(new ProcessStartInfo(temporary, "--uninstall-after " + process.Id) { UseShellExecute = true });
        }

        public static void Verify(string logPath)
        {
            string parent = Path.Combine(Path.GetTempPath(), "GPOCasebook-verify-" + Guid.NewGuid().ToString("N"));
            string appFolder = Path.Combine(parent, "Programs", Product);
            string archive = Path.Combine(parent, Product, "Archive");
            Directory.CreateDirectory(archive);
            string report = Path.Combine(archive, "retained-report.json");
            File.WriteAllText(report, "retain");
            Apply(appFolder, false, false);
            string installed = Path.Combine(appFolder, App);
            if (!File.Exists(installed) || !File.Exists(Path.Combine(appFolder, Setup)))
                throw new InvalidDataException("The application payload was not installed.");
            string output = Path.Combine(parent, "verify-ui.txt");
            using (var p = Process.Start(new ProcessStartInfo(installed, "--verify-ui \"" + output + "\"")
            { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!p.WaitForExit(20000))
                {
                    p.Kill();
                    throw new InvalidDataException("The installed application timed out.");
                }
                if (p.ExitCode != 0 || !File.Exists(output))
                {
                    string detail = File.Exists(output) ? File.ReadAllText(output) : "The installer could not verify the workbench.";
                    throw new InvalidDataException("The installed application failed verification.\n" + detail);
                }
            }
            Apply(appFolder, false, false);
            Remove(appFolder, false);
            if (Directory.Exists(appFolder) || File.ReadAllText(report) != "retain")
                throw new InvalidDataException("Uninstall did not preserve the report archive.");
            File.WriteAllText(logPath, File.ReadAllText(output) + Environment.NewLine +
                "PASS payload extraction, installed app startup, reinstall and archive retention.");
        }
    }

    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool verify = args.Length == 2 && args[0] == "--verify";
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (verify)
                {
                    using (var wizard = new Wizard()) wizard.VerifyPages();
                    Install.Verify(args[1]);
                    return 0;
                }
                bool uninstall = args.Length > 0 && (args[0] == "--uninstall" || args[0] == "--uninstall-after");
                if (uninstall && Install.FromInstalledFolder()) { Install.RelaunchRemover(); return 0; }
                if (uninstall && args.Length == 2 && args[0] == "--uninstall-after")
                {
                    int pid;
                    if (Int32.TryParse(args[1], out pid))
                    {
                        try
                        {
                            using (var host = Process.GetProcessById(pid)) host.WaitForExit(5000);
                        }
                        catch (ArgumentException) { }
                    }
                }
                if (uninstall)
                {
                    if (MessageBox.Show("Remove GPOCasebook from this Windows profile?\n\nYour archived captures stay in "
                        + Install.Archive + ".", "GPOCasebook uninstall", MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Question) == DialogResult.OK) Install.Remove();
                    return 0;
                }
                Application.Run(new Wizard());
                return 0;
            }
            catch (Exception e)
            {
                if (verify) File.WriteAllText(args[1], e.ToString());
                else MessageBox.Show(e.Message, "GPOCasebook Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal sealed class Wizard : Form
    {
        private readonly Label step, headline, description, path;
        private readonly CheckBox desktop, launch;
        private readonly ProgressBar progress;
        private readonly Button next, back, cancel;
        private int page;
        private bool installing;
        private static readonly Color Dark = Color.FromArgb(13, 23, 36);
        private static readonly Color Panel = Color.FromArgb(23, 38, 55);
        private static readonly Color Light = Color.FromArgb(232, 241, 248);
        private static readonly Color Muted = Color.FromArgb(174, 194, 211);
        private static readonly Color Accent = Color.FromArgb(122, 218, 205);

        public Wizard()
        {
            Text = "GPOCasebook Setup";
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(660, 429);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Dark;
            ForeColor = Light;

            var header = new Panel { BackColor = Panel, Bounds = new Rectangle(0, 0, 660, 77) };
            header.Controls.Add(Label("GPOCasebook", 28, 17, 24, Color.White));
            var version = Label(Install.Version, 550, 30, 11, Muted);
            header.Controls.Add(version);
            Controls.Add(header);

            step = Label("", 28, 101, 10, Accent);
            headline = Label("", 28, 132, 22, Light);
            description = new Label { Bounds = new Rectangle(30, 184, 593, 66), ForeColor = Muted };
            path = new Label { Bounds = new Rectangle(30, 246, 594, 44), ForeColor = Light, AutoEllipsis = true };
            desktop = new CheckBox { Text = "Create a desktop shortcut", Bounds = new Rectangle(30, 298, 560, 25) };
            launch = new CheckBox { Text = "Open GPOCasebook", Checked = true, Bounds = new Rectangle(30, 298, 560, 25) };
            progress = new ProgressBar { Bounds = new Rectangle(30, 303, 595, 12), Style = ProgressBarStyle.Marquee };
            cancel = UiButton("Cancel", 320, false);
            back = UiButton("Back", 424, false);
            next = UiButton("Next", 528, true);
            Controls.AddRange(new Control[] { step, headline, description, path, desktop, launch, progress, cancel, back, next });
            AcceptButton = next;
            CancelButton = cancel;
            cancel.Click += (sender, e) => Close();
            back.Click += (sender, e) => ShowPage(0);
            next.Click += async (sender, e) => await Next();
            FormClosing += (sender, e) => { if (installing) e.Cancel = true; };
            ShowPage(0);
        }

        private async Task Next()
        {
            if (page == 0) { ShowPage(1); return; }
            if (page == 2)
            {
                try
                {
                    if (launch.Checked) Process.Start(new ProcessStartInfo(Install.Exe) { UseShellExecute = true });
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, "Unable to open GPOCasebook", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                Close();
                return;
            }
            if (installing) return;
            installing = true;
            bool makeDesktop = desktop.Checked;
            progress.Visible = true;
            desktop.Visible = false;
            next.Enabled = back.Enabled = cancel.Enabled = false;
            try
            {
                await Task.Run(() => Install.Apply(makeDesktop));
                ShowPage(2);
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "Installation could not finish", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ShowPage(1);
            }
            finally { installing = false; progress.Visible = false; }
        }

        private void ShowPage(int value)
        {
            page = value;
            step.Text = value == 0 ? "1 OF 3  ·  WELCOME" : value == 1 ? "2 OF 3  ·  OPTIONS" : "3 OF 3  ·  COMPLETE";
            headline.Text = value == 0 ? "Install GPOCasebook" : value == 1 ? "Ready to install" : "Installation complete";
            description.Text = value == 0
                ? "Review Windows Group Policy events, compare captures and keep a local archive. Setup installs for your Windows account."
                : value == 1 ? "Setup will add GPOCasebook to your Start menu and install the files in the folder below."
                : "GPOCasebook is available in your Start menu. Saved captures are stored in the separate archive folder below.";
            path.Text = value == 0 ? "" : value == 1 ? Install.Folder : Install.Archive;
            desktop.Visible = value == 1;
            launch.Visible = value == 2;
            back.Enabled = value == 1;
            cancel.Enabled = value != 2;
            next.Enabled = true;
            next.Text = value == 0 ? "Next" : value == 1 ? "Install" : "Finish";
        }

        public void VerifyPages()
        {
            ShowPage(0);
            if (next.Text != "Next" || back.Enabled) throw new InvalidDataException("The welcome page is inconsistent.");
            ShowPage(1);
            if (next.Text != "Install" || path.Text != Install.Folder || !back.Enabled)
                throw new InvalidDataException("The options page is inconsistent.");
            ShowPage(2);
            if (next.Text != "Finish" || path.Text != Install.Archive || cancel.Enabled)
                throw new InvalidDataException("The finish page is inconsistent.");
        }

        private static Label Label(string value, int x, int y, float size, Color color)
        {
            return new Label { Text = value, Location = new Point(x, y), AutoSize = true,
                Font = new Font("Segoe UI", size, FontStyle.Bold), ForeColor = color };
        }

        private Button UiButton(string text, int x, bool primary)
        {
            var button = new Button { Text = text, Bounds = new Rectangle(x, 363, 96, 38),
                FlatStyle = FlatStyle.Flat, ForeColor = primary ? Dark : Light, BackColor = primary ? Accent : Panel };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(60, 84, 110);
            return button;
        }
    }
}
