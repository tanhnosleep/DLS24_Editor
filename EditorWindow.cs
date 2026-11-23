using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Dls24Editor
{
    public sealed class EditorWindow : Form
    {
        private const int ResultLimit = 350;
        private static readonly Color Ink = Color.FromArgb(25, 38, 50);
        private static readonly Color Accent = Color.FromArgb(0, 104, 90);
        private static readonly Color Canvas = Color.FromArgb(244, 247, 249);
        private static readonly string[] StatNames =
        {
            "Tăng tốc (ACC)", "Tốc độ (SPD)", "Thể lực (STA)", "Sức mạnh (STR)",
            "Tranh bóng (TAC)", "Kiểm soát (CON)", "Dứt điểm (SHO)", "Chuyền bóng (PAS)",
            "Thủ môn 1 (GK1)", "Thủ môn 2 (GK2)"
        };
        private static readonly string[] PositionNames =
        {
            "0 GK", "1 LB", "2 RB", "3 CB", "8 DM", "11 CM", "16 RM", "17 LM", "18 AM",
            "19 ST", "20 LW", "21 RW"
        };

        private readonly string workspace;
        private readonly string toolDirectory;
        private readonly TextBox search = new TextBox();
        private readonly ListView results = new ListView();
        private readonly Label resultCount = new Label();
        private readonly Label source = new Label();
        private readonly Label playerHeading = new Label();
        private readonly Label playerMeta = new Label();
        private readonly Label status = new Label();
        private readonly Label ovrPreview = new Label();
        private readonly TextBox ovrTarget = new TextBox { Width = 54, MaxLength = 4, TextAlign = HorizontalAlignment.Right, AccessibleName = "OVR mục tiêu" };
        private readonly Button applyOvr = MakeButton("Áp dụng OVR");
        private readonly ToolTip tips = new ToolTip();
        private readonly Timer searchDelay = new Timer { Interval = 160 };
        private readonly TextBox name = new TextBox();
        private readonly TextBox surname = new TextBox();
        private readonly TextBox displayName = new TextBox();
        private readonly TextBox appearance = new TextBox();
        private readonly NumericUpDown nationality = Number(65535);
        private readonly NumericUpDown height = Number(65535);
        private readonly NumericUpDown weight = Number(65535);
        private readonly ComboBox primary = PositionBox();
        private readonly ComboBox secondary = PositionBox();
        private readonly NumericUpDown foot = Number(65535);
        private readonly NumericUpDown flags = Number(65535);
        private readonly NumericUpDown[] stats = StatNames.Select(_ => Number(100, 1)).ToArray();
        private readonly TextBox recordHex = RawBox();
        private readonly TextBox marketJson = RawBox();
        private readonly TabControl tabs = new TabControl();
        private readonly Button save = MakeButton("Lưu cầu thủ", true);
        private readonly Button reset = MakeButton("Hoàn tác ô nhập");
        private readonly Button export = MakeButton("Xuất 2 tệp .dat");
        private readonly Button build = MakeButton("Build APK");
        private GameData game;
        private PlayerDetails player;
        private string archivePath;
        private bool filling;
        private bool dirty;
        private bool busy;
        private int revision;
        private int exportedRevision;

        public EditorWindow(string workspace, string toolDirectory, string initialPath)
        {
            this.workspace = workspace;
            this.toolDirectory = toolDirectory;
            Text = "DLS24 Player Editor";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1030, 720);
            var workingArea = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1230, workingArea.Width), Math.Min(830, workingArea.Height));
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            ForeColor = Ink;
            BackColor = Canvas;
            KeyPreview = true;
            BuildLayout();
            WireEvents();
            ClearPlayer();
            UpdateButtons();
            if (!string.IsNullOrEmpty(initialPath))
                Shown += async (sender, args) => await LoadArchive(initialPath);
        }

        public static void Run(string workspace, string toolDirectory, string initialPath)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var window = new EditorWindow(workspace, toolDirectory, initialPath);
            Application.Run(window);
        }

        private void BuildLayout()
        {
            var root = Table(1, 4);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            Controls.Add(root);

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Ink, Margin = Padding.Empty };
            var title = new Label
            {
                Text = "DLS24 Player Editor", ForeColor = Color.White, AutoSize = true,
                Location = new Point(19, 8), Font = new Font("Segoe UI", 18, FontStyle.Bold)
            };
            source.Text = "Mở APK hoặc hai tệp dữ liệu để bắt đầu";
            source.ForeColor = Color.FromArgb(202, 221, 227);
            source.Location = new Point(22, 47);
            source.AutoSize = true;
            header.Controls.AddRange(new Control[] { title, source });
            root.Controls.Add(header, 0, 0);

            var toolbar = Bar(new Padding(15, 7, 10, 5));
            var open = MakeButton("Mở APK / ZIP");
            var openDat = MakeButton("Mở cặp .dat");
            open.Click += async (sender, args) => await ChooseArchive();
            openDat.Click += async (sender, args) => await ChooseDatFiles();
            toolbar.Controls.AddRange(new Control[] { open, openDat, export, build });
            root.Controls.Add(toolbar, 0, 1);

            var body = Table(2, 1);
            body.Padding = new Padding(18, 3, 18, 3);
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 350));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(body, 0, 2);

            var left = Table(1, 4);
            left.Margin = new Padding(0, 0, 14, 0);
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.Controls.Add(Heading("Tìm cầu thủ", 12), 0, 0);
            search.PlaceholderText = "Nhập tên hoặc PID";
            search.Dock = DockStyle.Fill;
            left.Controls.Add(search, 0, 1);
            resultCount.Dock = DockStyle.Fill;
            resultCount.TextAlign = ContentAlignment.MiddleLeft;
            left.Controls.Add(resultCount, 0, 2);
            results.Dock = DockStyle.Fill;
            results.View = View.Details;
            results.FullRowSelect = true;
            results.MultiSelect = false;
            results.HideSelection = false;
            results.Columns.Add("PID", 61);
            results.Columns.Add("Cầu thủ", 199);
            results.Columns.Add("OVR", 50, HorizontalAlignment.Right);
            left.Controls.Add(results, 0, 3);
            body.Controls.Add(left, 0, 0);

            var right = Table(1, 4);
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            playerHeading.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            playerHeading.Dock = DockStyle.Fill;
            playerHeading.AutoEllipsis = true;
            playerMeta.Dock = DockStyle.Fill;
            playerMeta.AutoEllipsis = true;
            right.Controls.Add(playerHeading, 0, 0);
            right.Controls.Add(playerMeta, 0, 1);
            tabs.Dock = DockStyle.Fill;
            AddStatsTab();
            AddProfileTab();
            AddRawTab("Dữ liệu chợ", marketJson);
            AddRawTab("Bản ghi DBp", recordHex);
            right.Controls.Add(tabs, 0, 2);
            var editBar = Bar(new Padding(0, 5, 0, 0));
            editBar.Controls.AddRange(new Control[] { save, reset });
            right.Controls.Add(editBar, 0, 3);
            body.Controls.Add(right, 1, 0);

            status.Dock = DockStyle.Fill;
            status.Padding = new Padding(20, 2, 20, 2);
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            status.BackColor = Color.FromArgb(230, 239, 240);
            status.Margin = Padding.Empty;
            root.Controls.Add(status, 0, 3);
            SetStatus("Chọn gói DLS24 để xem dữ liệu cầu thủ.");
        }

        private void AddStatsTab()
        {
            var tab = new TabPage("Chỉ số") { BackColor = Color.White };
            var panel = Table(4, 8);
            panel.Padding = new Padding(15, 12, 15, 10);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            for (int row = 0; row < 5; row++)
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

            var ratingBar = Bar(Padding.Empty);
            var targetLabel = new Label { Text = "OVR mục tiêu", AutoSize = true, Margin = new Padding(0, 5, 8, 0) };
            ovrTarget.Margin = new Padding(0, 2, 8, 0);
            ovrPreview.AutoSize = true;
            ovrPreview.Margin = new Padding(12, 5, 0, 0);
            ratingBar.Controls.AddRange(new Control[] { targetLabel, ovrTarget, applyOvr, ovrPreview });
            panel.Controls.Add(ratingBar, 0, 0);
            panel.SetColumnSpan(ratingBar, 4);

            var hint = new Label { Text = "Áp dụng để công cụ điều chỉnh chỉ số theo vị trí, rồi xem lại trước khi lưu.", Dock = DockStyle.Fill };
            panel.Controls.Add(hint, 0, 1);
            panel.SetColumnSpan(hint, 4);
            for (int index = 0; index < stats.Length; index++)
            {
                stats[index].AccessibleName = StatNames[index];
                stats[index].Dock = DockStyle.Top;
                AddField(panel, StatNames[index], stats[index], 2 + index % 5, index < 5 ? 0 : 2);
            }
            tab.Controls.Add(panel);
            tabs.TabPages.Add(tab);
        }

        private void AddProfileTab()
        {
            var tab = new TabPage("Hồ sơ") { BackColor = Color.White };
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var panel = Table(2, 11);
            panel.AutoSize = true;
            panel.Dock = DockStyle.Top;
            panel.Padding = new Padding(15, 8, 18, 8);
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 157));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 11; row++)
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            AddField(panel, "Tên", name, 0);
            AddField(panel, "Họ / tên trên áo", surname, 1);
            AddField(panel, "Biệt danh hiển thị", displayName, 2);
            AddField(panel, "Mã quốc tịch", nationality, 3);
            AddField(panel, "Chiều cao (cm)", height, 4);
            AddField(panel, "Cân nặng (kg)", weight, 5);
            AddField(panel, "Vị trí chính", primary, 6);
            AddField(panel, "Vị trí phụ", secondary, 7);
            AddField(panel, "Mã chân thuận", foot, 8);
            AddField(panel, "Cờ DBp (số thập phân)", flags, 9);
            AddField(panel, "Ngoại hình 3D (hex)", appearance, 10);
            tips.SetToolTip(foot, "0 = trái, 1 = phải, 2 = hai chân. Giữ nguyên mã khác nếu không cần sửa.");
            tips.SetToolTip(appearance, "24 byte, viết bằng 48 ký tự hex.");
            scroller.Controls.Add(panel);
            tab.Controls.Add(scroller);
            tabs.TabPages.Add(tab);
        }

        private void AddRawTab(string title, TextBox content)
        {
            var tab = new TabPage(title) { Padding = new Padding(9), BackColor = Color.White };
            tab.Controls.Add(content);
            tabs.TabPages.Add(tab);
        }

        private void WireEvents()
        {
            search.TextChanged += (sender, args) =>
            {
                searchDelay.Stop();
                searchDelay.Start();
            };
            searchDelay.Tick += (sender, args) =>
            {
                searchDelay.Stop();
                RefreshResults();
            };
            results.SelectedIndexChanged += (sender, args) =>
            {
                if (filling || busy || results.SelectedItems.Count == 0) return;
                int pid = (int)results.SelectedItems[0].Tag;
                if (player?.Pid == pid) return;
                if (ConfirmDiscardEdit())
                    ShowPlayer(game.GetPlayer(pid));
                else
                    RestoreSelection();
            };
            foreach (var box in new[] { name, surname, displayName, appearance })
                box.TextChanged += MarkEdited;
            foreach (var box in stats.Concat(new[] { nationality, height, weight, foot, flags }))
            {
                box.ValueChanged += MarkEdited;
                box.TextChanged += MarkEdited;
            }
            foreach (var box in stats)
                box.Validated += (sender, args) => UpdateOvrPreview();
            primary.TextChanged += (sender, args) =>
            {
                MarkEdited(sender, args);
                UpdateOvrPreview();
            };
            secondary.TextChanged += MarkEdited;
            applyOvr.Click += (sender, args) => ApplyOvr();
            ovrTarget.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter || !applyOvr.Enabled) return;
                args.SuppressKeyPress = true;
                ApplyOvr();
            };
            save.Click += (sender, args) => SavePlayer();
            reset.Click += (sender, args) =>
            {
                if (player != null) ShowPlayer(game.GetPlayer(player.Pid));
            };
            export.Click += async (sender, args) => await ExportDat();
            build.Click += async (sender, args) => await BuildApk();
            FormClosing += (sender, args) => args.Cancel = busy || !ConfirmChangeSource();
            KeyDown += (sender, args) =>
            {
                if (args.Control && args.KeyCode == Keys.F && !busy)
                {
                    search.Focus();
                    search.SelectAll();
                    args.SuppressKeyPress = true;
                }
                if (args.Control && args.KeyCode == Keys.S && save.Enabled)
                {
                    SavePlayer();
                    args.SuppressKeyPress = true;
                }
            };
        }

        private async Task ChooseArchive()
        {
            using var dialog = FilePicker("Chọn gói DLS24", "Gói DLS24 (*.apk;*.zip)|*.apk;*.zip",
                archivePath == null ? workspace : Path.GetDirectoryName(archivePath));
            if (dialog.ShowDialog(this) == DialogResult.OK && ConfirmChangeSource())
                await LoadArchive(dialog.FileName);
        }

        private async Task LoadArchive(string path)
        {
            await Work("Đang đọc dữ liệu trong gói DLS24...", async () =>
            {
                var loaded = await Task.Run(() => GameData.LoadArchive(path));
                SetGame(loaded, path, Path.GetFileName(path));
            });
        }

        private async Task ChooseDatFiles()
        {
            using var first = FilePicker("Chọn dls24-local-bootstrap.dat", "Tệp bootstrap (*.dat)|*.dat", workspace);
            first.FileName = "dls24-local-bootstrap.dat";
            if (first.ShowDialog(this) != DialogResult.OK) return;
            using var second = FilePicker("Chọn dls24-local-market.dat", "Tệp market (*.dat)|*.dat", Path.GetDirectoryName(first.FileName));
            second.FileName = "dls24-local-market.dat";
            if (second.ShowDialog(this) != DialogResult.OK || !ConfirmChangeSource()) return;
            await Work("Đang giải mã hai tệp dữ liệu...", async () =>
            {
                var loaded = await Task.Run(() => GameData.LoadFiles(first.FileName, second.FileName));
                SetGame(loaded, null, "Cặp .dat trong " + Path.GetDirectoryName(first.FileName));
            });
        }

        private void SetGame(GameData loaded, string path, string description)
        {
            game = loaded;
            archivePath = path;
            source.Text = description;
            revision = exportedRevision = 0;
            search.Clear();
            ClearPlayer();
            RefreshResults();
            SetStatus($"Đã giải mã dữ liệu của {game.Count:N0} cầu thủ.");
            search.Focus();
        }

        private void RefreshResults()
        {
            if (game == null) return;
            string query = search.Text.Trim();
            var matches = game.Search(query);
            filling = true;
            results.BeginUpdate();
            try
            {
                results.Items.Clear();
                foreach (var match in matches.Take(ResultLimit))
                {
                    var row = new ListViewItem(match.Pid.ToString(CultureInfo.InvariantCulture)) { Tag = match.Pid };
                    row.SubItems.Add(match.DisplayName);
                    row.SubItems.Add(match.Rating.ToString(CultureInfo.InvariantCulture));
                    results.Items.Add(row);
                    if (match.Pid == player?.Pid) row.Selected = true;
                }
                resultCount.Text = query.Length == 0 ? "Nhập tên để tìm trong dữ liệu"
                    : matches.Length == 0 ? "Không có cầu thủ"
                    : matches.Length > ResultLimit ? $"Hiện {ResultLimit:N0} / {matches.Length:N0} cầu thủ"
                    : $"{matches.Length:N0} cầu thủ";
            }
            finally
            {
                results.EndUpdate();
                filling = false;
            }
        }

        private void RestoreSelection()
        {
            filling = true;
            try
            {
                foreach (ListViewItem row in results.Items)
                    row.Selected = (int)row.Tag == player?.Pid;
            }
            finally { filling = false; }
        }

        private void ClearPlayer()
        {
            player = null;
            dirty = false;
            playerHeading.Text = "Chọn một cầu thủ";
            playerMeta.Text = "";
            ovrPreview.Text = "";
            ovrTarget.Clear();
            foreach (var box in new[] { name, surname, displayName, appearance, recordHex, marketJson })
                box.Clear();
            foreach (var box in stats.Concat(new[] { nationality, height, weight, foot, flags }))
                box.Value = 0;
            primary.Text = secondary.Text = "";
            UpdateButtons();
        }

        private void ShowPlayer(PlayerDetails value)
        {
            filling = true;
            try
            {
                player = value;
                ovrTarget.Text = value.Rating.ToString(CultureInfo.InvariantCulture);
                name.Text = value.Name;
                surname.Text = value.Surname;
                displayName.Text = value.DisplayName;
                nationality.Value = value.Nationality;
                height.Value = value.Height;
                weight.Value = value.Weight;
                primary.Text = PositionLabel(value.PrimaryPosition);
                secondary.Text = PositionLabel(value.SecondaryPosition);
                foot.Value = value.PreferredFoot;
                flags.Value = value.Flags;
                appearance.Text = value.AppearanceHex;
                for (int index = 0; index < stats.Length; index++)
                {
                    stats[index].Maximum = Math.Max(100m, value.Stats[index] / 10m);
                    stats[index].Value = value.Stats[index] / 10m;
                }
                string heading = string.IsNullOrWhiteSpace(value.DisplayName) ? (value.Name + " " + value.Surname).Trim() : value.DisplayName;
                playerHeading.Text = $"{heading}  ·  PID {value.Pid}";
                playerMeta.Text = value.HasMarket ? $"OVR {value.Rating}    ·    Giá {value.Price:N0} xu    ·    {PositionLabel(value.PrimaryPosition)}"
                    : $"PID {value.Pid} không có hồ sơ trong chợ";
                recordHex.Text = FormatRecord(value.RecordHex);
                marketJson.Text = value.MarketJson ?? "Cầu thủ này không có hồ sơ trong chợ.";
                dirty = false;
            }
            finally
            {
                filling = false;
                UpdateButtons();
                UpdateOvrPreview();
            }
        }

        private PlayerDetails ReadRatingInputs()
        {
            return new PlayerDetails
            {
                Pid = player.Pid,
                PrimaryPosition = ReadPosition(primary, "vị trí chính"),
                Stats = stats.Select(box => (int)(box.Value * 10)).ToArray()
            };
        }

        private void UpdateOvrPreview()
        {
            if (filling || player == null) return;
            try
            {
                ovrPreview.Text = $"OVR theo chỉ số: {game.PreviewRating(ReadRatingInputs()) / 100}";
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is OverflowException)
            {
                ovrPreview.Text = "Kiểm tra vị trí và chỉ số";
            }
        }

        private void ApplyOvr()
        {
            if (player == null || busy) return;
            try
            {
                if (!int.TryParse(ovrTarget.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int target))
                    throw new InvalidOperationException("Nhập OVR mục tiêu là số nguyên.");

                var edit = ReadRatingInputs();
                int[] adjusted = game.AdjustStatsForOvr(edit, target);
                if (edit.Stats.SequenceEqual(adjusted))
                {
                    SetStatus($"Chỉ số đang nhập đã cho OVR {target}.");
                    return;
                }

                filling = true;
                try
                {
                    for (int index = 0; index < adjusted.Length; index++)
                        stats[index].Value = adjusted[index] / 10m;
                }
                finally { filling = false; }

                dirty = true;
                UpdateButtons();
                UpdateOvrPreview();
                SetStatus($"Đã điều chỉnh chỉ số để đạt OVR {target}. Bấm Lưu cầu thủ để đồng bộ dữ liệu.");
            }
            catch (Exception error) { ShowError(error); }
        }

        private void MarkEdited(object sender, EventArgs args)
        {
            if (filling || player == null) return;
            dirty = true;
            UpdateButtons();
        }

        private bool SavePlayer()
        {
            if (!dirty || player == null) return true;
            try
            {
                ValidateChildren();
                var edit = ReadRatingInputs();
                edit.Name = name.Text;
                edit.Surname = surname.Text;
                edit.DisplayName = displayName.Text;
                edit.Nationality = (int)nationality.Value;
                edit.Height = (int)height.Value;
                edit.Weight = (int)weight.Value;
                edit.SecondaryPosition = ReadPosition(secondary, "vị trí phụ");
                edit.PreferredFoot = (int)foot.Value;
                edit.Flags = (int)flags.Value;
                edit.AppearanceHex = appearance.Text;
                var warnings = game.SavePlayer(edit);
                revision++;
                ShowPlayer(game.GetPlayer(edit.Pid));
                RefreshResults();
                SetStatus($"Đã lưu PID {edit.Pid} và đồng bộ dữ liệu. Chọn Xuất 2 tệp .dat hoặc Build APK.");
                if (warnings.Length > 0)
                    MessageBox.Show(this, string.Join(Environment.NewLine, warnings), "DLS24 Player Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            catch (Exception error) { ShowError(error); return false; }
        }

        private async Task ExportDat()
        {
            if (!SavePlayer()) return;
            using var dialog = new FolderBrowserDialog { Description = "Chọn nơi lưu thư mục chứa hai tệp .dat", SelectedPath = workspace };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string parent = dialog.SelectedPath;
            await Work("Đang mã hóa và xuất hai tệp .dat...", async () =>
            {
                string output = await Task.Run(() =>
                {
                    var data = game.BuildDatFiles();
                    string target = Path.Combine(parent, "DLS24-dat-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
                    string pending = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    Directory.CreateDirectory(pending);
                    string bootstrap = Path.Combine(pending, "dls24-local-bootstrap.dat");
                    string market = Path.Combine(pending, "dls24-local-market.dat");
                    try
                    {
                        File.WriteAllBytes(bootstrap, data.Bootstrap);
                        File.WriteAllBytes(market, data.Market);
                        Directory.Move(pending, target);
                        return target;
                    }
                    finally
                    {
                        if (Directory.Exists(pending))
                        {
                            File.Delete(bootstrap);
                            File.Delete(market);
                            Directory.Delete(pending);
                        }
                    }
                });
                exportedRevision = revision;
                SetStatus("Đã xuất " + output);
            });
        }

        private async Task BuildApk()
        {
            if (!SavePlayer()) return;
            string input = archivePath;
            if (input == null)
            {
                using var basePicker = FilePicker("Chọn APK / ZIP gốc cùng bản với hai tệp .dat", "Gói DLS24 (*.apk;*.zip)|*.apk;*.zip", workspace);
                if (basePicker.ShowDialog(this) != DialogResult.OK) return;
                input = basePicker.FileName;
            }
            using var options = new SigningDialog(workspace, toolDirectory);
            if (options.ShowDialog(this) != DialogResult.OK) return;
            using var output = new SaveFileDialog
            {
                Title = "Chọn tên APK xuất ra", Filter = "Android APK (*.apk)|*.apk", DefaultExt = "apk",
                AddExtension = true, OverwritePrompt = false, InitialDirectory = workspace,
                FileName = "DLS24-edited-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".apk"
            };
            if (output.ShowDialog(this) != DialogResult.OK) return;
            string inputPath = input;
            string outputPath = output.FileName;
            string tools = options.BuildTools, keystore = options.Keystore, alias = options.Alias;
            string password = options.Password, keyPassword = options.KeyPassword;
            IProgress<string> progress = new Progress<string>(SetStatus);
            await Work("Đang mã hóa dữ liệu để build APK...", async () =>
            {
                string path = await Task.Run(() =>
                {
                    game.ValidateBaseArchive(inputPath);
                    var data = game.BuildDatFiles();
                    return ApkBuilder.Build(inputPath, outputPath, data.Bootstrap, data.Market,
                        tools, keystore, alias, password, keyPassword, progress.Report);
                });
                exportedRevision = revision;
                SetStatus("Đã ký và kiểm tra APK · " + path);
                MessageBox.Show(this, "Đã xuất APK đã ký.\r\n\r\n" + path,
                    "Build APK hoàn tất", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        private async Task Work(string message, Func<Task> action)
        {
            if (busy) return;
            busy = true;
            Enabled = false;
            UseWaitCursor = true;
            SetStatus(message);
            try { await action(); }
            catch (Exception error) { ShowError(error); }
            finally
            {
                busy = false;
                Enabled = true;
                UseWaitCursor = false;
                UpdateButtons();
            }
        }

        private bool ConfirmDiscardEdit()
        {
            if (!dirty) return true;
            return MessageBox.Show(this, "Bỏ các ô đang sửa của cầu thủ hiện tại?", "Thay đổi chưa lưu",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private bool ConfirmChangeSource()
        {
            if (!dirty && revision == exportedRevision) return true;
            return MessageBox.Show(this, "Các thay đổi chưa được xuất hết. Bỏ các thay đổi này?",
                "Thay đổi chưa xuất", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private void UpdateButtons()
        {
            tabs.Enabled = player != null && !busy;
            search.Enabled = game != null && !busy;
            applyOvr.Enabled = player != null && !busy;
            save.Enabled = reset.Enabled = player != null && dirty && !busy;
            export.Enabled = build.Enabled = game != null && !busy;
        }

        private void SetStatus(string message)
        {
            status.Text = message;
            tips.SetToolTip(status, message);
        }

        private void ShowError(Exception error)
        {
            SetStatus(error.Message);
            MessageBox.Show(this, error.Message, "DLS24 Player Editor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static string FormatRecord(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string hex = new string(raw.Where(Uri.IsHexDigit).ToArray());
            var lines = new System.Collections.Generic.List<string>();
            for (int offset = 0; offset < hex.Length; offset += 32)
            {
                string part = hex.Substring(offset, Math.Min(32, hex.Length - offset));
                var bytes = Enumerable.Range(0, part.Length / 2).Select(index => part.Substring(index * 2, 2));
                lines.Add($"{offset / 2:X4}    {string.Join(" ", bytes)}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static int ReadPosition(ComboBox box, string field)
        {
            string token = box.Text.Trim().Split(' ')[0];
            if (!byte.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out byte value))
                throw new InvalidOperationException($"Hãy nhập mã {field} từ 0 đến 255.");
            return value;
        }

        private static string PositionLabel(int code)
        {
            return PositionNames.FirstOrDefault(item => item.StartsWith(code + " ", StringComparison.Ordinal))
                ?? code.ToString(CultureInfo.InvariantCulture);
        }

        internal static TableLayoutPanel Table(int columns, int rows)
        {
            return new TableLayoutPanel
            {
                ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill, Margin = Padding.Empty
            };
        }

        internal static FlowLayoutPanel Bar(Padding padding)
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = padding, Margin = Padding.Empty, WrapContents = false
            };
        }

        internal static Button MakeButton(string label, bool accent = false)
        {
            var button = new Button
            {
                Text = label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 34, Padding = new Padding(10, 0, 10, 0), Margin = new Padding(0, 0, 8, 0)
            };
            if (accent)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.BackColor = Accent;
                button.ForeColor = Color.White;
            }
            return button;
        }

        private static Label Heading(string label, int size)
        {
            return new Label
            {
                Text = label, Font = new Font("Segoe UI", size, FontStyle.Bold), Dock = DockStyle.Fill
            };
        }

        private static ComboBox PositionBox()
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            box.Items.AddRange(PositionNames);
            return box;
        }

        private static NumericUpDown Number(decimal maximum, int decimals = 0)
        {
            return new NumericUpDown
            {
                Maximum = maximum, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : 0.1m,
                TextAlign = HorizontalAlignment.Right
            };
        }

        private static TextBox RawBox()
        {
            return new TextBox
            {
                Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both,
                WordWrap = false, BackColor = Color.White, Font = new Font("Consolas", 10)
            };
        }

        private static void AddField(TableLayoutPanel table, string label, Control field, int row, int column = 0)
        {
            var caption = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft, Padding = new Padding(0, 5, 0, 0) };
            field.Dock = DockStyle.Top;
            field.AccessibleName = label;
            table.Controls.Add(caption, column, row);
            table.Controls.Add(field, column + 1, row);
        }

        internal static OpenFileDialog FilePicker(string title, string filter, string path)
        {
            return new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = path, CheckFileExists = true };
        }
    }
}
