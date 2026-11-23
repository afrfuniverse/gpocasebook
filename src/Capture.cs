using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Xml;

namespace GPOCasebook
{
    internal static class Capture
    {
        private const int RemoteEventsPerChannel = 300;
        private const string Recent = "TimeCreated[timediff(@SystemTime) <= 86400000]";

        public static CaseData Local() { return ReadComputer(null, CancellationToken.None); }

        public static CaseData Remote(string computer, CancellationToken cancellation)
        {
            if (!DomainDirectory.IsDnsName(computer)) throw new ArgumentException("A valid computer DNS name is required.");
            return ReadComputer(computer, cancellation);
        }

        private static CaseData ReadComputer(string computer, CancellationToken cancellation)
        {
            var result = computer == null ? CaseData.Create("Local Group Policy capture", "This PC") :
                CaseData.Create("Domain capture · " + computer, "Domain scan");
            result.TargetComputer = computer;
            var events = new List<PolicyEvent>();
            cancellation.ThrowIfCancellationRequested();
            if (computer == null) ReadWindowsLogs(result, events, null, Evidence.MaxEvents, cancellation);
            else
            {
                try
                {
                    using (var session = new EventLogSession(computer))
                    using (cancellation.Register(() => Interrupt(session)))
                        ReadWindowsLogs(result, events, session, RemoteEventsPerChannel, cancellation);
                }
                catch (Exception error) when (IsWindowsReadError(error))
                {
                    result.Notes.Add("Remote Event Log could not connect. " + Evidence.Clip(error.Message, 300));
                }
            }
            cancellation.ThrowIfCancellationRequested();
            result.Events = Evidence.LatestEvents(events);
            CaptureRsop(result, computer, cancellation);
            return result;
        }

        private static void ReadWindowsLogs(CaseData result, List<PolicyEvent> events, EventLogSession session,
            int limit, CancellationToken cancellation)
        {
            AddChannel(events, result, Evidence.Operational, "*[System[" + Recent + "]]", session, limit, cancellation);
            AddChannel(events, result, "System", "*[System[Provider[@Name='" + Evidence.Provider + "'] and " + Recent + "]]",
                session, limit, cancellation);
        }

        private static void Interrupt(EventLogSession session)
        {
            try { session.CancelCurrentOperations(); }
            catch (Exception error) when (error is EventLogException || error is ObjectDisposedException ||
                error is UnauthorizedAccessException || error is SecurityException) { }
        }

        public static CaseData Import(string[] eventPaths, string rsopPath, string title)
        {
            var result = CaseData.Create(title, "Imported files");
            var events = new List<PolicyEvent>();
            foreach (string path in eventPaths)
            {
                if (String.Equals(Path.GetExtension(path), ".evtx", StringComparison.OrdinalIgnoreCase))
                {
                    var query = new EventLogQuery(path, PathType.FilePath,
                        "*[System[Provider[@Name='" + Evidence.Provider + "']]]") { ReverseDirection = true };
                    ReadRecords(query, events, result.Notes, Evidence.MaxEvents, CancellationToken.None);
                }
                else
                {
                    var loaded = Evidence.ReadEventXml(path)
                        .OrderByDescending(e => e.Utc, StringComparer.Ordinal).Take(Evidence.MaxEvents + 1).ToList();
                    if (loaded.Count == 0) result.Notes.Add("An XML file contained no Group Policy events.");
                    events.AddRange(loaded);
                }
            }
            if (events.Count > Evidence.MaxEvents)
                result.Notes.Add("The capture keeps up to 3,000 recent distinct events.");
            result.Events = Evidence.LatestEvents(events);
            if (!String.IsNullOrEmpty(rsopPath))
            {
                result.Policies = Evidence.ReadRsop(rsopPath);
                if (result.Policies.Count == 0) result.Notes.Add("The RSoP report contained no named policy records.");
            }
            if (result.Events.Count == 0 && result.Policies.Count == 0)
                throw new InvalidDataException("The selected files contained no supported Group Policy observations.");
            return result;
        }

        private static void AddChannel(List<PolicyEvent> events, CaseData result, string channel, string filter,
            EventLogSession session, int limit, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var query = new EventLogQuery(channel, PathType.LogName, filter) { ReverseDirection = true };
                if (session != null) query.Session = session;
                ReadRecords(query, events, result.Notes, limit, cancellation);
            }
            catch (Exception error) when (IsWindowsReadError(error))
            {
                result.Notes.Add("Could not read " + channel + ". " + Evidence.Clip(error.Message, 300));
            }
        }

        private static bool IsWindowsReadError(Exception error)
        {
            return error is UnauthorizedAccessException || error is SecurityException || error is EventLogException ||
                error is Win32Exception;
        }

        private static void ReadRecords(EventLogQuery query, List<PolicyEvent> destination, List<string> notes,
            int limit, CancellationToken cancellation)
        {
            int invalid = 0;
            int count = 0;
            using (var reader = new EventLogReader(query))
            {
                while (count < limit)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using (EventRecord record = reader.ReadEvent(TimeSpan.FromSeconds(5)))
                    {
                        if (record == null) break;
                        count++;
                        string description = null;
                        try { description = record.FormatDescription(); }
                        catch (Exception error) when (IsWindowsReadError(error)) { }
                        try
                        {
                            var item = Evidence.ParseEvent(record.ToXml(), description);
                            if (item != null) destination.Add(item);
                        }
                        catch (XmlException) { invalid++; }
                        catch (InvalidDataException) { invalid++; }
                    }
                }
            }
            if (invalid != 0) notes.Add(invalid + " event records could not be read.");
            if (count == limit) notes.Add("A log reached the limit of " + limit.ToString(CultureInfo.InvariantCulture) + " recent records.");
        }

        internal static string RsopArguments(string computer, string path)
        {
            return (computer == null ? "" : "/s " + computer + " /scope computer ") + "/x \"" + path + "\"";
        }

        private static void CaptureRsop(CaseData result, string computer, CancellationToken cancellation)
        {
            string path = Path.Combine(Path.GetTempPath(), "GPOCasebook-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "gpresult.exe"),
                    RsopArguments(computer, path)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(start))
                {
                    process.StandardInput.Close();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(200))
                    {
                        if (cancellation.IsCancellationRequested || elapsed.ElapsedMilliseconds >= 20000)
                        {
                            try { process.Kill(); process.WaitForExit(1000); }
                            catch (InvalidOperationException) { }
                            cancellation.ThrowIfCancellationRequested();
                            result.Notes.Add("gpresult timed out.");
                            return;
                        }
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0 || !File.Exists(path))
                    {
                        result.Notes.Add("gpresult returned exit code " + process.ExitCode +
                            " without a usable " + (computer == null ? "local" : "remote Computer") + " RSoP report.");
                        return;
                    }
                }
                result.Policies = Evidence.ReadRsop(path);
                if (result.Policies.Count == 0) result.Notes.Add("gpresult returned no named policy records.");
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || IsWindowsReadError(e) || e is XmlException)
            {
                result.Notes.Add("Windows could not collect an RSoP report.");
            }
            finally
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

    }
}
