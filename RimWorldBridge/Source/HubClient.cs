using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Verse;

namespace RimDewBridge
{
    public class HubClient
    {
        private readonly BlockingCollection<string> outbox = new BlockingCollection<string>();
        private volatile bool stop;
        public ConcurrentQueue<Dictionary<string, object>> Inbox = new ConcurrentQueue<Dictionary<string, object>>();
        public volatile bool Connected;

        public HubClient() { new Thread(Run) { IsBackground = true, Name = "RimDewHub" }.Start(); }

        public void Send(Dictionary<string, object> m) { outbox.Add(MiniJson.Serialize(m)); }
        public void Stop() { stop = true; }

        private void Run()
        {
            while (!stop)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        tcp.Connect("127.0.0.1", 7777);
                        var stream = tcp.GetStream();
                        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                        writer.WriteLine("{\"type\":\"hello\",\"game\":\"rimworld\"}");
                        Connected = true;
                        Log.Message("[RimDew] Connected to hub.");
                        var reader = new Thread(() =>
                        {
                            try
                            {
                                var sr = new StreamReader(stream, Encoding.UTF8);
                                string l;
                                while ((l = sr.ReadLine()) != null)
                                    try { Inbox.Enqueue(MiniJson.Parse(l).Obj()); } catch { }
                            }
                            catch { }
                        }) { IsBackground = true };
                        reader.Start();
                        var lastBeat = DateTime.UtcNow;
                        while (!stop && reader.IsAlive)
                        {
                            string line;
                            if (outbox.TryTake(out line, 250)) writer.WriteLine(line);
                            // Heartbeat runs on this thread so pausing the game doesn't trip the hub watchdog.
                            if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 1)
                            {
                                writer.WriteLine("{\"type\":\"heartbeat\"}");
                                lastBeat = DateTime.UtcNow;
                            }
                        }
                    }
                }
                catch { }
                if (Connected) Log.Message("[RimDew] Disconnected from hub; retrying.");
                Connected = false;
                Thread.Sleep(3000);
            }
        }
    }
}
