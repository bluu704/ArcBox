using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;

namespace ArcBox.Shell
{
    /// <summary>
    /// Explorer starts one process per selected file for classic context-menu verbs. The first process becomes the
    /// "leader", collects the paths from the others over a named pipe, and handles them all in one window.
    /// </summary>
    public static class Collector
    {
        // string.GetHashCode() is randomized per process on .NET Core, so use FNV-1a
        private static string StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("x8");
        }

        /// <returns>All paths to process, or null when this process handed its paths to a leader and should exit.</returns>
        public static List<string> Collect(string verb, IEnumerable<string> mine)
        {
            var mineList = mine.ToList();
            string name = "ArcBox-" + verb.Trim('/') + "-" + StableHash(Environment.UserDomainName + "\\" + Environment.UserName) + "-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
            bool leader;
            Mutex mutex;
            try { mutex = new Mutex(true, "Local\\" + name, out leader); }
            catch { return mineList; }

            if (!leader)
            {
                mutex.Dispose();
                try
                {
                    using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out))
                    {
                        client.Connect(3000);
                        var data = Encoding.UTF8.GetBytes(string.Join("\n", mineList) + "\n");
                        client.Write(data, 0, data.Length);
                        client.Flush();
                    }
                    return null;
                }
                catch { return mineList; } // leader gone: handle our own files
            }

            var all = new List<string>(mineList);
            var sync = new object();
            DateTime last = DateTime.UtcNow;
            var stop = new ManualResetEvent(false);
            var server = new Thread(() =>
            {
                while (!stop.WaitOne(0))
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                        {
                            var ar = pipe.BeginWaitForConnection(null, null);
                            while (!ar.AsyncWaitHandle.WaitOne(50)) if (stop.WaitOne(0)) return;
                            pipe.EndWaitForConnection(ar);
                            using (var sr = new StreamReader(pipe, Encoding.UTF8))
                            {
                                string text = sr.ReadToEnd();
                                lock (sync)
                                {
                                    all.AddRange(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
                                    last = DateTime.UtcNow;
                                }
                            }
                        }
                    }
                    catch { Thread.Sleep(20); }
                }
            }) { IsBackground = true };
            server.Start();

            // Explorer starts all processes at once; wait until nobody new has arrived for a short while.
            int minWait = 700, quiet = 500;
            if (int.TryParse(Environment.GetEnvironmentVariable("ARCBOX_COLLECT_MS"), out int ov) && ov > 0) { minWait = ov; quiet = ov; }
            var started = DateTime.UtcNow;
            while (true)
            {
                Thread.Sleep(50);
                lock (sync)
                {
                    var now = DateTime.UtcNow;
                    if ((now - last).TotalMilliseconds > quiet && (now - started).TotalMilliseconds > minWait) break;
                    if ((now - started).TotalSeconds > 15) break;
                }
            }
            stop.Set();
            server.Join(1000);
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
            lock (sync) return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
