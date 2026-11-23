using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GPOCasebook
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool verify = args.Length == 2 && args[0] == "--verify-ui";
            try
            {
                var app = new Application();
                bool openDomain = (args.Length == 1 || args.Length == 2) && args[0] == "--domain";
                string family = PackageIdentity.CurrentFamily();
                if (openDomain && args.Length == 2)
                {
                    if (!PackageIdentity.IsValidFamily(args[1]) ||
                        (family != null && !String.Equals(family, args[1], StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException("The domain window received an invalid package family.");
                    family = args[1];
                }
                var archive = new Archive(Archive.PathForFamily(family));
                var workbench = new Workbench(archive, openDomain, family);
                if (verify)
                {
                    var domain = new DomainWindow(workbench.View, archive);
                    var help = workbench.CreateDocumentationDialog(false);
                    var about = workbench.CreateDocumentationDialog(true);
                    if (((TextBlock)workbench.View.FindName("CaseTitle")).Text != "No capture open" ||
                        domain.View.FindName("Start") == null ||
                        !((StackPanel)help.Content).Children.OfType<TextBox>().Single().Text.Contains("Scan computers in a domain") ||
                        !((StackPanel)about.Content).Children.OfType<TextBox>().Single().Text.Contains("MIT License"))
                        throw new InvalidOperationException("The workbench did not load its starting view and documentation.");
                    File.WriteAllText(args[1], "PASS empty workbench, domain window, Help and MIT license.");
                    return 0;
                }
                return app.Run(workbench.View);
            }
            catch (Exception e)
            {
                if (verify) File.WriteAllText(args[1], e.ToString());
                else MessageBox.Show("GPOCasebook could not open.\n\n" + e.Message,
                    "GPOCasebook", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    internal sealed class Workbench
    {
        private readonly Archive archive;
        private readonly string packageFamily;
        private const string ProjectUrl = "https://github.com/afrfuniverse/gpocasebook";
        private List<ArchiveEntry> entries = new List<ArchiveEntry>();
        private CaseData current;
        private CaseData comparedBefore;
        private CaseData comparedAfter;
        private bool busy;
        private bool updating;
        private int unreadable;
        private static readonly Brush Error = BrushOf("#FFAAA2");
        private static readonly Brush Warm = BrushOf("#FFD389");
        private static readonly Brush Success = BrushOf("#7ADACD");
        private static readonly Brush Muted = BrushOf("#9DB0C6");

        public Window View { get; private set; }

        public Workbench(Archive archive, bool openDomainOnStart = false, string packageFamily = null)
        {
            this.archive = archive;
            this.packageFamily = packageFamily;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Workbench.xaml"))
                View = (Window)XamlReader.Load(stream);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GPOCasebook.png"))
                View.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            At<Image>("AppMark").Source = View.Icon;

            View.Loaded += async (sender, e) =>
            {
                await Run("Opening archive…", async () =>
                {
                    await RefreshArchive();
                    Status("Ready");
                });
                if (openDomainOnStart) await OpenDomain();
            };
            At<Button>("DomainButton").Click += async (sender, e) => await OpenDomain();
            At<Button>("HelpButton").Click += (sender, e) => ShowDocumentation(false);
            At<Button>("AboutButton").Click += (sender, e) => ShowDocumentation(true);
            At<Button>("RefreshArchiveButton").Click += async (sender, e) => await Run("Refreshing archive…", async () =>
            {
                await RefreshArchive();
                Status("Archive refreshed");
            });
            At<Button>("CaptureButton").Click += async (sender, e) => await Run("Reading Windows Group Policy evidence…", async () =>
            {
                var captured = await Task.Run(() => Capture.Local());
                Show(captured);
                Status(captured.Events.Count + " events and " + captured.Policies.Count + " RSoP records collected");
            });
            At<Button>("ImportButton").Click += async (sender, e) => await Import();
            At<Button>("ArchiveButton").Click += async (sender, e) => await SaveCase();
            At<Button>("ExportButton").Click += async (sender, e) => await Export();
            At<Button>("OpenArchiveButton").Click += async (sender, e) => await Run("Opening archive folder…", () =>
            {
                Directory.CreateDirectory(archive.FolderPath);
                Process.Start(new ProcessStartInfo(archive.FolderPath) { UseShellExecute = true });
                Status("Archive folder opened");
                return Task.CompletedTask;
            });
            At<TextBox>("ArchiveSearch").TextChanged += (sender, e) => RefreshList();
            At<ListBox>("ArchiveList").SelectionChanged += async (sender, e) =>
            {
                if (updating || busy) return;
                var selected = At<ListBox>("ArchiveList").SelectedItem as ArchiveEntry;
                if (selected == null || (current != null && current.Id == selected.Id)) return;
                await Run("Opening saved capture…", async () =>
                {
                    Show(await Task.Run(() => archive.Load(selected.Id)));
                    Status("Saved capture opened");
                });
            };
            At<ComboBox>("RunPicker").SelectionChanged += (sender, e) => SelectRun();
            At<DataGrid>("Timeline").SelectionChanged += (sender, e) => ShowEvent(At<DataGrid>("Timeline").SelectedItem as PolicyEvent);
            At<ComboBox>("BeforePicker").SelectionChanged += async (sender, e) => await UpdateComparison();
            At<ComboBox>("AfterPicker").SelectionChanged += async (sender, e) => await UpdateComparison();
            Text("CaseTitle", "No capture open");
            Text("CaseMetadata", "Collect from this PC, import evidence, or scan selected domain computers.");
            Text("ComputerStatus", "Unavailable");
            Text("UserStatus", "Unavailable");
            Text("EventCount", "—");
            Text("EventHeadline", "Select a capture to review its evidence");
            Text("PolicyHint", "Open a capture to review resultant policies.");
            Text("CompareHint", "Open a capture to select a baseline and follow-up.");
            RefreshList();
            UpdateActions();
        }

        private T At<T>(string name) where T : class { return (T)View.FindName(name); }
        private void Text(string name, string value) { At<TextBlock>(name).Text = value; }
        private void Status(string value) { Text("StatusBar", value); }

        private async Task OpenDomain()
        {
            await Run("Domain scan open…", async () =>
            {
                new DomainWindow(View, archive, packageFamily).Show();
                await RefreshArchive();
                Status("Archive refreshed");
            });
        }

        private static string ResourceText(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }

        private void ShowDocumentation(bool about)
        {
            var dialog = CreateDocumentationDialog(about);
            dialog.Owner = View;
            dialog.ShowDialog();
        }

        internal Window CreateDocumentationDialog(bool about)
        {
            var dialog = Dialog(about ? "About GPOCasebook" : "GPOCasebook Help", 707, false);
            dialog.Resources.MergedDictionaries.Add(View.Resources);
            var form = (StackPanel)dialog.Content;
            form.Children.Add(new TextBlock
            {
                Text = about ? "GPOCasebook " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) : "Review Group Policy evidence",
                FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
            });
            if (about)
            {
                form.Children.Add(new TextBlock
                {
                    Text = "Developed by Alireza Foroughifar · MIT License",
                    Foreground = Muted, Margin = new Thickness(0, 0, 0, 10)
                });
                var link = new Button { Content = "GPOCasebook on GitHub", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 11) };
                link.Click += (sender, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true }); }
                    catch (Exception error) { MessageBox.Show(dialog, error.Message, "Open GitHub"); }
                };
                form.Children.Add(link);
            }
            var text = new TextBox
            {
                Text = ResourceText(about ? "LICENSE.txt" : "Help.txt"), IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = Math.Max(235, Math.Min(about ? 385 : 520, SystemParameters.WorkArea.Height - 230)),
                FontFamily = new FontFamily(about ? "Consolas" : "Segoe UI"), Padding = new Thickness(11)
            };
            form.Children.Add(text);
            var close = new Button
            {
                Content = "Close", IsCancel = true, IsDefault = true,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 11, 0, 0)
            };
            close.Click += (sender, e) => dialog.Close();
            form.Children.Add(close);
            return dialog;
        }

        private async Task Import()
        {
            string[] files = new string[0];
            string rsop = null;
            var dialog = Dialog("Import Group Policy evidence", 465);
            var form = (StackPanel)dialog.Content;
            form.Children.Add(new TextBlock { Text = "Select Windows event exports, an RSoP XML report, or both.",
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 13) });
            var events = new Button { Content = "Choose event files", HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(13, 6, 13, 6), Margin = new Thickness(0, 0, 0, 5) };
            var result = new Button { Content = "Choose gpresult XML", HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(13, 6, 13, 6), Margin = new Thickness(0, 9, 0, 5) };
            var eventsLabel = new TextBlock { Text = "No event export selected", Foreground = Muted, TextWrapping = TextWrapping.Wrap };
            var resultLabel = new TextBlock { Text = "No RSoP report selected", Foreground = Muted, TextWrapping = TextWrapping.Wrap };
            var confirm = new Button { Content = "Open capture", IsDefault = true, IsEnabled = false };
            var title = new TextBox { Text = "Imported Group Policy capture", MaxLength = 110, Padding = new Thickness(5) };
            events.Click += (sender, e) =>
            {
                var picker = new OpenFileDialog { Multiselect = true, Filter = "Windows events (*.evtx;*.xml)|*.evtx;*.xml",
                    Title = "Select Group Policy event exports" };
                if (picker.ShowDialog(dialog) != true) return;
                if (picker.FileNames.Length > 8)
                {
                    MessageBox.Show(dialog, "Select up to eight event files per capture.", "Import files");
                    return;
                }
                files = picker.FileNames;
                eventsLabel.Text = files.Length + " event file(s) selected";
                confirm.IsEnabled = true;
            };
            result.Click += (sender, e) =>
            {
                var picker = new OpenFileDialog { Filter = "RSoP XML (*.xml)|*.xml", Title = "Select a gpresult /x report" };
                if (picker.ShowDialog(dialog) != true) return;
                rsop = picker.FileName;
                resultLabel.Text = Path.GetFileName(rsop);
                confirm.IsEnabled = true;
            };
            form.Children.Add(events);
            form.Children.Add(eventsLabel);
            form.Children.Add(result);
            form.Children.Add(resultLabel);
            form.Children.Add(new TextBlock { Text = "Capture name", Margin = new Thickness(0, 12, 0, 4) });
            form.Children.Add(title);
            AddDialogButtons(dialog, confirm);
            if (dialog.ShowDialog() != true) return;
            string label = String.IsNullOrWhiteSpace(title.Text) ? "Imported Group Policy capture" : title.Text.Trim();
            await Run("Reading the selected evidence…", async () =>
            {
                Show(await Task.Run(() => Capture.Import(files, rsop, label)));
                Status("Evidence imported");
            });
        }

        private async Task SaveCase()
        {
            if (current == null) return;
            var saved = current;
            var dialog = Dialog("Save to archive", 420);
            var form = (StackPanel)dialog.Content;
            form.Children.Add(new TextBlock { Text = "Capture name", Margin = new Thickness(0, 0, 0, 5) });
            var title = new TextBox { Text = saved.Title, MaxLength = 110, Padding = new Thickness(6) };
            form.Children.Add(title);
            AddDialogButtons(dialog, new Button { Content = "Save capture", IsDefault = true });
            dialog.Loaded += (sender, e) => { title.Focus(); title.SelectAll(); };
            if (dialog.ShowDialog() != true) return;

            await Run("Saving capture to the archive…", async () =>
            {
                string previous = saved.Title;
                saved.Title = String.IsNullOrWhiteSpace(title.Text) ? previous : title.Text.Trim();
                try { await Task.Run(() => archive.Save(saved)); }
                catch { saved.Title = previous; throw; }
                await RefreshArchive();
                Text("CaseTitle", saved.Title);
                Status("Capture saved to the local archive");
            });
        }

        private async Task Export()
        {
            var item = current;
            if (item == null) return;
            var dialog = Dialog("Export HTML report", 465);
            var form = (StackPanel)dialog.Content;
            form.Children.Add(new TextBlock { Text = "The shareable summary includes processing status, event numbers, and UTC times.",
                Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            var details = new CheckBox { Content = "Include original messages, paths and policy names", Foreground = Brushes.White };
            form.Children.Add(details);
            var before = comparedBefore;
            var after = comparedAfter;
            var comparison = new CheckBox { Content = "Include the selected comparison", IsChecked = true,
                IsEnabled = before != null && after != null, Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) };
            form.Children.Add(comparison);
            AddDialogButtons(dialog, new Button { Content = "Choose save location", IsDefault = true });
            if (dialog.ShowDialog() != true) return;
            var picker = new SaveFileDialog { AddExtension = true, DefaultExt = ".html", Filter = "HTML report (*.html)|*.html",
                FileName = "GPOCasebook-" + item.CapturedUtc.Substring(0, 10) + ".html" };
            if (picker.ShowDialog(View) != true) return;
            bool includeDetails = details.IsChecked == true;
            bool includeComparison = comparison.IsEnabled && comparison.IsChecked == true;
            await Run("Saving the HTML report…", async () =>
            {
                await Task.Run(() => HtmlReport.Save(picker.FileName, item,
                    includeComparison ? before : null, includeComparison ? after : null, includeDetails));
                Status("Report saved as " + Path.GetFileName(picker.FileName));
            });
        }

        private Window Dialog(string title, int width, bool owned = true)
        {
            var form = new StackPanel { Margin = new Thickness(20) };
            return new Window { Owner = owned ? View : null, Title = title, Width = width, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = BrushOf("#121D2B"), Foreground = Brushes.White, FontSize = 13,
                ShowInTaskbar = false, Content = form };
        }

        private static void AddDialogButtons(Window dialog, Button confirm)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 17, 0, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(13, 6, 13, 6),
                Margin = new Thickness(0, 0, 8, 0) };
            confirm.Padding = new Thickness(13, 6, 13, 6);
            confirm.Click += (sender, e) => dialog.DialogResult = true;
            row.Children.Add(cancel);
            row.Children.Add(confirm);
            ((StackPanel)dialog.Content).Children.Add(row);
        }

        private async Task Run(string state, Func<Task> action)
        {
            if (busy) return;
            busy = true;
            Status(state);
            UpdateActions();
            try { await action(); }
            catch (Exception e)
            {
                Status("The operation could not be completed");
                MessageBox.Show(View, e.Message, "GPOCasebook", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { busy = false; UpdateActions(); }
        }

        private async Task RefreshArchive()
        {
            int skipped = 0;
            entries = await Task.Run(() => archive.List(out skipped));
            unreadable = skipped;
            RefreshList();
            if (current != null) ResetComparison();
        }

        private void RefreshList()
        {
            string query = At<TextBox>("ArchiveSearch").Text.Trim();
            var visible = entries.Where(entry => entry.Display.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            updating = true;
            var list = At<ListBox>("ArchiveList");
            list.ItemsSource = visible;
            list.SelectedItem = current == null ? null : visible.FirstOrDefault(entry => entry.Id == current.Id);
            updating = false;
            string count = visible.Count == 0 && entries.Count == 0 ? "No saved captures" : visible.Count + " saved capture(s)";
            Text("ArchiveCount", count + (unreadable > 0 ? "\n" + unreadable + " unreadable file(s)" : ""));
        }

        internal void Show(CaseData data)
        {
            current = data;
            Text("CaseTitle", data.Title);
            Text("CaseSource", data.Source);
            Text("CaseMetadata", data.Host + "   ·   " + data.CapturedUtc.Substring(0, 16).Replace('T', ' ') + " UTC");
            List<PolicyRun> runs = PolicyRun.From(data);
            SetScope(runs, "Computer", "ComputerStatus");
            SetScope(runs, "User", "UserStatus");
            Text("EventCount", data.Events.Count.ToString(CultureInfo.InvariantCulture) +
                "  ·  " + data.Events.Count(e => e.IsError) + " errors");
            var warnings = At<TextBlock>("Warnings");
            warnings.Text = String.Join("  ", data.Notes.Distinct());
            warnings.Visibility = warnings.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            var picker = At<ComboBox>("RunPicker");
            picker.ItemsSource = runs;
            picker.SelectedItem = runs.FirstOrDefault(r => r.Events.Any(e => e.IsError)) ?? runs.FirstOrDefault();
            SelectRun();
            At<DataGrid>("PolicyGrid").ItemsSource = data.Policies;
            Text("PolicyHint", data.Policies.Count == 0 ? "No RSoP policy records are available for this capture." :
                "The RSoP snapshot reports resultant settings separately from the event timeline.");
            At<TabControl>("Tabs").SelectedIndex = 0;
            RefreshList();
            ResetComparison();
            UpdateActions();
        }

        private void SetScope(List<PolicyRun> runs, string scope, string name)
        {
            var recent = runs.FirstOrDefault(x => x.Scope == scope);
            string state = recent == null ? "Unavailable" : recent.Outcome;
            var text = At<TextBlock>(name);
            text.Text = state;
            text.ToolTip = recent == null ? "No run with this scope was identified" : recent.Label;
            text.Foreground = state.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ? Error :
                state.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0 ? Warm :
                state == "Completed" ? Success : Muted;
        }

        private void SelectRun()
        {
            var run = At<ComboBox>("RunPicker").SelectedItem as PolicyRun;
            var timeline = At<DataGrid>("Timeline");
            timeline.ItemsSource = run == null ? null : run.Events;
            timeline.SelectedItem = run == null ? null : run.Events.FirstOrDefault(e => e.IsError) ?? run.Events[0];
            ShowEvent(timeline.SelectedItem as PolicyEvent);
        }

        private void ShowEvent(PolicyEvent e)
        {
            Text("EventHeadline", e == null ? "No event selected" : e.Summary);
            Text("EventProvenance", e == null ? "" : (e.Channel == Evidence.Operational ? "GroupPolicy/Operational" : e.Channel) +
                "   ·   Event " + e.Id + "   ·   Record " + (e.Record == 0 ? "unavailable" : e.Record.ToString()) +
                "\n" + e.Utc + (e.Activity.Length == 0 ? "\nActivity ID unavailable" : "\nActivity " + e.Activity));
            Text("EventMessage", e == null ? "" : String.IsNullOrEmpty(e.Message) ? "Windows message unavailable. Recorded fields appear below." : e.Message);
            At<ItemsControl>("EventFields").ItemsSource = e == null ? null : e.Fields;
        }

        private void ResetComparison()
        {
            var earlier = At<ComboBox>("BeforePicker").SelectedItem as ArchiveEntry;
            var later = At<ComboBox>("AfterPicker").SelectedItem as ArchiveEntry;
            var options = new List<ArchiveEntry> { new ArchiveEntry(current) };
            options.AddRange(entries.Where(item => item.Id != current.Id));
            updating = true;
            var before = At<ComboBox>("BeforePicker");
            var after = At<ComboBox>("AfterPicker");
            before.ItemsSource = options;
            after.ItemsSource = options;
            before.SelectedItem = earlier == null ? null : options.FirstOrDefault(item => item.Id == earlier.Id);
            after.SelectedItem = later == null ? options[0] :
                options.FirstOrDefault(item => item.Id == later.Id) ?? options[0];
            updating = false;
            if (comparedBefore == null || comparedAfter == null ||
                (before.SelectedItem as ArchiveEntry)?.Id != comparedBefore.Id ||
                (after.SelectedItem as ArchiveEntry)?.Id != comparedAfter.Id)
                ShowComparison(null, null);
        }

        private async Task UpdateComparison()
        {
            if (updating || busy || current == null) return;
            var left = At<ComboBox>("BeforePicker").SelectedItem as ArchiveEntry;
            var right = At<ComboBox>("AfterPicker").SelectedItem as ArchiveEntry;
            if (left == null || right == null || left.Id == right.Id)
            {
                ShowComparison(null, null);
                return;
            }
            await Run("Comparing captures…", async () =>
            {
                ShowComparison(await Task.Run(() => Resolve(left.Id)), await Task.Run(() => Resolve(right.Id)));
                Status("Comparison ready");
            });
        }

        private CaseData Resolve(Guid id)
        {
            if (current.Id == id) return current;
            return archive.Load(id);
        }

        internal void ShowComparison(CaseData before, CaseData after)
        {
            comparedBefore = before;
            comparedAfter = after;
            var grid = At<DataGrid>("CompareGrid");
            if (before == null || after == null)
            {
                Text("CompareHint", "Choose two distinct captures to compare their latest runs and RSoP observations.");
                grid.ItemsSource = null;
                return;
            }
            var context = ComparisonRow.SourceContext(before, after);
            if (String.CompareOrdinal(before.CapturedUtc, after.CapturedUtc) > 0)
                context += " The Before capture has a later capture time.";
            Text("CompareHint", context + " A GPO missing from one report is marked Not in report.");
            grid.ItemsSource = ComparisonRow.Build(before, after);
        }

        private void UpdateActions()
        {
            foreach (string name in new[] { "DomainButton", "CaptureButton", "ImportButton", "OpenArchiveButton",
                "RefreshArchiveButton", "HelpButton", "AboutButton" })
                At<Button>(name).IsEnabled = !busy;
            At<ListBox>("ArchiveList").IsEnabled = !busy;
            At<ComboBox>("BeforePicker").IsEnabled = !busy && current != null;
            At<ComboBox>("AfterPicker").IsEnabled = !busy && current != null;
            bool saved = current != null && entries.Any(x => x.Id == current.Id);
            var button = At<Button>("ArchiveButton");
            button.Content = saved ? "In archive" : "Save to archive";
            button.IsEnabled = !busy && current != null && !saved && (current.Events.Count > 0 || current.Policies.Count > 0);
            At<Button>("ExportButton").IsEnabled = !busy && current != null && (current.Events.Count > 0 || current.Policies.Count > 0);
        }

        private static Brush BrushOf(string color)
        {
            var brush = (Brush)new BrushConverter().ConvertFromInvariantString(color);
            brush.Freeze();
            return brush;
        }
    }
}
