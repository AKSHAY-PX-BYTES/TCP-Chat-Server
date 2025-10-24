using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChatClientWin
{
    public partial class ChatForm : Form
    {
        private readonly string _user;
        private readonly string _pass;
        private readonly string _host;
        private readonly int _port;

        private TcpClient _tcp;
        private StreamReader _reader;
        private StreamWriter _writer;

        public ChatForm(string user, string pass, string host, int port)
        {
            _user = user; _pass = pass; _host = host; _port = port;
            InitializeComponent();
            lblUser.Text = $"User: {_user}";
            StartClient();
        }

        private async void StartClient()
        {
            try
            {
                _tcp = new TcpClient();
                await _tcp.ConnectAsync(_host, _port);
                var stream = _tcp.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var loginReq = new { type = "LOGIN_REQ", payload = new { username = _user, password = _pass } };
                await _writer.WriteLineAsync(JsonSerializer.Serialize(loginReq));

                var resp = await _reader.ReadLineAsync();
                if (resp == null) { AppendLog("Server closed"); return; }
                var doc = JsonDocument.Parse(resp);
                bool ok = doc.RootElement.GetProperty("payload").GetProperty("ok").GetBoolean();
                if (!ok) { AppendLog("Login failed"); return; }

                AppendLog("Logged in.");
                _ = Task.Run(ReceiveLoop);
                _ = Task.Run(SendHeartbeats);
            }
            catch (Exception ex) { AppendLog("Connect error: " + ex.Message); }
        }

        private async Task SendHeartbeats()
        {
            try
            {
                while (true)
                {
                    var hb = new { type = "HEARTBEAT", payload = new { } };
                    await _writer.WriteLineAsync(JsonSerializer.Serialize(hb));
                    await Task.Delay(20000);
                }
            }
            catch { }
        }

        private async Task ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    var line = await _reader.ReadLineAsync();
                    if (line == null) break;
                    try
                    {
                        var msg = JsonDocument.Parse(line);
                        var type = msg.RootElement.GetProperty("type").GetString() ?? "";
                        var payload = msg.RootElement.GetProperty("payload").ToString();
                        AppendLog($"[{DateTime.Now:T}] {type}: {payload}");
                    }
                    catch { AppendLog("Invalid JSON from server: " + line); }
                }
            }
            catch (Exception ex) { AppendLog("Read error: " + ex.Message); }
        }

        private void AppendLog(string s)
        {
            if (InvokeRequired) { Invoke(new Action(()=> AppendLog(s))); return; }
            txtHistory.AppendText(s + Environment.NewLine);
        }

        private async void btnSend_Click(object sender, EventArgs e)
        {
            var mode = comboMode.SelectedItem?.ToString();
            if (mode == null) { MessageBox.Show("Select mode"); return; }
            var message = txtMessage.Text;
            if (string.IsNullOrWhiteSpace(message)) return;

            object payload;
            string type;
            if (mode == "DM")
            {
                var to = txtTarget.Text.Trim();
                payload = new { to, msg = message };
                type = "DM";
            }
            else if (mode == "MULTI")
            {
                var users = txtTarget.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                payload = new { to = users, msg = message };
                type = "MULTI";
            }
            else // BROADCAST
            {
                payload = new { msg = message };
                type = "BROADCAST";
            }

            var pkt = new { type, payload };
            await _writer.WriteLineAsync(JsonSerializer.Serialize(pkt));
            txtMessage.Clear();
        }

        private void ChatForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try { _tcp?.Close(); } catch { }
        }
    }
}
