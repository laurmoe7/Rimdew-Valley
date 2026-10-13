using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;

namespace StardewBridge;

/// <summary>Background TCP client for the relay hub. Messages are newline-delimited JSON.</summary>
public class HubClient : IDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly Action<string> log;
    private readonly CancellationTokenSource cts = new();
    private readonly BlockingCollection<string> outbox = new();

    /// <summary>Messages received from the hub, drained on the game thread.</summary>
    public ConcurrentQueue<JObject> Inbox { get; } = new();
    public bool Connected { get; private set; }

    public HubClient(string host, int port, Action<string> log)
    {
        this.host = host;
        this.port = port;
        this.log = log;
        new Thread(Run) { IsBackground = true, Name = "HubClient" }.Start();
    }

    public void Send(JObject msg) => outbox.Add(msg.ToString(Newtonsoft.Json.Formatting.None));

    private void Run()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                tcp.Connect(host, port);
                using var stream = tcp.GetStream();
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                writer.WriteLine(new JObject { ["type"] = "hello", ["game"] = "stardew" }.ToString(Newtonsoft.Json.Formatting.None));
                Connected = true;
                log("Connected to hub.");

                var reader = new Thread(() =>
                {
                    try
                    {
                        using var sr = new StreamReader(stream, Encoding.UTF8);
                        string? line;
                        while ((line = sr.ReadLine()) != null)
                            try { Inbox.Enqueue(JObject.Parse(line)); } catch { }
                    }
                    catch { }
                }) { IsBackground = true };
                reader.Start();

                var lastBeat = DateTime.UtcNow;
                while (!cts.IsCancellationRequested && reader.IsAlive)
                {
                    if (outbox.TryTake(out var line, 250))
                        writer.WriteLine(line);
                    // Heartbeat comes from this thread so a long save (game thread frozen) doesn't drop us.
                    if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 1)
                    {
                        writer.WriteLine("{\"type\":\"heartbeat\"}");
                        lastBeat = DateTime.UtcNow;
                    }
                }
            }
            catch { }
            if (Connected) log("Disconnected from hub; retrying.");
            Connected = false;
            cts.Token.WaitHandle.WaitOne(3000);
        }
    }

    public void Dispose() => cts.Cancel();
}
