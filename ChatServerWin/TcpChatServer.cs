using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatServerWin.Models;

namespace ChatServerWin
{
    public class TcpChatServer
    {
        private readonly IPAddress _ip;
        private readonly int _port;
        private readonly TcpListener _listener;
        private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
        private readonly Dictionary<string, string> _creds = new() { ["akshay"] = "1234", ["patil"] = "4567", ["admin"] = "qwerty" };
        private readonly CancellationTokenSource _cts = new();

        // Metrics & Audit
        private readonly string _auditPath = "audit.log";
        private long _messagesReceived;
        private long _messagesDelivered;
        private readonly ConcurrentQueue<long> _deliveredTimestamps = new();
        private readonly List<long> _latencies = new();
        private readonly object _latLock = new();

        private readonly Action<string> _log;
        private readonly Action<string[]> _updateUsers;

        public TcpChatServer(IPAddress ip, int port, Action<string> log, Action<string[]> updateUsers)
        {
            _ip = ip; _port = port; _listener = new TcpListener(_ip, _port);
            _log = log; _updateUsers = updateUsers;
            File.WriteAllText(_auditPath, string.Empty);
        }

        public async Task StartAsync()
        {
            _listener.Start();
            _log?.Invoke($"Server listening on {_ip}:{_port}");
            _ = Task.Run(StatsLoop);
            _ = Task.Run(IdleLoop);

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (Exception ex) { _log?.Invoke("Accept error: " + ex.Message); }
            }
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            foreach (var s in _sessions.Values) s.Close();
            _sessions.Clear();
            _updateUsers?.Invoke(_sessions.Keys.ToArray());
        }

        private async Task HandleClientAsync(TcpClient tcp)
        {
            var stream = tcp.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            string line = null;
            try { line = await reader.ReadLineAsync(); } catch { }
            if (string.IsNullOrEmpty(line)) { tcp.Close(); return; }

            var jsonDoc = JsonDocument.Parse(line);
            var payload = jsonDoc.RootElement.GetProperty("payload");

            // Extract username and password
            string username = payload.GetProperty("username").GetString();
            string password = payload.GetProperty("password").GetString();

            Packet pkt;
            try { pkt = ParsePacket(line); } catch { tcp.Close(); return; }
            if (!pkt.type.Equals("LOGIN_REQ", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "LOGIN_RESP", payload = new { ok = false, reason = "first packet must be LOGIN_REQ" } }));
                tcp.Close(); return;
            }

            //string username = null;
            //string password = null;
            try
            {
                if (pkt.payload.TryGetProperty("username", out var u)) username = u.GetString();
                if (pkt.payload.TryGetProperty("password", out var p)) password = p.GetString();
            }
            catch { }

            if (username == null || password == null || !_creds.TryGetValue(username, out var expected) || expected != password)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "LOGIN_RESP", payload = new { ok = false, reason = "invalid credentials" } }));
                tcp.Close(); return;
            }

            if (_sessions.ContainsKey(username))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "LOGIN_RESP", payload = new { ok = false, reason = "already logged in" } }));
                tcp.Close(); return;
            }

            var session = new ClientSession(username, tcp, reader, writer);
            if (!_sessions.TryAdd(username, session)) { tcp.Close(); return; }

            _log?.Invoke($"{username} connected");
            _updateUsers?.Invoke(_sessions.Keys.ToArray());
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "LOGIN_RESP", payload = new { ok = true } }));

            _ = Task.Run(() => session.SendLoopAsync(_cts.Token, RecordLatency));
            _ = Task.Run(() => ReadLoopAsync(session));
        }

        private Packet ParsePacket(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var payload = root.GetProperty("payload");
            return new Packet(type, payload);
        }

        private async Task ReadLoopAsync(ClientSession session)
        {
            var reader = session.Reader;
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                   
                    if (line == null) break;
                    session.UpdateActivity();
                    Packet pkt;
                    try { pkt = ParsePacket(line); } catch { break; } // malformed -> close
                    Interlocked.Increment(ref _messagesReceived);
                    var now = DateTime.UtcNow;

                    switch (pkt.type.ToUpperInvariant())
                    {
                        case "DM":
                            var jsonDoc = JsonDocument.Parse(line);
                            var payload = jsonDoc.RootElement.GetProperty("payload");

                            // Extract username and password
                            string to = payload.GetProperty("to").GetString();
                            string msg = payload.GetProperty("msg").GetString();
                            //var to = pkt.payload.GetProperty("to").GetString();
                            //var msg = pkt.payload.GetProperty("msg").GetString();
                            var bytes = Encoding.UTF8.GetByteCount(msg ?? "");
                            AppendAudit(now, session.Username, to, "DM", bytes);
                            string key = "admin"; // or your key variable
                            if (_sessions.TryGetValue(key, out ClientSession session1))

                            {
                                session1.Enqueue(MakeEnvelope("INCOMING_DM", new { from = session.Username, msg }));
                                Interlocked.Increment(ref _messagesDelivered);
                                _deliveredTimestamps.Enqueue(DateTime.UtcNow.Ticks);
                            }
                            break;
                        case "MULTI":
                            // Parse JSON
                            var jsonDoc1 = JsonDocument.Parse(line);
                            var root = jsonDoc1.RootElement;

                            // Extract type
                            string messageType = root.GetProperty("type").GetString(); // "MULTI"

                            // Extract payload
                            var payload1 = root.GetProperty("payload");
                            string message = payload1.GetProperty("msg").GetString(); // "hi1122"

                            // Extract "to" array (empty in this case)
                            var toArray = payload1.GetProperty("to");
                            var recipients = new List<string>();
                            foreach (var item in toArray.EnumerateArray())
                            {
                                recipients.Add(item.GetString());
                            }

                            Console.WriteLine($"Type: {messageType}");
                            Console.WriteLine($"Message: {message}");
                            Console.WriteLine($"Recipients count: {recipients.Count}");


                            //var toArr = pkt.payload.GetProperty("to").EnumerateArray();
                            //var multiMsg = pkt.payload.GetProperty("msg").GetString();
                            var multiMsg = message;
                            //foreach (var t in toArr)
                            //{
                                //var destUser = t.GetString();
                                 key = "admin"; // or your key variable
                                if (_sessions.TryGetValue(key, out ClientSession session2))
                                {
                                    session2.Enqueue(MakeEnvelope("INCOMING_MULTI", new { from = session.Username, msg = multiMsg }));
                                    Interlocked.Increment(ref _messagesDelivered);
                                    _deliveredTimestamps.Enqueue(DateTime.UtcNow.Ticks);
                                    AppendAudit(now, session.Username, "server", "MULTI", Encoding.UTF8.GetByteCount(multiMsg ?? ""));
                                }
                            //}
                            break;
                        case "BROADCAST":

                            var jsonDocBroad = JsonDocument.Parse(line);
                            string messageBroad = jsonDocBroad.RootElement
                                .GetProperty("payload")
                                .GetProperty("msg")
                                .GetString();
                           // var broadcastMsg = pkt.payload.GetProperty("msg").GetString();
                            foreach (var kv in _sessions)
                            {
                                //if (kv.Key == session.Username) continue;
                                kv.Value.Enqueue(MakeEnvelope("INCOMING_BROADCAST", new { from = session.Username, msg = messageBroad }));
                                Interlocked.Increment(ref _messagesDelivered);
                                _deliveredTimestamps.Enqueue(DateTime.UtcNow.Ticks);
                                AppendAudit(now, session.Username, kv.Key, "BROADCAST", Encoding.UTF8.GetByteCount(messageBroad ?? ""));
                            }
                            break;
                        case "HEARTBEAT":
                            break;
                    }
                }
            }
            catch (IOException ex) {
            
            }
            finally
            {
                await DisconnectSession(session.Username);
            }
        }

        private string MakeEnvelope(string type, object payload)
        {
            var envelope = new { type, payload, meta = new { enqueuedAtUtc = DateTime.UtcNow.ToString("o") } };
            return JsonSerializer.Serialize(envelope);
        }

        private void AppendAudit(DateTime ts, string from, string to, string type, int bytes)
        {
            try { File.AppendAllText(_auditPath, $"{ts:o}\tfrom={from}\tto={to}\ttype={type}\tbytes={bytes}{Environment.NewLine}"); } catch { }
        }

        private async Task DisconnectSession(string username)
        {
            if (_sessions.TryRemove(username, out var s))
            {
                _log?.Invoke($"{username} disconnected");
                s.Close();
                _updateUsers?.Invoke(_sessions.Keys.ToArray());
            }
            await Task.CompletedTask;
        }

        private async Task IdleLoop()
        {
            var timeout = TimeSpan.FromSeconds(120);
            while (!_cts.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in _sessions.ToArray()) if (now - kv.Value.LastActivity > timeout) await DisconnectSession(kv.Key);
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }

        private void RecordLatency(long us)
        {
            lock (_latLock) { _latencies.Add(us); if (_latencies.Count > 2000) _latencies.RemoveRange(0, _latencies.Count - 1000); }
        }

        private async Task StatsLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                long recent;
                var oneSecAgo = DateTime.UtcNow.AddSeconds(-1).Ticks;
                var temp = new List<long>();
                while (_deliveredTimestamps.TryDequeue(out var t)) if (t >= oneSecAgo) temp.Add(t);
                foreach (var t in temp) _deliveredTimestamps.Enqueue(t);
                recent = temp.Count;

                long min = 0, max = 0, avg = 0;
                lock (_latLock) { if (_latencies.Count > 0) { min = _latencies.Min(); max = _latencies.Max(); avg = (long)_latencies.Average(); } }
                _log?.Invoke($"[STAT] online={_sessions.Count} totalRecv={_messagesReceived} totalDelivered={_messagesDelivered} msgs/sec={recent} latency_us(min/avg/max)={min}/{avg}/{max}");
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
    }

    public class ClientSession
    {
        public string Username { get; }
        public TcpClient Tcp { get; }
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
        private readonly BlockingCollection<string> _sendQueue = new();
        public DateTime LastActivity { get; private set; }

        public ClientSession(string username, TcpClient tcp, StreamReader reader, StreamWriter writer) { Username = username; Tcp = tcp; Reader = reader; Writer = writer; UpdateActivity(); }
        public void Enqueue(string msg) => _sendQueue.Add(msg);
        public void UpdateActivity() => LastActivity = DateTime.UtcNow;

        public async Task SendLoopAsync(CancellationToken ct, Action<long> recordLatency)
        {
            try
            {
                foreach (var msg in _sendQueue.GetConsumingEnumerable(ct))
                {
                    try
                    {
                        DateTime enq = DateTime.UtcNow;
                        try
                        {
                            using var doc = JsonDocument.Parse(msg);
                            if (doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("enqueuedAtUtc", out var ts))
                            {
                                if (DateTime.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                                    enq = parsed;
                            }
                        }
                        catch { }

                        var now = DateTime.UtcNow;
                        var latencyUs = (long)((now - enq).TotalMilliseconds * 1000);
                        recordLatency?.Invoke(latencyUs);

                        await Writer.WriteLineAsync(msg);
                    }
                    catch { break; }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Close() { try { _sendQueue.CompleteAdding(); } catch { } try { Tcp.Close(); } catch { } }
    }
}
