using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;

namespace GPOCasebook
{
    internal static class Tests
    {
        private static int passed;
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "GPOCasebook-tests-" + Guid.NewGuid().ToString("N"));

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--render-docs")
            {
                Examples.Render(args[1], Root);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--check-local")
            {
                var capture = Capture.Local();
                Console.WriteLine("Local Windows collection finished. Events: {0}. RSoP records: {1}.",
                    capture.Events.Count, capture.Policies.Count);
                foreach (var note in capture.Notes) Console.WriteLine(note);
                return 0;
            }
            Directory.CreateDirectory(Root);
            try
            {
                Test("LocalGPO fields and activity correlation", DemoEvidence);
                Test("Different computers and uncorrelated records", Correlation);
                Test("Duplicate records and cross-channel identity", Deduplicate);
                Test("Ignore events from other providers", OtherProviders);
                Test("Reject DTDs in imported XML", RejectDtd);
                Test("Read scoped RSoP and distinguish recorded states", ParseRsop);
                Test("Compare the failing and healthy captures", Compare);
                Test("Partial evidence preserves unknown completion", Partial);
                Test("Recognize periodic Windows policy completion", Periodic);
                Test("Archive survives reopen and blocks duplicate writes", RoundTrip);
                Test("An invalid archive file does not hide valid cases", InvalidArchive);
                Test("A pre-domain-scan archive still opens", LegacyArchive);
                Test("HTML encodes event details and omits them by default", Export);
                Test("Saving a second export can replace the output file", ReplaceReport);
                Test("Domain and host fields accept only DNS names", DomainNames);
                Test("Remote RSoP arguments use integrated Windows credentials", RemoteRsop);
                Test("Windows account prompt arguments survive spaces", RunAsQuoting);
                Test("Packaged Windows sessions use the same archive as their parent", PackageArchive);
                Test("Domain scan archives completed and partial captures", ScanArchive);
                Test("Cancelled scans preserve saved files and leave targets queued", ScanCancel);
                Console.WriteLine("{0} tests passed.", passed);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void Test(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void Require(bool passes, string description)
        {
            if (!passes) throw new InvalidOperationException(description);
        }

        private static void DemoEvidence()
        {
            var sample = Examples.Sample(false);
            var runs = PolicyRun.From(sample);
            Require(runs.Count == 2, "Expected two manual processing activities.");
            var computer = runs.Single(r => r.Scope == "Computer");
            var user = runs.Single(r => r.Scope == "User");
            var error = computer.Events.Single(e => e.Id == 1096);
            Require(error.Channel == "System" && error.Field("GPOCNName") == "LocalGPO", "Expected retained source evidence.");
            Require(error.Field("FilePath").EndsWith(@"Machine\Registry.pol") && error.Field("ErrorCode") == "13",
                "Expected the original path and error code.");
            Require(computer.Outcome == "Error recorded" && user.Outcome == "Completed", "The two scopes have different outcomes.");
        }

        private static void Correlation()
        {
            var sample = Examples.Sample(false);
            var original = sample.Events.First(e => e.Id == 4004);
            var differentHost = Examples.Sample(false).Events.First(e => e.Id == 4004);
            differentHost.Computer = "OTHER-PC";
            var noActivity = Examples.Sample(false).Events.First(e => e.Id == 1096);
            noActivity.Activity = "";
            sample.Events.Clear();
            sample.Events.AddRange(new[] { original, differentHost, noActivity });
            var runs = PolicyRun.From(sample);
            Require(runs.Count == 3, "Activity IDs must not merge different computers or unmatched events.");
            Require(runs.Any(r => r.Label.Contains("Uncorrelated")), "The trace should mark unmatched evidence.");
        }

        private static void Deduplicate()
        {
            var a = Examples.Sample(false).Events.First(e => e.Id == 1096);
            var b = Examples.Sample(false).Events.First(e => e.Id == 1096);
            var c = Examples.Sample(false).Events.First(e => e.Id == 1096);
            c.Channel = Evidence.Operational;
            Require(Evidence.LatestEvents(new[] { a, b, c }).Count == 2, "Independent logs use independent record numbers.");
        }

        private static void OtherProviders()
        {
            const string xml = @"<Event xmlns=""http://schemas.microsoft.com/win/2004/08/events/event"">
              <System><Provider Name=""OtherProvider""/><EventID>1096</EventID><Channel>System</Channel>
              <TimeCreated SystemTime=""2026-11-21T07:00:00Z""/></System></Event>";
            Require(Evidence.ParseEvent(xml, null) == null, "The provider determines whether the event is allowed.");
        }

        private static void RejectDtd()
        {
            var path = Path.Combine(Root, "dtd.xml");
            File.WriteAllText(path, @"<!DOCTYPE Rsop [<!ENTITY data SYSTEM ""file:///C:/Windows/win.ini"">]><Rsop>&data;</Rsop>");
            try { Evidence.ReadRsop(path); }
            catch (XmlException) { return; }
            throw new InvalidOperationException("Import accepted a document type.");
        }

        private static void ParseRsop()
        {
            var path = Path.Combine(Root, "rsop.xml");
            File.WriteAllText(path, @"<Rsop xmlns=""http://www.microsoft.com/GroupPolicy/Rsop"">
              <ComputerResults><GPO><Name>Baseline</Name><AppliedOrder>1</AppliedOrder></GPO>
                <GPO><Name>Filtered policy</Name><FilterAllowed>false</FilterAllowed></GPO>
                <GPO><Name>Reserved policy</Name><AccessDenied>true</AccessDenied></GPO>
              </ComputerResults><UserResults><GPO><Name>Partial</Name><AppliedOrder>0</AppliedOrder></GPO></UserResults></Rsop>");
            var sample = Capture.Import(new string[0], path, "RSoP only");
            Require(sample.Policies.Count == 4 && sample.Events.Count == 0, "RSoP-only captures must open.");
            Require(sample.Policies.Single(p => p.Name == "Baseline").State == "Applied", "Applied order is evidence.");
            Require(sample.Policies.Single(p => p.Name == "Filtered policy").State == "Filtered", "The filter was recorded.");
            Require(sample.Policies.Single(p => p.Name == "Reserved policy").State == "Denied", "Denial must remain visible.");
            Require(sample.Policies.Single(p => p.Name == "Partial").State == "Listed", "An unknown result must stay unknown.");
        }

        private static void Compare()
        {
            var before = Examples.Sample(false);
            var after = Examples.Sample(true);
            after.Policies.RemoveAll(p => p.Scope == "User");
            var rows = ComparisonRow.Build(before, after);
            Require(rows[0].Before == "Error recorded" && rows[0].After == "Completed", "Computer processing recovered.");
            Require(rows[1].Before == rows[1].After, "The User runs both completed.");
            Require(rows.Single(p => p.Item.EndsWith("Workstation preferences")).After == "Not in report",
                "Missing policy evidence cannot be called removed.");
        }

        private static void Partial()
        {
            var item = Examples.Sample(true);
            item.Events.RemoveAll(e => e.Id == 8004);
            Require(PolicyRun.From(item).Single(r => r.Scope == "Computer").Outcome == "Completion unavailable",
                "An extension success is insufficient to infer overall completion.");
        }

        private static void Periodic()
        {
            var item = Examples.Sample(true);
            foreach (var e in item.Events)
            {
                if (e.Id == 4004 || e.Id == 4005 || e.Id == 8004 || e.Id == 8005)
                    e.Id += 2;
            }
            var runs = PolicyRun.From(item);
            Require(runs.Any(r => r.Scope == "Computer" && r.Outcome == "Completed"), "Expected periodic Computer completion.");
            Require(runs.Any(r => r.Scope == "User" && r.Outcome == "Completed"), "Expected periodic User completion.");
        }

        private static void RoundTrip()
        {
            var path = Path.Combine(Root, "archive");
            var item = Examples.Sample(false);
            new Archive(path).Save(item);
            var reopened = new Archive(path);
            var loaded = reopened.Load(item.Id);
            Require(loaded.Title == item.Title && loaded.Events.Count == 7 && loaded.Policies.Count == 2, "Archive lost evidence.");
            item.Title = "Replacement";
            bool denied = false;
            try { reopened.Save(item); }
            catch (IOException) { denied = true; }
            Require(denied && reopened.Load(item.Id).Title == loaded.Title, "Duplicate saves must preserve existing cases.");
            Require(!Directory.EnumerateFiles(path, "*.tmp").Any(), "A normal duplicate failure must clean its staging file.");
        }

        private static void InvalidArchive()
        {
            var path = Path.Combine(Root, "archive");
            File.WriteAllText(Path.Combine(path, Guid.NewGuid().ToString("N") + ".json"), "{ invalid");
            File.WriteAllText(Path.Combine(path, Guid.NewGuid().ToString("N") + ".json"), @"{""Version"":999}");
            int unreadable;
            var results = new Archive(path).List(out unreadable);
            Require(unreadable == 2 && results.Count == 1, "The archive must retain valid entries when some files are damaged.");
        }

        private static void LegacyArchive()
        {
            string directory = Path.Combine(Root, "legacy");
            var archive = new Archive(directory);
            var item = Examples.Sample(false);
            archive.Save(item);
            string path = Path.Combine(directory, item.Id.ToString("N") + ".json");
            string legacy = File.ReadAllText(path).Replace(",\"TargetComputer\":null", "");
            Require(!legacy.Contains("\"TargetComputer\""), "The fixture must have the earlier format.");
            File.WriteAllText(path, legacy);
            Require(archive.Load(item.Id).Host == "LAB-WIN11", "The previous version's reports must remain readable.");
        }

        private static void Export()
        {
            var item = Examples.Sample(false);
            item.Title = "InternalTitle";
            var error = item.Events.First(x => x.Id == 1096);
            error.Message = "<script>alert('Confidential')</script>";
            error.Fields.Add(new EventField { Name = "Windows user", Value = "PrivatePersonName" });
            var full = HtmlReport.Render(item, item, Examples.Sample(true), true);
            var shared = HtmlReport.Render(item, item, Examples.Sample(true), false);
            Require(full.Contains("&lt;script&gt;") && !full.Contains("<script>") && full.Contains("PrivatePersonName"), "Full HTML must encode untrusted fields.");
            Require(!shared.Contains("InternalTitle") && !shared.Contains("Confidential") && !shared.Contains("PrivatePersonName") &&
                !shared.Contains("LAB-WIN11") && !shared.Contains("Workstation preferences") &&
                !shared.Contains(@"Machine\Registry.pol"), "Shareable HTML exposed an internal field.");
            Require(shared.Contains(">1096<") && shared.Contains("Capture comparison"), "Shareable HTML must keep diagnostic evidence.");
        }

        private static void ReplaceReport()
        {
            string path = Path.Combine(Root, "report.html");
            var sample = Examples.Sample(false);
            HtmlReport.Save(path, sample, null, null, true);
            HtmlReport.Save(path, sample, null, null, false);
            Require(File.ReadAllText(path).Contains("Shareable summary"), "Updating a report must replace the selected file.");
        }

        private static void DomainNames()
        {
            Require(DomainDirectory.IsDnsName("ws-17.lab.example.test"), "Expected a valid host name.");
            Require(DomainDirectory.DomainFromContext("DC=lab,DC=example,DC=test") == "lab.example.test",
                "Expected the domain DNS name from the directory context.");
            foreach (string input in new[] { "lab.test/RootDSE", "lab..test", "10.0.0.10", "*.test", "-lab.test", "lab.test /u" })
                Require(!DomainDirectory.IsDnsName(input), "The directory and remote queries must receive host names only.");
        }

        private static void RemoteRsop()
        {
            Require(Capture.RsopArguments("ws.casebook.test", @"C:\Evidence folder\result.xml") ==
                "/s ws.casebook.test /scope computer /x \"C:\\Evidence folder\\result.xml\"",
                "The Computer RSoP query should use the process's Windows identity.");
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static void RunAsQuoting()
        {
            const string path = @"C:\Program Files\GPOCasebook\GPOCasebook.exe";
            const string username = @"LAB\Network Reader";
            int count;
            IntPtr args = CommandLineToArgvW("runas.exe " + WindowsAccount.RunAsArguments(username, path), out count);
            Require(args != IntPtr.Zero, "Windows could not parse the launch arguments.");
            try
            {
                var words = Enumerable.Range(0, count)
                    .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, index * IntPtr.Size))).ToArray();
                Require(words.SequenceEqual(new[] { "runas.exe", "/netonly", "/user:" + username, "\"" + path + "\" --domain" }),
                    "The alternate Windows identity must not alter the executable or its arguments.");
            }
            finally { LocalFree(args); }
        }

        private static void ScanArchive()
        {
            var archive = new Archive(Path.Combine(Root, "domain"));
            string[] hosts = { "complete.example.test", "partial.example.test", "offline.example.test" };
            var results = DomainScanner.Scan(hosts, archive, (host, cancellation) =>
            {
                var item = CaseData.Create("Domain capture · " + host, "Domain scan");
                item.TargetComputer = host;
                if (host == hosts[2]) item.Notes.Add("RPC unavailable.");
                else
                {
                    item.Policies.Add(new PolicyRecord { Name = "Baseline", Scope = "Computer", State = "Applied", Reason = "RSoP" });
                    if (host == hosts[1]) item.Notes.Add("Event Log unavailable. <script>");
                }
                return item;
            }, null, CancellationToken.None).GetAwaiter().GetResult();
            Require(results.Select(r => r.Status).SequenceEqual(new[] { "Saved", "Partial", "Unavailable" }), "Each computer needs its own status.");
            Require(results[0].CaseId.HasValue && results[1].CaseId.HasValue && !results[2].CaseId.HasValue,
                "Only collected evidence belongs in the archive.");
            Require(archive.Load(results[1].CaseId.Value).Host == hosts[1], "RSoP-only captures should keep their computer name.");
            string summary = DomainScanner.SaveSummary(archive, "example.test", DateTimeOffset.UtcNow, results);
            string html = File.ReadAllText(summary);
            Require(html.Contains(hosts[2]) && html.Contains("&lt;script&gt;") && !html.Contains("<script>"),
                "The scan report must include offline results and encode diagnostic text.");
        }

        private static void PackageArchive()
        {
            const string family = "AlirezaForoughifar.GPOCasebook_a1b2c3d4e5f6g";
            string profile = Path.Combine(Root, "profile");
            Require(PackageIdentity.ArchiveDirectory(profile, null) == Path.Combine(profile, "GPOCasebook", "Archive"),
                "The classic edition should still open existing archives.");
            Require(PackageIdentity.ArchiveDirectory(profile, family) ==
                Path.Combine(profile, "Packages", family, "LocalState", "Archive"),
                "The Store edition should store cases in the package's user data folder.");
            Require(!PackageIdentity.IsValidFamily("..\\OtherPackage"), "An arbitrary path is not a package family.");
            Require(WindowsAccount.RunAsArguments(@"LAB\reader", @"C:\Test\GPOCasebook.exe", family)
                .EndsWith(" --domain " + family + "\"", StringComparison.Ordinal),
                "A separate Windows identity needs the current edition's archive.");
        }

        private static void ScanCancel()
        {
            var archive = new Archive(Path.Combine(Root, "cancelled"));
            var baseline = Examples.Sample(false);
            archive.Save(baseline);
            using (var cancellation = new CancellationTokenSource())
            {
                var targets = Enumerable.Range(1, 16).Select(n => "pc" + n + ".example.test").ToArray();
                cancellation.Cancel();
                var results = DomainScanner.Scan(targets, archive, (host, token) =>
                {
                    throw new InvalidOperationException("A cancelled request tried to start a read.");
                }, null, cancellation.Token).GetAwaiter().GetResult();
                Require(results.Length == targets.Length && results.All(r => r.Status == "Cancelled" && !r.CaseId.HasValue),
                    "The scan should mark cancelled targets.");
                Require(archive.Load(baseline.Id).Events.Count > 0, "Cancellation must preserve previously saved captures.");
            }
        }
    }
}
