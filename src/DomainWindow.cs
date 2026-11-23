using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace GPOCasebook
{
    internal sealed class DomainComputerRow : INotifyPropertyChanged
    {
        private bool included;
        private bool canSelect = true;
        private DomainReading reading;
        public string Computer { get; private set; }
        public bool Included { get { return included; } set { if (included != value) { included = value; Changed(nameof(Included)); } } }
        public bool CanSelect { get { return canSelect; } set { canSelect = value; Changed(nameof(CanSelect)); } }
        public DomainReading Reading { get { return reading; } set { reading = value; Changed(nameof(Reading)); } }
        public event PropertyChangedEventHandler PropertyChanged;

        public DomainComputerRow(string computer)
        {
            Computer = computer;
            Reading = new DomainReading(computer, "Ready", "");
        }

        private void Changed(string name) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    }

    internal sealed class DomainWindow
    {
        private readonly Window window;
        private readonly Window owner;
        private readonly Archive archive;
        private readonly string packageFamily;
        private List<DomainComputerRow> rows = new List<DomainComputerRow>();
        private ICollectionView visible;
        private string discoveredDomain;
        private CancellationTokenSource running;
        private int selected;
        private bool bulkSelection;
        internal Window View { get { return window; } }

        public DomainWindow(Window owner, Archive archive, string packageFamily = null)
        {
            this.owner = owner;
            this.archive = archive;
            this.packageFamily = packageFamily;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DomainWindow.xaml"))
                window = (Window)XamlReader.Load(stream);
            window.Resources.MergedDictionaries.Add(owner.Resources);
            window.ShowInTaskbar = false;

            At<Button>("Account").Click += (sender, e) => ChangeAccount();
            At<Button>("Find").Click += async (sender, e) => await Find();
            At<Button>("Start").Click += async (sender, e) => await Scan();
            At<Button>("Stop").Click += (sender, e) => Stop();
            At<Button>("Reports").Click += (sender, e) => OpenReports();
            At<Button>("SelectAll").Click += (sender, e) => SetSelection(visible.Cast<DomainComputerRow>(), true);
            At<Button>("Clear").Click += (sender, e) => SetSelection(rows, false);
            At<TextBox>("Filter").TextChanged += (sender, e) => { visible.Refresh(); UpdateControls(); };
            At<TextBox>("DomainName").TextChanged += (sender, e) =>
            {
                if (discoveredDomain == null) return;
                SetComputers(new string[0], null);
                Status("Find computers to use the updated domain.");
            };
            At<DataGrid>("Computers").SelectionChanged += (sender, e) => ShowDetails();
            window.Closing += (sender, e) =>
            {
                if (running == null) return;
                e.Cancel = true;
                Stop();
            };
            SetComputers(new string[0], null);
            Status("Leave the domain blank to use your current Windows domain.");
        }

        public void Show() { window.Owner = owner; window.ShowDialog(); }
        private T At<T>(string name) where T : class { return (T)window.FindName(name); }
        private void Status(string text) { At<TextBlock>("Status").Text = text; }

        internal void SetComputers(string[] hosts, string domain)
        {
            discoveredDomain = domain;
            selected = 0;
            rows = hosts.Select(host => new DomainComputerRow(host)).ToList();
            foreach (var row in rows)
            {
                row.PropertyChanged += (sender, e) =>
                {
                    if (e.PropertyName == nameof(DomainComputerRow.Included))
                    {
                        selected += row.Included ? 1 : -1;
                        if (!bulkSelection) UpdateControls();
                    }
                };
            }
            visible = CollectionViewSource.GetDefaultView(rows);
            visible.Filter = item => ((DomainComputerRow)item).Computer.IndexOf(At<TextBox>("Filter").Text.Trim(),
                StringComparison.OrdinalIgnoreCase) >= 0;
            At<DataGrid>("Computers").ItemsSource = visible;
            ShowDetails();
            UpdateControls();
        }

        private void SetSelection(IEnumerable<DomainComputerRow> targets, bool value)
        {
            bulkSelection = true;
            foreach (var row in targets) row.Included = value;
            bulkSelection = false;
            UpdateControls();
        }

        private async Task Find()
        {
            string domain = At<TextBox>("DomainName").Text.Trim();
            await Run("Reading the Active Directory computer list…", async cancellation =>
            {
                DomainComputers inventory = await Task.Run(() => DomainDirectory.Find(domain, cancellation));
                SetComputers(inventory.Hosts, inventory.Domain);
                Status(inventory.Hosts.Length == 0 ? "No enabled computers with usable DNS names were returned." :
                    "Choose the computers to scan in " + inventory.Domain + ".");
            });
        }

        private async Task Scan()
        {
            string domain = discoveredDomain;
            var targets = rows.Where(row => row.Included).ToDictionary(row => row.Computer, StringComparer.OrdinalIgnoreCase);
            if (targets.Count == 0 || domain == null) return;
            if (MessageBox.Show(window, "Read Group Policy evidence on " + targets.Count + " selected computer(s) in " + domain +
                "? Captures and an internal scan summary will be saved to the local archive.",
                "Start domain scan", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            await Run("Reading Group Policy evidence…", async cancellation =>
            {
                DateTimeOffset started = DateTimeOffset.UtcNow;
                foreach (var row in targets.Values) row.Reading = new DomainReading(row.Computer, "Queued", "");
                int completed = 0;
                var progress = new Progress<DomainReading>(reading =>
                {
                    if (running == null) return;
                    targets[reading.Computer].Reading = reading;
                    completed++;
                    if (!cancellation.IsCancellationRequested)
                        Status("Processed " + completed + " of " + targets.Count + " computers.");
                    ShowDetails();
                });
                DomainReading[] results = await DomainScanner.Scan(targets.Keys.ToArray(), archive, Capture.Remote, progress, cancellation);
                foreach (DomainReading result in results) targets[result.Computer].Reading = result;
                ShowDetails();
                int saved = results.Count(result => result.CaseId.HasValue);
                int cancelled = results.Count(result => result.Status == "Cancelled");
                string summary = saved + " captures saved · " + (results.Length - saved - cancelled) +
                    " without a capture" + (cancelled == 0 ? "" : " · " + cancelled + " cancelled");
                Status(summary + ". Saving the scan summary…");
                try
                {
                    await Task.Run(() => DomainScanner.SaveSummary(archive, domain, started, results));
                    Status(summary + ". Summary saved in Scan reports.");
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Status(summary + ". The scan summary could not be saved.");
                }
            });
        }

        private async Task Run(string message, Func<CancellationToken, Task> action)
        {
            if (running != null) return;
            using (var cancellation = new CancellationTokenSource())
            {
                running = cancellation;
                SetEditable(false);
                Status(message);
                try { await action(cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Status("Operation cancelled."); }
                catch (Exception error)
                {
                    Status("The operation could not be completed.");
                    MessageBox.Show(window, error.Message, "GPOCasebook", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    running = null;
                    SetEditable(true);
                }
            }
        }

        private void Stop()
        {
            if (running == null || running.IsCancellationRequested) return;
            Status("Stopping. Waiting for pending Windows requests to finish…");
            running.Cancel();
            UpdateControls();
        }

        private void SetEditable(bool enabled)
        {
            foreach (var row in rows) row.CanSelect = enabled;
            UpdateControls();
        }

        private void UpdateControls()
        {
            bool idle = running == null;
            At<Button>("Find").IsEnabled = idle;
            At<Button>("Account").IsEnabled = idle;
            At<TextBox>("DomainName").IsEnabled = idle;
            At<Button>("SelectAll").IsEnabled = idle && visible != null && !visible.IsEmpty;
            At<Button>("Clear").IsEnabled = idle && selected != 0;
            At<Button>("Start").IsEnabled = idle && selected != 0;
            At<Button>("Stop").IsEnabled = !idle && !running.IsCancellationRequested;
            At<TextBlock>("Counts").Text = rows.Count + " discovered · " + selected + " selected";
        }

        private void ShowDetails()
        {
            var row = At<DataGrid>("Computers").SelectedItem as DomainComputerRow;
            At<TextBlock>("SelectedDetails").Text = row == null ? "Select a row to see its collection details." :
                row.Computer + " · " + row.Reading.Status +
                (String.IsNullOrWhiteSpace(row.Reading.Detail) ? "" : "\n" + row.Reading.Detail);
        }

        private void OpenReports()
        {
            try
            {
                Directory.CreateDirectory(archive.DomainReportsPath);
                Process.Start(new ProcessStartInfo(archive.DomainReportsPath) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(window, error.Message, "Scan reports", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ChangeAccount()
        {
            var dialog = new Window
            {
                Owner = window, Title = "Use another Windows account", Width = 490, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
                Background = window.Background, Foreground = window.Foreground, FontFamily = window.FontFamily, FontSize = 13
            };
            dialog.Resources.MergedDictionaries.Add(window.Resources);
            var form = new StackPanel { Margin = new Thickness(18) };
            form.Children.Add(new TextBlock { Text = "Type the domain account name. Enter its password in the Windows Run as prompt.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var account = new TextBox { MaxLength = 256, ToolTip = @"DOMAIN\username or username@domain" };
            System.Windows.Automation.AutomationProperties.SetName(account, "Domain account");
            form.Children.Add(account);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 13, 0, 0) };
            row.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 7, 0) });
            var proceed = new Button { Content = "Open Windows prompt", IsDefault = true };
            row.Children.Add(proceed);
            form.Children.Add(row);
            dialog.Content = form;
            dialog.Loaded += (sender, e) => account.Focus();
            proceed.Click += (sender, e) =>
            {
                try
                {
                    WindowsAccount.OpenDomainWindow(account.Text.Trim(), packageFamily);
                    dialog.DialogResult = true;
                }
                catch (Exception error)
                {
                    MessageBox.Show(dialog, error.Message, "Windows account", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            if (dialog.ShowDialog() == true)
                Status("Enter the password in Windows Run as. A separate domain scan window will open.");
        }
    }
}
