using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

namespace GPOCasebook
{
    internal static class Examples
    {
        internal static CaseData Sample(bool after)
        {
            string kind = after ? "after" : "before";
            var result = CaseData.Create(after ? "After repair" : "LocalGPO registry error", "Sample data");
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("demo-" + kind + ".xml"))
            {
                result.Events = Evidence.LatestEvents(XDocument.Load(stream).Root.Elements()
                    .Select(e => Evidence.ParseEvent(e, null)).Where(e => e != null));
            }
            result.CapturedUtc = result.Events.Last().Utc;
            result.Policies.Add(new PolicyRecord { Name = "Local Group Policy", Scope = "Computer",
                State = after ? "Applied" : "Listed", Reason = after ? "Sample RSoP records an application order." :
                    "Sample RSoP does not record successful application." });
            result.Policies.Add(new PolicyRecord { Name = "Workstation preferences", Scope = "User",
                State = "Applied", Reason = "Sample RSoP records an application order." });
            return result;
        }

        internal static void Render(string destination, string archiveRoot)
        {
            Directory.CreateDirectory(destination);
            var app = new Application();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var archive = new Archive(archiveRoot);
            var workbench = new Workbench(archive);
            var before = Sample(false);
            workbench.Show(before);
            RenderWindow(workbench.View, (FrameworkElement)workbench.View.Content, destination, "timeline.png", 1600, 900);

            var after = Sample(true);
            archive.Save(before);
            archive.Save(after);
            var options = new[] { new ArchiveEntry(before), new ArchiveEntry(after) };
            var earlier = (ComboBox)workbench.View.FindName("BeforePicker");
            var later = (ComboBox)workbench.View.FindName("AfterPicker");
            earlier.ItemsSource = options;
            later.ItemsSource = options;
            earlier.SelectedIndex = 0;
            later.SelectedIndex = 1;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!later.IsEnabled && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            }
            if (!later.IsEnabled) throw new InvalidOperationException("The comparison could not be opened for its screenshot.");
            workbench.ShowComparison(before, after);
            ((TabControl)workbench.View.FindName("Tabs")).SelectedIndex = 2;
            RenderWindow(workbench.View, (FrameworkElement)workbench.View.Content, destination, "comparison.png", 1600, 900);

            var domain = new DomainWindow(workbench.View, archive);
            string[] hosts = { "lab-dc01.example.test", "lab-win11.example.test", "lab-win12.example.test" };
            ((TextBox)domain.View.FindName("DomainName")).Text = "example.test";
            domain.SetComputers(hosts, "example.test");
            var grid = (DataGrid)domain.View.FindName("Computers");
            var rows = grid.Items.Cast<DomainComputerRow>().ToArray();
            rows[0].Reading = new DomainReading(hosts[0], "Saved", "Capture saved to the archive.", Sample(true));
            var partial = Sample(false);
            partial.Policies.Clear();
            rows[1].Reading = new DomainReading(hosts[1], "Partial", "Computer RSoP could not be read with this account.", partial);
            rows[2].Reading = new DomainReading(hosts[2], "Unavailable", "Event Log and Computer RSoP could not connect.");
            foreach (var row in rows) row.Included = true;
            grid.SelectedItem = rows[1];
            ((TextBlock)domain.View.FindName("Status")).Text = "Sample scan · 2 captures saved · 1 without a capture";
            RenderWindow(domain.View, (FrameworkElement)domain.View.Content, destination, "domain-scan.png", 1600, 900);
        }

        private static void RenderWindow(Window window, FrameworkElement content, string destination, string name, int width, int height)
        {
            if (content is Grid grid) grid.Background = window.Background;
            var size = new Size(width, height);
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(destination, name))) encoder.Save(stream);
        }

    }
}
