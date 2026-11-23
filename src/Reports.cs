using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GPOCasebook
{
    internal sealed class Archive
    {
        private readonly string root;
        private const long DocumentLimit = 64 * 1024 * 1024;

        public Archive(string path) { root = path; }
        public string FolderPath { get { return root; } }
        public string DomainReportsPath { get { return Path.Combine(root, "DomainScans"); } }
        internal static string PathForFamily(string family)
        {
            return PackageIdentity.ArchiveDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), family);
        }

        public List<ArchiveEntry> List(out int unreadable)
        {
            unreadable = 0;
            var result = new List<ArchiveEntry>();
            if (!Directory.Exists(root)) return result;
            foreach (string file in Directory.EnumerateFiles(root, "*.json"))
            {
                Guid id;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out id)) continue;
                try { result.Add(new ArchiveEntry(Load(id))); }
                catch (IOException) { unreadable++; }
                catch (InvalidDataException) { unreadable++; }
                catch (UnauthorizedAccessException) { unreadable++; }
                catch (SerializationException) { unreadable++; }
            }
            return result.OrderByDescending(e => e.CapturedUtc, StringComparer.Ordinal).ToList();
        }

        public CaseData Load(Guid id)
        {
            using (var stream = new FileStream(PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > DocumentLimit) throw new InvalidDataException("The archived case exceeds 64 MB.");
                var item = (CaseData)Serializer().ReadObject(stream);
                Validate(item);
                if (item.Id != id) throw new InvalidDataException("The archived case ID does not match its file.");
                return item;
            }
        }

        public void Save(CaseData item)
        {
            Validate(item);
            Directory.CreateDirectory(root);
            WriteFile(PathFor(item.Id), false, stream =>
            {
                Serializer().WriteObject(stream, item);
                if (stream.Length > DocumentLimit) throw new InvalidDataException("The case exceeds the 64 MB archive limit.");
            });
        }

        private string PathFor(Guid id) { return Path.Combine(root, id.ToString("N") + ".json"); }

        private static DataContractJsonSerializer Serializer()
        {
            return new DataContractJsonSerializer(typeof(CaseData),
                new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 380000 });
        }

        private static bool Stamp(string value)
        {
            DateTimeOffset parsed;
            return DateTimeOffset.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) &&
                parsed.Offset == TimeSpan.Zero;
        }

        private static void Validate(CaseData item)
        {
            if (item == null || item.Version != 1 || item.Id == Guid.Empty || !Stamp(item.CapturedUtc) ||
                String.IsNullOrWhiteSpace(item.Title) || String.IsNullOrWhiteSpace(item.Source) ||
                (item.TargetComputer != null && !DomainDirectory.IsDnsName(item.TargetComputer)) ||
                item.Events == null || item.Events.Count > Evidence.MaxEvents ||
                item.Policies == null || item.Policies.Count > 2000 || item.Notes == null)
                throw new InvalidDataException("This file is not a supported GPOCasebook case.");

            foreach (var e in item.Events)
            {
                Guid activity;
                if (e == null || !Stamp(e.Utc) || e.Computer == null || e.Activity == null || e.Message == null ||
                    (e.Activity.Length > 0 && (!Guid.TryParseExact(e.Activity, "D", out activity) || activity == Guid.Empty)) ||
                    e.Fields == null || e.Fields.Count > 32 ||
                    (e.Channel != "System" && e.Channel != Evidence.Operational) || e.Id < 1 || e.Id > 65535 ||
                    e.Fields.Any(f => f == null || f.Name == null || f.Value == null))
                    throw new InvalidDataException("The case contains an invalid event record.");
            }
            if (item.Policies.Any(p => p == null || String.IsNullOrEmpty(p.Name) ||
                (p.Scope != "Computer" && p.Scope != "User") || p.State == null || p.Reason == null))
                throw new InvalidDataException("The case contains an invalid policy record.");
        }

        public static void WriteFile(string path, bool overwrite, Action<FileStream> write)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            string temporary = Path.Combine(directory, ".gpo-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);
                }
                if (overwrite && File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal sealed class ArchiveEntry
    {
        public Guid Id { get; private set; }
        public string Title { get; private set; }
        public string CapturedUtc { get; private set; }
        public string Host { get; private set; }
        public string Metadata { get; private set; }
        public string Display { get { return Title + " · " + Metadata; } }
        public int ErrorCount { get; private set; }

        public ArchiveEntry(CaseData item)
        {
            Id = item.Id;
            Title = item.Title;
            CapturedUtc = item.CapturedUtc;
            Host = item.Host;
            ErrorCount = item.Events.Count(e => e.IsError);
            Metadata = item.CapturedUtc.Substring(0, 10) + "  ·  " + item.Host;
        }
    }

    internal static class HtmlReport
    {
        public static void Save(string path, CaseData current, CaseData before, CaseData after, bool includeDetails)
        {
            string html = Render(current, before, after, includeDetails);
            Archive.WriteFile(path, true, stream =>
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)) writer.Write(html);
            });
        }

        public static string Render(CaseData current, CaseData before, CaseData after, bool includeDetails)
        {
            var html = new StringBuilder();
            html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">")
                .Append("<title>GPOCasebook report</title><style>body{font:16px/1.55 system-ui,sans-serif;color:#182635;margin:36px auto;")
                .Append("padding:0 24px;max-width:1060px}h1{margin:0}h2{margin-top:32px}table{width:100%;border-collapse:collapse}")
                .Append("th,td{padding:9px;text-align:left;border-bottom:1px solid #dce4ec;vertical-align:top;overflow-wrap:anywhere}")
                .Append("th{background:#edf3fa}pre{white-space:pre-wrap;overflow-wrap:anywhere;margin:0}small{color:#526579}</style>")
                .Append("</head><body><h1>GPOCasebook report</h1>");
            if (includeDetails) html.Append("<p>").Append(E(current.Title)).Append(" · ").Append(E(current.Host)).Append("</p>");
            else html.Append("<p>Shareable summary</p>");
            html.Append("<p>Capture time <strong>").Append(E(current.CapturedUtc)).Append("</strong> · ")
                .Append(current.Events.Count).Append(" events · ").Append(current.Events.Count(e => e.IsError))
                .Append(" errors</p>");
            if (includeDetails && current.Notes.Count > 0)
                html.Append("<p>").Append(E(String.Join(" ", current.Notes))).Append("</p>");
            else if (current.Notes.Count > 0) html.Append("<p>Some sources reported collection limitations.</p>");

            html.Append("<h2>Processing runs</h2><p>Times are in UTC. Uncorrelated events appear separately.</p>");
            foreach (var run in PolicyRun.From(current))
            {
                html.Append("<h3>").Append(E(run.Label)).Append("</h3><table><thead><tr><th>Time (UTC)</th>")
                    .Append("<th>Event ID</th><th>Channel / record</th><th>Observation</th></tr></thead><tbody>");
                foreach (var e in run.Events)
                {
                    html.Append("<tr><td>").Append(E(e.Clock)).Append("</td><td>").Append(e.Id)
                        .Append("</td><td>").Append(e.Channel == "System" ? "System" : "GroupPolicy/Operational");
                    if (includeDetails) html.Append(" / ").Append(e.Record);
                    html.Append("</td><td>").Append(E(e.Severity)).Append(" · ").Append(E(e.Summary));
                    if (includeDetails)
                    {
                        html.Append("<pre>").Append(E(e.Message));
                        foreach (var field in e.Fields)
                            html.Append('\n').Append(E(field.Name)).Append("  ").Append(E(field.Value));
                        html.Append("</pre>");
                    }
                    html.Append("</td></tr>");
                }
                html.Append("</tbody></table>");
            }
            if (includeDetails && current.Policies.Count > 0)
            {
                html.Append("<h2>Resultant policy snapshot</h2><table><thead><tr><th>Policy</th><th>Scope</th>")
                    .Append("<th>Reported state</th><th>Evidence</th></tr></thead><tbody>");
                foreach (var p in current.Policies) Row(html, p.Name, p.Scope, p.State, p.Reason);
                html.Append("</tbody></table>");
            }
            if (before != null && after != null && before.Id != after.Id)
            {
                html.Append("<h2>Capture comparison</h2><p>").Append(E(ComparisonRow.SourceContext(before, after)))
                    .Append("</p><p>").Append(E(before.CapturedUtc)).Append(" → ")
                    .Append(E(after.CapturedUtc)).Append("</p><table><thead><tr><th>Observation</th><th>Before</th>")
                    .Append("<th>After</th></tr></thead><tbody>");
                var rows = ComparisonRow.Build(before, after);
                foreach (var row in includeDetails ? rows : rows.Take(2))
                    Row(html, row.Item, row.Before, row.After);
                html.Append("</tbody></table>");
            }
            return html.Append("</body></html>").ToString();
        }

        private static void Row(StringBuilder builder, params string[] values)
        {
            builder.Append("<tr>");
            foreach (string value in values) builder.Append("<td>").Append(E(value)).Append("</td>");
            builder.Append("</tr>");
        }

        private static string E(string value) { return WebUtility.HtmlEncode(value ?? ""); }
    }
}
