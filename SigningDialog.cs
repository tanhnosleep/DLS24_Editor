using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Dls24Editor
{
    internal sealed class SigningDialog : Form
    {
        private readonly TextBox tools = new TextBox();
        private readonly TextBox keystore = new TextBox();
        private readonly TextBox alias = new TextBox { Text = "dls24mod" };
        private readonly TextBox password = new TextBox { UseSystemPasswordChar = true };
        private readonly TextBox keyPassword = new TextBox { UseSystemPasswordChar = true };

        public string BuildTools => tools.Text.Trim();
        public string Keystore => keystore.Text.Trim();
        public string Alias => alias.Text.Trim();
        public string Password => password.Text;
        public string KeyPassword => keyPassword.Text;

        public SigningDialog(string workspace, string toolDirectory)
        {
            Text = "Ký APK";
            ClientSize = new Size(705, 336);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MaximizeBox = MinimizeBox = false;

            string localTools = Path.Combine(toolDirectory, "android-build-tools");
            tools.Text = ApkBuilder.FindBuildTools() ??
                (File.Exists(Path.Combine(localTools, "zipalign.exe")) ? localTools : "");
            string defaultKey = Path.Combine(workspace, "DLS24-mod-key", "dls24-mod.keystore");
            if (File.Exists(defaultKey)) keystore.Text = defaultKey;
            keyPassword.PlaceholderText = "Bỏ trống nếu trùng mật khẩu keystore";

            var panel = EditorWindow.Table(3, 7);
            panel.Padding = new Padding(16, 14, 16, 8);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 164));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            for (int row = 0; row < 5; row++)
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var note = new Label
            {
                Text = "Để cài đè và giữ save, hãy chọn keystore đã ký bản DLS24 đang cài. Có thể tạo khóa mới để ký cho bản cài riêng.",
                Dock = DockStyle.Fill
            };
            panel.Controls.Add(note, 0, 0);
            panel.SetColumnSpan(note, 3);
            AddField(panel, "Android Build Tools", tools, 1);
            AddField(panel, "Tệp keystore", keystore, 2);
            AddField(panel, "Bí danh khóa (alias)", alias, 3);
            AddField(panel, "Mật khẩu keystore", password, 4);
            AddField(panel, "Mật khẩu khóa", keyPassword, 5);

            var browseTools = EditorWindow.MakeButton("Chọn");
            browseTools.Click += (sender, args) =>
            {
                using var folder = new FolderBrowserDialog { Description = "Chọn thư mục chứa zipalign.exe", SelectedPath = BuildTools };
                if (folder.ShowDialog(this) == DialogResult.OK) tools.Text = folder.SelectedPath;
            };
            panel.Controls.Add(browseTools, 2, 1);
            var browseKey = EditorWindow.MakeButton("Chọn");
            browseKey.Click += (sender, args) =>
            {
                using var picker = EditorWindow.FilePicker("Chọn keystore", "Keystore (*.keystore;*.jks;*.p12)|*.keystore;*.jks;*.p12|Tất cả tệp (*.*)|*.*", workspace);
                if (picker.ShowDialog(this) == DialogResult.OK) keystore.Text = picker.FileName;
            };
            panel.Controls.Add(browseKey, 2, 2);

            var buttons = EditorWindow.Bar(new Padding(1, 7, 0, 0));
            var next = EditorWindow.MakeButton("Chọn tệp đầu ra", true);
            var create = EditorWindow.MakeButton("Tạo keystore mới");
            var cancel = EditorWindow.MakeButton("Hủy");
            cancel.DialogResult = DialogResult.Cancel;
            next.Click += (sender, args) =>
            {
                if (!File.Exists(Keystore) || string.IsNullOrWhiteSpace(Alias))
                {
                    MessageBox.Show(this, "Chọn keystore và nhập alias của khóa ký.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            create.Click += (sender, args) => CreateKeystore(workspace);
            buttons.Controls.AddRange(new Control[] { next, create, cancel });
            panel.Controls.Add(buttons, 0, 6);
            panel.SetColumnSpan(buttons, 3);
            Controls.Add(panel);
            AcceptButton = next;
            CancelButton = cancel;
        }

        private void CreateKeystore(string workspace)
        {
            if (Alias.Length == 0 || Password.Length < 6)
            {
                MessageBox.Show(this, "Nhập alias và mật khẩu keystore ít nhất 6 ký tự trước khi tạo.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var picker = new SaveFileDialog
            {
                Title = "Lưu khóa ký mới", InitialDirectory = workspace,
                FileName = "dls24-mod.keystore", Filter = "Keystore (*.keystore)|*.keystore",
                AddExtension = true, DefaultExt = "keystore", OverwritePrompt = false
            };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                UseWaitCursor = true;
                ApkBuilder.CreateKeystore(picker.FileName, Alias, Password);
                keystore.Text = picker.FileName;
                keyPassword.Clear();
                MessageBox.Show(this, "Đã tạo keystore. Hãy giữ lại tệp và mật khẩu để ký các bản cập nhật sau.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { UseWaitCursor = false; }
        }

        private static void AddField(TableLayoutPanel panel, string label, TextBox box, int row)
        {
            panel.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);
            box.Dock = DockStyle.Top;
            box.AccessibleName = label;
            panel.Controls.Add(box, 1, row);
        }
    }
}
