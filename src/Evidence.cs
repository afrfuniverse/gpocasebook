using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace GPOCasebook
{
    public sealed class CaseData
    {
        public int Version { get; set; }
        public Guid Id { get; set; }
        public string Title { get; set; }
        public string Source { get; set; }
        public string TargetComputer { get; set; }
        public string CapturedUtc { get; set; }
        public List<PolicyEvent> Events { get; set; }
        public List<PolicyRecord> Policies { get; set; }
        public List<string> Notes { get; set; }

        public static CaseData Create(string title, string source)
        {
            return new CaseData
            {
                Version = 1, Id = Guid.NewGuid(), Title = title, Source = source,
                CapturedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Events = new List<PolicyEvent>(), Policies = new List<PolicyRecord>(), Notes = new List<string>()
            };
        }

        public string Host
        {
            get
            {
                var hosts = Events.Select(e => e.Computer).Where(x => !String.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
                return hosts.Length == 0 ? (TargetComputer ?? "Computer unknown") :
                    hosts.Length == 1 ? hosts[0] : "Multiple computers";
            }
        }
    }

    public sealed class EventField
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    public sealed class PolicyEvent
    {
        public int Id { get; set; }
        public int Level { get; set; }
        public long Record { get; set; }
        public string Utc { get; set; }
        public string Channel { get; set; }
        public string Computer { get; set; }
        public string Activity { get; set; }
        public string Message { get; set; }
        public List<EventField> Fields { get; set; }

        public string Field(string name)
        {
            var field = Fields.FirstOrDefault(f => String.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            return field == null ? "" : field.Value;
        }

        public bool IsError { get { return Id == 1096 || Level == 1 || Level == 2; } }
        public bool IsWarning { get { return Level == 3 && !IsError; } }
        public string Severity { get { return IsError ? "Error" : IsWarning ? "Warning" : "Info"; } }
        public string Clock { get { return Utc.Substring(11, 8); } }
        public string EventNumber { get { return Id.ToString(CultureInfo.InvariantCulture); } }
        public string Summary
        {
            get
            {
                switch (Id)
                {
                    case 1096:
                        return Field("GPOCNName").Equals("LocalGPO", StringComparison.OrdinalIgnoreCase)
                            ? "LocalGPO registry policy error" : "Registry policy error";
                    case 4000: case 4002: case 4004: case 4006: return "Computer policy processing started";
                    case 4001: case 4003: case 4005: case 4007: return "User policy processing started";
                    case 8000: case 8002: case 8004: case 8006: return "Computer policy processing completed";
                    case 8001: case 8003: case 8005: case 8007: return "User policy processing completed";
                    case 4016: return "Client-side extension started";
                    case 5016: return "Client-side extension completed";
                    case 6016: return "Client-side extension warning";
                    case 7016: return "Client-side extension error";
                    case 5312: return "Applicable GPOs listed";
                    case 5313: return "Filtered GPOs listed";
                }
                return "Group Policy event " + EventNumber;
            }
        }
    }

    public sealed class PolicyRecord
    {
        public string Name { get; set; }
        public string Scope { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
    }

    internal static class Evidence
    {
        public const string Provider = "Microsoft-Windows-GroupPolicy";
        public const string Operational = Provider + "/Operational";
        public const int MaxEvents = 3000;
        public const long XmlLimit = 32 * 1024 * 1024;
        private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

        public static XElement ReadXml(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > XmlLimit) throw new InvalidDataException("An XML file exceeds 32 MB.");
                using (var reader = XmlReader.Create(stream, XmlSettings()))
                    return XDocument.Load(reader).Root;
            }
        }

        private static XmlReaderSettings XmlSettings()
        {
            return new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = XmlLimit };
        }

        public static PolicyEvent ParseEvent(string xml, string message)
        {
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, XmlSettings()))
                return ParseEvent(XElement.Load(reader), message);
        }

        public static PolicyEvent ParseEvent(XElement node, string renderedMessage)
        {
            var system = node.Element(EventNamespace + "System");
            if (node.Name != EventNamespace + "Event" || system == null) return null;
            var provider = Attribute(system.Element(EventNamespace + "Provider"), "Name");
            var channel = (string)system.Element(EventNamespace + "Channel");
            if (!String.Equals(provider, Provider, StringComparison.OrdinalIgnoreCase) ||
                !(String.Equals(channel, "System", StringComparison.OrdinalIgnoreCase) ||
                  String.Equals(channel, Operational, StringComparison.OrdinalIgnoreCase))) return null;

            int id;
            DateTimeOffset utc;
            if (!Int32.TryParse((string)system.Element(EventNamespace + "EventID"), NumberStyles.None,
                CultureInfo.InvariantCulture, out id) || id < 1 || id > 65535 ||
                !DateTimeOffset.TryParse(Attribute(system.Element(EventNamespace + "TimeCreated"), "SystemTime"),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out utc))
                throw new InvalidDataException("A Group Policy event is missing a valid event ID or timestamp.");

            int level;
            long record;
            Guid activity;
            Int32.TryParse((string)system.Element(EventNamespace + "Level"), out level);
            Int64.TryParse((string)system.Element(EventNamespace + "EventRecordID"), out record);
            Guid.TryParse(Attribute(system.Element(EventNamespace + "Correlation"), "ActivityID"), out activity);
            var data = node.Element(EventNamespace + "EventData");
            var rendering = node.Element(EventNamespace + "RenderingInfo");
            var fields = data == null ? new List<EventField>() : data.Elements(EventNamespace + "Data").Take(32)
                .Select((field, index) => new EventField
                {
                    Name = Clip(Attribute(field, "Name") ?? ("Data " + (index + 1)), 120),
                    Value = Clip(field.Value, 2048)
                }).ToList();
            return new PolicyEvent
            {
                Id = id, Level = level, Record = Math.Max(0, record),
                Utc = utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                Computer = Clip((string)system.Element(EventNamespace + "Computer"), 255),
                Channel = String.Equals(channel, "System", StringComparison.OrdinalIgnoreCase) ? "System" : Operational,
                Activity = activity == Guid.Empty ? "" : activity.ToString("D"),
                Message = Clip(renderedMessage ?? (rendering == null ? null : (string)rendering.Element(EventNamespace + "Message")), 3200),
                Fields = fields
            };
        }

        public static IEnumerable<PolicyEvent> ReadEventXml(string path)
        {
            var root = ReadXml(path);
            IEnumerable<XElement> nodes;
            if (root.Name == EventNamespace + "Event") nodes = new[] { root };
            else if (root.Name.LocalName == "Events") nodes = root.Elements(EventNamespace + "Event");
            else throw new InvalidDataException("Choose an Event Viewer event XML file or an .evtx export.");
            return nodes.Select(node => ParseEvent(node, null)).Where(e => e != null);
        }

        public static List<PolicyRecord> ReadRsop(string path)
        {
            var root = ReadXml(path);
            if (!String.Equals(root.Name.LocalName, "Rsop", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose an XML report created with gpresult /x.");
            var records = new List<PolicyRecord>();
            foreach (var result in root.Elements())
            {
                var scope = result.Name.LocalName == "ComputerResults" ? "Computer" :
                    result.Name.LocalName == "UserResults" ? "User" : null;
                if (scope == null) continue;
                foreach (var gpo in result.Elements().Where(x => x.Name.LocalName == "GPO"))
                {
                    if (records.Count == 2000) throw new InvalidDataException("The RSoP report exceeds 2,000 policy records.");
                    string name = Clip(Value(gpo, "Name"), 384);
                    if (name.Length == 0) continue;
                    string state = "Listed";
                    string reason = "RSoP did not identify an application state.";
                    int order;
                    if (Value(gpo, "AccessDenied").Equals("true", StringComparison.OrdinalIgnoreCase))
                    { state = "Denied"; reason = "RSoP reports access denied."; }
                    else if (Value(gpo, "FilterAllowed").Equals("false", StringComparison.OrdinalIgnoreCase))
                    { state = "Filtered"; reason = "RSoP reports that the filter did not allow this GPO."; }
                    else if (Value(gpo, "Enabled").Equals("false", StringComparison.OrdinalIgnoreCase))
                    { state = "Disabled"; reason = "RSoP reports this GPO as disabled."; }
                    else if (Int32.TryParse(Value(gpo, "AppliedOrder"), out order) && order > 0)
                    { state = "Applied"; reason = "RSoP records application order " + order + "."; }
                    records.Add(new PolicyRecord { Name = name, Scope = scope, State = state, Reason = reason });
                }
            }
            return records;
        }

        public static List<PolicyEvent> LatestEvents(IEnumerable<PolicyEvent> events)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var distinct = new List<PolicyEvent>();
            foreach (var e in events.OrderByDescending(x => x.Utc, StringComparer.Ordinal).ThenByDescending(x => x.Record))
            {
                string identity = e.Computer + "|" + e.Channel + "|" + e.Record + "|" + e.Utc + "|" + e.Id + "|" + e.Activity;
                if (e.Record == 0 || seen.Add(identity)) distinct.Add(e);
                if (distinct.Count == MaxEvents) break;
            }
            distinct.Reverse();
            return distinct;
        }

        private static string Attribute(XElement element, string name)
        { return element == null ? null : (string)element.Attribute(name); }

        private static string Value(XElement element, string name)
        { return (string)element.Elements().FirstOrDefault(e => e.Name.LocalName == name) ?? ""; }

        public static string Clip(string value, int limit)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            value = value.Trim();
            if (value.Length <= limit) return value;
            int end = Char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit;
            return value.Substring(0, end) + "\u2026";
        }
    }

    internal sealed class PolicyRun
    {
        public List<PolicyEvent> Events { get; private set; }
        public string Scope { get; private set; }
        public string Outcome { get; private set; }
        public string Label { get; private set; }

        public PolicyRun(IEnumerable<PolicyEvent> events)
        {
            Events = events.OrderBy(e => e.Utc, StringComparer.Ordinal).ThenBy(e => e.Record).ToList();
            var scopes = Events.Select(ScopeOf).Where(x => x != "").Distinct().ToList();
            Scope = scopes.Count == 1 ? scopes[0] : "Unassigned";
            bool completed = Events.Any(e => e.Channel == Evidence.Operational && SuccessfulEnd(e.Id) && !e.IsError && !e.IsWarning);
            bool errors = Events.Any(e => e.IsError);
            bool warnings = Events.Any(e => e.IsWarning);
            Outcome = errors ? (completed ? "Completed with errors" : "Error recorded") :
                warnings ? (completed ? "Completed with warnings" : "Warning recorded") :
                completed ? "Completed" : "Completion unavailable";
            Label = Events[0].Utc.Substring(0, 10) + "  " + Events[0].Clock + " UTC   ·   " +
                Scope + "   ·   " + Outcome +
                (Events[0].Activity.Length == 0 || Events[0].Computer.Length == 0 ? "   ·   Uncorrelated" : "");
        }

        public static List<PolicyRun> From(CaseData data)
        {
            var correlated = data.Events.Where(e => e.Activity.Length != 0 && e.Computer.Length != 0)
                .GroupBy(e => e.Computer + "|" + e.Activity, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PolicyRun(g));
            return correlated.Concat(data.Events.Where(e => e.Activity.Length == 0 || e.Computer.Length == 0)
                .Select(e => new PolicyRun(new[] { e })))
                .OrderByDescending(run => run.Events.Last().Utc, StringComparer.Ordinal).ToList();
        }

        private static bool SuccessfulEnd(int id) { return id >= 8000 && id <= 8007; }

        private static string ScopeOf(PolicyEvent evidence)
        {
            if (evidence.Channel == Evidence.Operational &&
                ((evidence.Id >= 4000 && evidence.Id <= 4007) || SuccessfulEnd(evidence.Id)))
                return evidence.Id % 2 == 0 ? "Computer" : "User";
            if (evidence.Id == 1096)
            {
                string path = evidence.Field("FilePath").Replace('/', '\\');
                if (path.EndsWith(@"\GroupPolicy\Machine\Registry.pol", StringComparison.OrdinalIgnoreCase)) return "Computer";
                if (path.EndsWith(@"\GroupPolicy\User\Registry.pol", StringComparison.OrdinalIgnoreCase)) return "User";
            }
            return "";
        }
    }

    internal sealed class ComparisonRow
    {
        public string Item { get; set; }
        public string Before { get; set; }
        public string After { get; set; }
        public string Change { get { return Before == After ? "Same observation" : "Changed"; } }

        public static string SourceContext(CaseData before, CaseData after)
        {
            string first = before.Host, second = after.Host;
            if (new[] { first, second }.Any(x => x == "Computer unknown" || x == "Multiple computers"))
                return "A single computer label is unavailable for at least one capture.";
            return String.Equals(first, second, StringComparison.OrdinalIgnoreCase)
                ? "Source labels match." : "The captures have different source labels.";
        }

        public static List<ComparisonRow> Build(CaseData before, CaseData after)
        {
            var baseline = PolicyRun.From(before);
            var latest = PolicyRun.From(after);
            var rows = new List<ComparisonRow>();
            foreach (var scope in new[] { "Computer", "User" })
            {
                var older = baseline.FirstOrDefault(r => r.Scope == scope);
                var newer = latest.FirstOrDefault(r => r.Scope == scope);
                rows.Add(new ComparisonRow {
                    Item = scope + " · latest run", Before = older == null ? "Unavailable" : older.Outcome,
                    After = newer == null ? "Unavailable" : newer.Outcome });
            }
            var matches = before.Policies.Select(p => p.Scope + " · " + p.Name)
                .Union(after.Policies.Select(p => p.Scope + " · " + p.Name), StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            var oldStates = PolicyStates(before);
            var newStates = PolicyStates(after);
            foreach (var key in matches)
                rows.Add(new ComparisonRow { Item = key, Before = Lookup(oldStates, key), After = Lookup(newStates, key) });
            return rows;
        }

        private static Dictionary<string, string> PolicyStates(CaseData data)
        {
            return data.Policies.GroupBy(p => p.Scope + " · " + p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => String.Join(" / ", g.Select(p => p.State).Distinct().OrderBy(x => x)),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static string Lookup(Dictionary<string, string> states, string key)
        {
            string value;
            return states.TryGetValue(key, out value) ? value : "Not in report";
        }
    }
}
