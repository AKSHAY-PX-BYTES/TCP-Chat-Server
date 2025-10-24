using System;
using System.Windows.Forms;

namespace ChatClientWin
{
    public partial class LoginForm : Form
    {
        public LoginForm() { InitializeComponent(); }

        private void btnLogin_Click(object sender, EventArgs e)
        {
            var user = txtUser.Text.Trim();
            var pass = txtPass.Text;
            if (string.IsNullOrEmpty(user)) { MessageBox.Show("Enter username"); return; }
            var chat = new ChatForm(user, pass, txtServer.Text.Trim(), int.TryParse(txtPort.Text, out var p) ? p : 5000);
            this.Hide();
            chat.ShowDialog();
            this.Show();
        }
    }
}
