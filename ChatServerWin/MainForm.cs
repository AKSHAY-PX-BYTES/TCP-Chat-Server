using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChatServerWin
{
    public partial class MainForm : Form
    {
        private TcpChatServer _server;

        public MainForm()
        {
            InitializeComponent();
            btnStop.Enabled = false;
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            int port = 5000;
            if (!int.TryParse(txtPort.Text, out port)) port = 5000;
            _server = new TcpChatServer(System.Net.IPAddress.Loopback, port, LogMessage, UpdateUsers);
            await _server.StartAsync();
            btnStart.Enabled = false;
            btnStop.Enabled = true;
            LogMessage("Server started on port " + port);
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            _server?.Stop();
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            LogMessage("Server stopped");
        }

        private void LogMessage(string msg)
        {
            if (InvokeRequired) { Invoke(new Action(()=> LogMessage(msg))); return; }
            lstLogs.AppendText(msg + Environment.NewLine);
        }

        private void UpdateUsers(string[] users)
        {
            if (InvokeRequired) { Invoke(new Action(()=> UpdateUsers(users))); return; }
            lstUsers.Items.Clear();
            lstUsers.Items.AddRange(users);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _server?.Stop();
        }
    }
}
