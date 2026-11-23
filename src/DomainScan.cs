using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.DirectoryServices;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GPOCasebook
{
    internal sealed class DomainComputers
    {
        public string Domain { get; private set; }
        public string[] Hosts { get; private set; }

        public DomainComputers(string domain, string[] hosts) { Domain = domain; Hosts = hosts; }
    }

    internal static class DomainDirectory
    {
        private const AuthenticationTypes DirectoryAuthentication =
            AuthenticationTypes.Secure | AuthenticationTypes.Signing | AuthenticationTypes.Sealing;
        private const string EnabledComputers =
            "(&(objectCategory=computer)(dNSHostName=*)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";

        public static bool IsDnsName(string value)
        {
            return !String.IsNullOrEmpty(value) && value.Length <= 253 &&
                Uri.CheckHostName(value) == UriHostNameType.Dns &&
                value.Split('.').All(label => label.Length <= 63 &&
                    Regex.IsMatch(label, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant));
        }

        public static DomainComputers Find(string domain, CancellationToken cancellation)
        {
            if (domain.Length != 0 && !IsDnsName(domain)) throw new ArgumentException("Enter the DNS name of an Active Directory domain.");
            cancellation.ThrowIfCancellationRequested();
            string ldap = domain.Length == 0 ? "LDAP://" : "LDAP://" + domain + "/";
            string namingContext;
            using (var root = new DirectoryEntry(ldap + "RootDSE", null, null, DirectoryAuthentication))
                namingContext = root.Properties["defaultNamingContext"].Value as string;
            cancellation.ThrowIfCancellationRequested();
            string discoveredDomain = DomainFromContext(namingContext);
            using (var entry = new DirectoryEntry(ldap + namingContext, null, null, DirectoryAuthentication))
            using (var query = new DirectorySearcher(entry, EnabledComputers, new[] { "dNSHostName" }, SearchScope.Subtree))
            {
                query.PageSize = 500;
                query.ClientTimeout = TimeSpan.FromSeconds(45);
                query.ReferralChasing = ReferralChasingOption.None;
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                using (SearchResultCollection results = query.FindAll())
                {
                    foreach (SearchResult result in results)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var property = result.Properties["dNSHostName"];
                        string hostname = property.Count == 0 ? null : property[0] as string;
                        if (IsDnsName(hostname)) names.Add(hostname);
                    }
                }
                return new DomainComputers(discoveredDomain, names.ToArray());
            }
        }

        internal static string DomainFromContext(string context)
        {
            string[] components = (context ?? "").Split(',');
            if (!components.All(component => component.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The directory did not return a usable Active Directory domain.");
            string domain = String.Join(".", components.Select(component => component.Substring(3)));
            if (!IsDnsName(domain)) throw new InvalidDataException("The Active Directory domain name could not be determined.");
            return domain;
        }
    }

    internal static class WindowsAccount
    {
        public static void OpenDomainWindow(string account, string packageFamily)
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            string arguments = RunAsArguments(account, executable, packageFamily);
            using (Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "runas.exe"), arguments)
            {
                UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Path.GetDirectoryName(executable)
            })) { }
        }

        internal static string RunAsArguments(string account, string executable, string packageFamily = null)
        {
            if (account.Length > 256 || !Regex.IsMatch(account,
                @"\A(?:[A-Za-z0-9.-]+\\[^""\\/@\p{C}]+|[^""\\/@\s\p{C}]+@[A-Za-z0-9.-]+)\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("Enter the account as DOMAIN\\username or username@domain.");
            if (packageFamily != null && !PackageIdentity.IsValidFamily(packageFamily))
                throw new ArgumentException("Invalid Windows package family.");
            string command = Quoted(executable) + " --domain" + (packageFamily == null ? "" : " " + packageFamily);
            return "/netonly " + Quoted("/user:" + account) + " " + Quoted(command);
        }

        private static string Quoted(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
    }

    internal sealed class DomainReading
    {
        public string Computer { get; private set; }
        public string Status { get; private set; }
        public string Detail { get; private set; }
        public int Events { get; private set; }
        public int Policies { get; private set; }
        public Guid? CaseId { get; private set; }

        public DomainReading(string computer, string status, string detail, CaseData evidence = null)
        {
            Computer = computer;
            Status = status;
            Detail = detail;
            Events = evidence == null ? 0 : evidence.Events.Count;
            Policies = evidence == null ? 0 : evidence.Policies.Count;
            CaseId = evidence == null ? (Guid?)null : evidence.Id;
        }
    }

    internal static class DomainScanner
    {
        internal const int ConcurrentComputers = 4;

        internal static async Task<DomainReading[]> Scan(string[] computers, Archive archive,
            Func<string, CancellationToken, CaseData> collect, IProgress<DomainReading> progress, CancellationToken cancellation)
        {
            var results = new DomainReading[computers.Length];
            int index = -1;
            var workers = Enumerable.Range(0, Math.Min(ConcurrentComputers, computers.Length)).Select(worker =>
                Task.Run(() =>
                {
                    int next;
                    while (!cancellation.IsCancellationRequested &&
                        (next = Interlocked.Increment(ref index)) < computers.Length)
                    {
                        string computer = computers[next];
                        results[next] = ReadOne(computer, archive, collect, cancellation);
                        if (progress != null) progress.Report(results[next]);
                    }
                })).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            for (int i = 0; i < results.Length; i++)
                if (results[i] == null) results[i] = Cancelled(computers[i]);
            return results;
        }

        private static DomainReading ReadOne(string computer, Archive archive,
            Func<string, CancellationToken, CaseData> collect, CancellationToken cancellation)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                CaseData evidence = collect(computer, cancellation);
                cancellation.ThrowIfCancellationRequested();
                string notes = String.Join(" ", evidence.Notes.Distinct());
                if (evidence.Events.Count == 0 && evidence.Policies.Count == 0)
                    return new DomainReading(computer, "Unavailable", notes.Length == 0 ? "No Group Policy evidence was returned." : notes);
                try { archive.Save(evidence); }
                catch (Exception error) when (IsReadOrWriteError(error))
                {
                    return new DomainReading(computer, "Archive error", Evidence.Clip(error.Message, 320));
                }
                return new DomainReading(computer, notes.Length == 0 ? "Saved" : "Partial",
                    notes.Length == 0 ? "Capture saved to the archive." : notes, evidence);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return Cancelled(computer); }
            catch (Exception error) when (IsReadOrWriteError(error))
            {
                return new DomainReading(computer, "Unavailable", Evidence.Clip(error.Message, 320));
            }
        }

        private static bool IsReadOrWriteError(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is InvalidDataException ||
                error is COMException || error is Win32Exception || error is EventLogException || error is SecurityException ||
                error is SerializationException || error is System.Xml.XmlException;
        }

        private static DomainReading Cancelled(string computer)
        {
            return new DomainReading(computer, "Cancelled", "The scan stopped before a capture was saved.");
        }

        internal static string SaveSummary(Archive archive, string domain, DateTimeOffset started, DomainReading[] results)
        {
            string directory = archive.DomainReportsPath;
            Directory.CreateDirectory(directory);
            string filename = "scan-" + started.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N") + ".html";
            string path = Path.Combine(directory, filename);
            var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>GPOCasebook domain scan</title>")
                .Append("<meta name=\"viewport\" content=\"width=device-width\"><style>body{font:16px/1.5 system-ui;margin:32px auto;max-width:1120px;padding:0 20px;color:#182635}")
                .Append("table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:9px;border-bottom:1px solid #dce4ec;")
                .Append("vertical-align:top;overflow-wrap:anywhere}th{background:#edf3fa}</style></head>")
                .Append("<body><h1>Group Policy domain scan</h1><p>Domain <strong>").Append(WebUtility.HtmlEncode(domain))
                .Append("</strong> · Started ").Append(started.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture))
                .Append("</p><p>").Append(results.Length).Append(" computers selected · ")
                .Append(results.Count(e => e.CaseId.HasValue)).Append(" captures saved</p>")
                .Append("<table><thead><tr><th>Computer</th><th>Status</th><th>Events</th><th>Computer RSoP records</th><th>Details</th></tr></thead><tbody>");
            foreach (DomainReading result in results)
            {
                html.Append("<tr><td>").Append(WebUtility.HtmlEncode(result.Computer)).Append("</td><td>")
                    .Append(WebUtility.HtmlEncode(result.Status)).Append("</td><td>").Append(result.Events)
                    .Append("</td><td>").Append(result.Policies).Append("</td><td>")
                    .Append(WebUtility.HtmlEncode(result.Detail)).Append("</td></tr>");
            }
            html.Append("</tbody></table></body></html>");
            Archive.WriteFile(path, false, stream =>
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                    writer.Write(html.ToString());
            });
            return path;
        }
    }
}
