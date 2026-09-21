using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace fardnia_radius_report
{
    // ==========================================
    // 1. Models
    // ==========================================
    public class UsageRecord
    {
        public int RowNum { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public string StartTimeDisplay { get; set; } = string.Empty;
        public long RawUploadBytes { get; set; }
        public long RawDownloadBytes { get; set; }
        public string UploadFormatted { get; set; } = string.Empty;
        public string DownloadFormatted { get; set; } = string.Empty;
        public string TotalFormatted { get; set; } = string.Empty;
    }

    public class UserSummaryRecord
    {
        public int RowNum { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public int SessionCount { get; set; }
        public long RawUploadBytes { get; set; }
        public long RawDownloadBytes { get; set; }
        public string UploadFormatted { get; set; } = string.Empty;
        public string DownloadFormatted { get; set; } = string.Empty;
        public string TotalFormatted { get; set; } = string.Empty;
    }

    // ==========================================
    // 2. Date & Format Helpers
    // ==========================================
    public static class DateConverter
    {
        private static readonly PersianCalendar _persianCalendar = new PersianCalendar();

        public static string ToPersianDate(DateTime gregorianDate)
        {
            int year = _persianCalendar.GetYear(gregorianDate);
            int month = _persianCalendar.GetMonth(gregorianDate);
            int day = _persianCalendar.GetDayOfMonth(gregorianDate);
            return $"{year:D4}/{month:D2}/{day:D2}";
        }

        public static string ToPersianWithTime(DateTime gregorianDate)
        {
            return $"{ToPersianDate(gregorianDate)} {gregorianDate:HH:mm:ss}";
        }

        public static DateTime? PersianToGregorian(string persianDateStr)
        {
            try
            {
                var parts = persianDateStr.Split('/', '-');
                if (parts.Length != 3) return null;

                int year = int.Parse(parts[0]);
                int month = int.Parse(parts[1]);
                int day = int.Parse(parts[2]);

                return _persianCalendar.ToDateTime(year, month, day, 0, 0, 0, 0);
            }
            catch
            {
                return null;
            }
        }
    }

    public static class FormatHelper
    {
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "0 B";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;

            while (Math.Round(number / 1024m) >= 1m && counter < suffixes.Length - 1)
            {
                number /= 1024m;
                counter++;
            }

            return $"{number:N2} {suffixes[counter]}";
        }
    }

    // ==========================================
    // 3. Database Service
    // ==========================================
    public class DatabaseService
    {
        private readonly string _connectionString;
        private readonly string _dbPath;

        public DatabaseService()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            _dbPath = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\TekRADIUSLT.db3"));
            
            if (!File.Exists(_dbPath))
            {
                _dbPath = Path.GetFullPath(Path.Combine(baseDir, @"..\TekRADIUSLT.db3"));
            }

            if (!File.Exists(_dbPath))
            {
                _dbPath = Path.Combine(baseDir, "TekRADIUSLT.db3");
            }

            _connectionString = $"Data Source={_dbPath};Mode=ReadOnly;";
        }

        public void ValidateDatabase()
        {
            if (!File.Exists(_dbPath))
            {
                throw new FileNotFoundException($"فایل دیتابیس TekRADIUS LT در مسیر زیر یافت نشد:\n{_dbPath}");
            }

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Accounting';";
            var result = command.ExecuteScalar();

            if (result == null || Convert.ToInt32(result) == 0)
            {
                throw new Exception("جدول 'Accounting' در دیتابیس یافت نشد.");
            }
        }

        public async Task<List<string>> GetUsersAsync()
        {
            var users = new List<string>();
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT UserName FROM Accounting WHERE UserName IS NOT NULL AND UserName != '' ORDER BY UserName;";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                users.Add(reader.GetString(0));
            }

            return users;
        }

        public async Task<List<string>> GetGroupsAsync()
        {
            var groups = new List<string>();
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT GroupID FROM Groups WHERE GroupID IS NOT NULL AND GroupID != '' ORDER BY GroupID;";

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                groups.Add(reader.GetString(0));
            }

            return groups;
        }

        // کوئری تب اول: ریز اتصالات (آخرین مقدار ثبت شده برای هر SessionId)
        public async Task<(List<UsageRecord> Records, long TotalUpload, long TotalDownload)> GetDetailReportAsync(
            bool enableDateFilter,
            DateTime startDate,
            DateTime endDate,
            bool displayPersianInTable,
            string? userName,
            string? groupName,
            long? minUploadMB,
            long? maxUploadMB,
            long? minDownloadMB,
            long? maxDownloadMB)
        {
            var records = new List<UsageRecord>();
            long grandTotalUpload = 0;
            long grandTotalDownload = 0;

            long? minUploadBytes = minUploadMB.HasValue ? minUploadMB.Value * 1024 * 1024 : null;
            long? maxUploadBytes = maxUploadMB.HasValue ? maxUploadMB.Value * 1024 * 1024 : null;
            long? minDownloadBytes = minDownloadMB.HasValue ? minDownloadMB.Value * 1024 * 1024 : null;
            long? maxDownloadBytes = maxDownloadMB.HasValue ? maxDownloadMB.Value * 1024 * 1024 : null;

            string gStart = startDate.ToString("yyyy-MM-dd 00:00:00");
            string gEnd = endDate.ToString("yyyy-MM-dd 23:59:59");

            string query = @"
                WITH LastSessions AS (
                    SELECT 
                        a.UserName,
                        a.SessionId,
                        a.TimeStamp,
                        (COALESCE(a.InputGigaWord, 0) * 4294967296 + COALESCE(a.InputOcts, 0)) AS TotalUpload,
                        (COALESCE(a.OutputGigaWord, 0) * 4294967296 + COALESCE(a.OutOcts, 0)) AS TotalDownload,
                        ROW_NUMBER() OVER (PARTITION BY a.UserName, a.SessionId ORDER BY a.TimeStamp DESC) as rn
                    FROM Accounting a
                    WHERE (@EnableDateFilter = 0 OR (a.TimeStamp >= @GStart AND a.TimeStamp <= @GEnd))
                      AND (@UserName IS NULL OR a.UserName = @UserName)
                )
                SELECT 
                    ls.UserName,
                    COALESCE(u.GroupName, '') AS GroupName,
                    ls.TimeStamp,
                    ls.TotalUpload,
                    ls.TotalDownload
                FROM LastSessions ls
                LEFT JOIN AcctUserList u ON ls.UserName = u.UserName
                WHERE ls.rn = 1
                  AND (@GroupName IS NULL OR u.GroupName = @GroupName)
                  AND (@MinUpload IS NULL OR ls.TotalUpload >= @MinUpload)
                  AND (@MaxUpload IS NULL OR ls.TotalUpload <= @MaxUpload)
                  AND (@MinDownload IS NULL OR ls.TotalDownload >= @MinDownload)
                  AND (@MaxDownload IS NULL OR ls.TotalDownload <= @MaxDownload)
                ORDER BY ls.TimeStamp DESC;";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = query;

            command.Parameters.AddWithValue("@EnableDateFilter", enableDateFilter ? 1 : 0);
            command.Parameters.AddWithValue("@GStart", gStart);
            command.Parameters.AddWithValue("@GEnd", gEnd);
            command.Parameters.AddWithValue("@UserName", (object?)userName ?? DBNull.Value);
            command.Parameters.AddWithValue("@GroupName", (object?)groupName ?? DBNull.Value);
            command.Parameters.AddWithValue("@MinUpload", (object?)minUploadBytes ?? DBNull.Value);
            command.Parameters.AddWithValue("@MaxUpload", (object?)maxUploadBytes ?? DBNull.Value);
            command.Parameters.AddWithValue("@MinDownload", (object?)minDownloadBytes ?? DBNull.Value);
            command.Parameters.AddWithValue("@MaxDownload", (object?)maxDownloadBytes ?? DBNull.Value);

            using var reader = await command.ExecuteReaderAsync();
            int rowCounter = 1;

            while (await reader.ReadAsync())
            {
                string user = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string group = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string rawTimeStamp = reader.IsDBNull(2) ? "-" : reader.GetString(2);

                long uploadBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                long downloadBytes = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);

                grandTotalUpload += uploadBytes;
                grandTotalDownload += downloadBytes;

                string displayTime = rawTimeStamp;
                if (displayPersianInTable && DateTime.TryParse(rawTimeStamp, out DateTime parsedDate))
                {
                    displayTime = DateConverter.ToPersianWithTime(parsedDate);
                }

                records.Add(new UsageRecord
                {
                    RowNum = rowCounter++,
                    UserName = "\u200E" + user,
                    GroupName = group,
                    StartTimeDisplay = displayTime,
                    RawUploadBytes = uploadBytes,
                    RawDownloadBytes = downloadBytes,
                    UploadFormatted = FormatHelper.FormatBytes(uploadBytes),
                    DownloadFormatted = FormatHelper.FormatBytes(downloadBytes),
                    TotalFormatted = FormatHelper.FormatBytes(uploadBytes + downloadBytes)
                });
            }

            return (records, grandTotalUpload, grandTotalDownload);
        }

        // کوئری تب دوم: مجموع کارکرد هر کاربر (Group By User)
        public async Task<(List<UserSummaryRecord> Records, long TotalUpload, long TotalDownload)> GetUserSummaryReportAsync(
            bool enableDateFilter,
            DateTime startDate,
            DateTime endDate,
            string? userName,
            string? groupName)
        {
            var records = new List<UserSummaryRecord>();
            long grandTotalUpload = 0;
            long grandTotalDownload = 0;

            string gStart = startDate.ToString("yyyy-MM-dd 00:00:00");
            string gEnd = endDate.ToString("yyyy-MM-dd 23:59:59");

            string query = @"
                WITH LastSessions AS (
                    SELECT 
                        a.UserName,
                        a.SessionId,
                        a.TimeStamp,
                        (COALESCE(a.InputGigaWord, 0) * 4294967296 + COALESCE(a.InputOcts, 0)) AS TotalUpload,
                        (COALESCE(a.OutputGigaWord, 0) * 4294967296 + COALESCE(a.OutOcts, 0)) AS TotalDownload,
                        ROW_NUMBER() OVER (PARTITION BY a.UserName, a.SessionId ORDER BY a.TimeStamp DESC) as rn
                    FROM Accounting a
                    WHERE (@EnableDateFilter = 0 OR (a.TimeStamp >= @GStart AND a.TimeStamp <= @GEnd))
                      AND (@UserName IS NULL OR a.UserName = @UserName)
                )
                SELECT 
                    ls.UserName,
                    COALESCE(u.GroupName, '') AS GroupName,
                    COUNT(DISTINCT ls.SessionId) AS SessionCount,
                    SUM(ls.TotalUpload) AS GrandUpload,
                    SUM(ls.TotalDownload) AS GrandDownload
                FROM LastSessions ls
                LEFT JOIN AcctUserList u ON ls.UserName = u.UserName
                WHERE ls.rn = 1
                  AND (@GroupName IS NULL OR u.GroupName = @GroupName)
                GROUP BY ls.UserName, COALESCE(u.GroupName, '')
                ORDER BY (SUM(ls.TotalUpload) + SUM(ls.TotalDownload)) DESC;";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = query;

            command.Parameters.AddWithValue("@EnableDateFilter", enableDateFilter ? 1 : 0);
            command.Parameters.AddWithValue("@GStart", gStart);
            command.Parameters.AddWithValue("@GEnd", gEnd);
            command.Parameters.AddWithValue("@UserName", (object?)userName ?? DBNull.Value);
            command.Parameters.AddWithValue("@GroupName", (object?)groupName ?? DBNull.Value);

            using var reader = await command.ExecuteReaderAsync();
            int rowCounter = 1;

            while (await reader.ReadAsync())
            {
                string user = reader.IsDBNull(0) ? "" : reader.GetString(0);
                string group = reader.IsDBNull(1) ? "" : reader.GetString(1);
                int count = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                long uploadBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                long downloadBytes = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);

                grandTotalUpload += uploadBytes;
                grandTotalDownload += downloadBytes;

                records.Add(new UserSummaryRecord
                {
                    RowNum = rowCounter++,
                    UserName = "\u200E" + user,
                    GroupName = group,
                    SessionCount = count,
                    RawUploadBytes = uploadBytes,
                    RawDownloadBytes = downloadBytes,
                    UploadFormatted = FormatHelper.FormatBytes(uploadBytes),
                    DownloadFormatted = FormatHelper.FormatBytes(downloadBytes),
                    TotalFormatted = FormatHelper.FormatBytes(uploadBytes + downloadBytes)
                });
            }

            return (records, grandTotalUpload, grandTotalDownload);
        }
    }

    // ==========================================
    // 4. Main Form
    // ==========================================
    public class MainForm : Form
    {
        private readonly DatabaseService _dbService;

        private Panel pnlFilters = null!;
        private GroupBox grpFilters = null!;
        private FlowLayoutPanel flowLayout = null!;

        private CheckBox chkEnableDate = null!;
        private ComboBox cmbCalendarType = null!;
        private DateTimePicker datePickerFrom = null!;
        private DateTimePicker datePickerTo = null!;
        private TextBox txtPersianFrom = null!;
        private TextBox txtPersianTo = null!;

        private ComboBox cmbUser = null!;
        private ComboBox cmbGroup = null!;
        private NumericUpDown numMinUpload = null!;
        private NumericUpDown numMaxUpload = null!;
        private NumericUpDown numMinDownload = null!;
        private NumericUpDown numMaxDownload = null!;

        private Button btnSearch = null!;
        private Button btnReset = null!;

        private TabControl mainTabControl = null!;
        private TabPage tabDetails = null!;
        private TabPage tabSummary = null!;

        private DataGridView dgvDetails = null!;
        private DataGridView dgvSummary = null!;

        private Button btnExportDetails = null!;
        private Button btnExportSummary = null!;

        private StatusStrip statusStrip = null!;
        private ToolStripStatusLabel lblStatus = null!;
        private ToolStripStatusLabel lblRecordCount = null!;
        private ToolStripStatusLabel lblTotalUsage = null!;

        public MainForm()
        {
            InitializeComponent();
            _dbService = new DatabaseService();
        }

        private void InitializeComponent()
        {
            pnlFilters = new Panel();
            grpFilters = new GroupBox();
            flowLayout = new FlowLayoutPanel();

            chkEnableDate = new CheckBox { Text = "فیلتر تاریخ", Checked = false, AutoSize = true, Margin = new Padding(5, 8, 5, 5) };
            
            cmbCalendarType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 85 };
            cmbCalendarType.Items.AddRange(new object[] { "شمسی", "میلادی" });
            cmbCalendarType.SelectedIndex = 0;

            datePickerFrom = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110, Enabled = false, Visible = false };
            datePickerTo = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 110, Enabled = false, Visible = false };

            txtPersianFrom = new TextBox { Width = 110, Enabled = false, Text = DateConverter.ToPersianDate(DateTime.Today.AddDays(-30)) };
            txtPersianTo = new TextBox { Width = 110, Enabled = false, Text = DateConverter.ToPersianDate(DateTime.Today) };

            chkEnableDate.CheckedChanged += (s, e) => ToggleDateControls();
            cmbCalendarType.SelectedIndexChanged += (s, e) => ToggleCalendarType();

            cmbUser = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            cmbGroup = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };

            numMinUpload = new NumericUpDown { Maximum = 1000000, Width = 80 };
            numMaxUpload = new NumericUpDown { Maximum = 1000000, Width = 80 };
            numMinDownload = new NumericUpDown { Maximum = 1000000, Width = 80 };
            numMaxDownload = new NumericUpDown { Maximum = 1000000, Width = 80 };

            btnSearch = new Button { Text = "جستجو", Size = new Size(100, 32) };
            btnReset = new Button { Text = "پاک‌سازی", Size = new Size(90, 32) };

            btnSearch.Click += btnSearch_Click;
            btnReset.Click += btnReset_Click;

            // ساخت TabControl
            mainTabControl = new TabControl { Dock = DockStyle.Fill };
            tabDetails = new TabPage { Text = "ریز کارکرد اتصالات (نشست‌ها)" };
            tabSummary = new TabPage { Text = "مجموع کارکرد هر کاربر" };

            // DataGridView تب اول (ریز اتصالات)
            dgvDetails = CreateStandardGrid();
            dgvDetails.Columns.AddRange(new DataGridViewColumn[] {
                new DataGridViewTextBoxColumn { DataPropertyName = "RowNum", HeaderText = "ردیف", FillWeight = 30 },
                new DataGridViewTextBoxColumn { DataPropertyName = "UserName", HeaderText = "نام کاربری", FillWeight = 80 },
                new DataGridViewTextBoxColumn { DataPropertyName = "GroupName", HeaderText = "گروه", FillWeight = 80 },
                new DataGridViewTextBoxColumn { DataPropertyName = "StartTimeDisplay", HeaderText = "زمان ثبت", FillWeight = 110 },
                new DataGridViewTextBoxColumn { DataPropertyName = "UploadFormatted", HeaderText = "حجم آپلود", FillWeight = 70 },
                new DataGridViewTextBoxColumn { DataPropertyName = "DownloadFormatted", HeaderText = "حجم دانلود", FillWeight = 70 },
                new DataGridViewTextBoxColumn { DataPropertyName = "TotalFormatted", HeaderText = "مجموع حجم", FillWeight = 80 }
            });
            dgvDetails.Columns[1].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            btnExportDetails = new Button { Text = "خروجی CSV ریز کارکرد", Size = new Size(150, 30), Dock = DockStyle.Bottom };
            btnExportDetails.Click += (s, e) => ExportGridToCsv(dgvDetails, "Radius_Details");
            tabDetails.Controls.Add(dgvDetails);
            tabDetails.Controls.Add(btnExportDetails);

            // DataGridView تب دوم (مجموع کارکرد هر کاربر)
            dgvSummary = CreateStandardGrid();
            dgvSummary.Columns.AddRange(new DataGridViewColumn[] {
                new DataGridViewTextBoxColumn { DataPropertyName = "RowNum", HeaderText = "ردیف", FillWeight = 30 },
                new DataGridViewTextBoxColumn { DataPropertyName = "UserName", HeaderText = "نام کاربری", FillWeight = 90 },
                new DataGridViewTextBoxColumn { DataPropertyName = "GroupName", HeaderText = "گروه", FillWeight = 80 },
                new DataGridViewTextBoxColumn { DataPropertyName = "SessionCount", HeaderText = "تعداد نشست‌ها", FillWeight = 60 },
                new DataGridViewTextBoxColumn { DataPropertyName = "UploadFormatted", HeaderText = "مجموع آپلود", FillWeight = 80 },
                new DataGridViewTextBoxColumn { DataPropertyName = "DownloadFormatted", HeaderText = "مجموع دانلود", FillWeight = 80 },
                new DataGridViewTextBoxColumn { DataPropertyName = "TotalFormatted", HeaderText = "کل حجم مصرفی", FillWeight = 90 }
            });
            dgvSummary.Columns[1].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            btnExportSummary = new Button { Text = "خروجی CSV مجموع کاربران", Size = new Size(160, 30), Dock = DockStyle.Bottom };
            btnExportSummary.Click += (s, e) => ExportGridToCsv(dgvSummary, "Radius_User_Summary");
            tabSummary.Controls.Add(dgvSummary);
            tabSummary.Controls.Add(btnExportSummary);

            mainTabControl.TabPages.Add(tabDetails);
            mainTabControl.TabPages.Add(tabSummary);

            // StatusStrip
            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel { Text = "آماده به کار" };
            lblRecordCount = new ToolStripStatusLabel { Text = "تعداد رکوردها: 0" };
            lblTotalUsage = new ToolStripStatusLabel { Text = "مجموع: 0 B" };
            statusStrip.Items.AddRange(new ToolStripItem[] { lblStatus, lblRecordCount, lblTotalUsage });

            // چیدمان فیلترها
            flowLayout.Dock = DockStyle.Fill;
            flowLayout.AutoScroll = true;
            flowLayout.Padding = new Padding(5);

            flowLayout.Controls.Add(chkEnableDate);
            AddFilterControl("نوع تقویم:", cmbCalendarType);

            Panel pnlFrom = new Panel { Size = new Size(180, 35), Margin = new Padding(2) };
            pnlFrom.Controls.Add(new Label { Text = "از تاریخ:", AutoSize = true, Location = new Point(120, 8) });
            pnlFrom.Controls.Add(txtPersianFrom);
            pnlFrom.Controls.Add(datePickerFrom);
            flowLayout.Controls.Add(pnlFrom);

            Panel pnlTo = new Panel { Size = new Size(180, 35), Margin = new Padding(2) };
            pnlTo.Controls.Add(new Label { Text = "تا تاریخ:", AutoSize = true, Location = new Point(120, 8) });
            pnlTo.Controls.Add(txtPersianTo);
            pnlTo.Controls.Add(datePickerTo);
            flowLayout.Controls.Add(pnlTo);

            AddFilterControl("کاربر:", cmbUser);
            AddFilterControl("گروه:", cmbGroup);
            AddFilterControl("حداقل آپلود (MB):", numMinUpload);
            AddFilterControl("حداکثر آپلود (MB):", numMaxUpload);
            AddFilterControl("حداقل دانلود (MB):", numMinDownload);
            AddFilterControl("حداکثر دانلود (MB):", numMaxDownload);

            FlowLayoutPanel btnPanel = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(10, 5, 10, 0) };
            btnPanel.Controls.Add(btnSearch);
            btnPanel.Controls.Add(btnReset);
            flowLayout.Controls.Add(btnPanel);

            grpFilters.Controls.Add(flowLayout);
            grpFilters.Dock = DockStyle.Fill;
            grpFilters.Text = "فیلترهای گزارش";

            pnlFilters.Controls.Add(grpFilters);
            pnlFilters.Dock = DockStyle.Top;
            pnlFilters.Height = 165;

            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1100, 680);
            Controls.Add(mainTabControl);
            Controls.Add(pnlFilters);
            Controls.Add(statusStrip);
            Font = new Font("Tahoma", 9F, FontStyle.Regular, GraphicsUnit.Point);
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "گزارش‌گیری جامع TekRADIUS LT";
            Load += MainForm_Load;

            ResumeLayout(false);
            PerformLayout();
        }

        private DataGridView CreateStandardGrid()
        {
            return new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoGenerateColumns = false,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
        }

        private void AddFilterControl(string labelText, Control control)
        {
            Panel p = new Panel { Size = new Size(210, 35), Margin = new Padding(2) };
            Label lbl = new Label { Text = labelText, AutoSize = true, Location = new Point(115, 8) };
            control.Location = new Point(5, 5);
            p.Controls.Add(lbl);
            p.Controls.Add(control);
            flowLayout.Controls.Add(p);
        }

        private void ToggleDateControls()
        {
            bool isEnabled = chkEnableDate.Checked;
            bool isPersian = cmbCalendarType.SelectedIndex == 0;

            txtPersianFrom.Enabled = isEnabled && isPersian;
            txtPersianTo.Enabled = isEnabled && isPersian;
            datePickerFrom.Enabled = isEnabled && !isPersian;
            datePickerTo.Enabled = isEnabled && !isPersian;
        }

        private void ToggleCalendarType()
        {
            bool isPersian = cmbCalendarType.SelectedIndex == 0;

            txtPersianFrom.Visible = isPersian;
            txtPersianTo.Visible = isPersian;
            datePickerFrom.Visible = !isPersian;
            datePickerTo.Visible = !isPersian;

            ToggleDateControls();
        }

        private async void MainForm_Load(object? sender, EventArgs? e)
        {
            try
            {
                _dbService.ValidateDatabase();
                lblStatus.Text = "ارتباط با دیتابیس برقرار است.";

                await LoadDropdownsAsync();
                ResetFilters();

                btnSearch_Click(null, null);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "خطا در اتصال به دیتابیس.";
                MessageBox.Show(ex.Message, "خطا", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task LoadDropdownsAsync()
        {
            try
            {
                var users = await _dbService.GetUsersAsync();
                cmbUser.Items.Clear();
                cmbUser.Items.Add("همه کاربران");
                foreach (var user in users) cmbUser.Items.Add(user);
                cmbUser.SelectedIndex = 0;

                var groups = await _dbService.GetGroupsAsync();
                cmbGroup.Items.Clear();
                cmbGroup.Items.Add("همه گروه‌ها");
                foreach (var group in groups) cmbGroup.Items.Add(group);
                cmbGroup.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"خطا در بارگذاری لیست کاربران و گروه‌ها: {ex.Message}", "خطا", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async void btnSearch_Click(object? sender, EventArgs? e)
        {
            btnSearch.Enabled = false;
            lblStatus.Text = "در حال دریافت اطلاعات و محاسبه گزارش‌ها...";

            try
            {
                bool enableDate = chkEnableDate.Checked;
                bool isPersian = cmbCalendarType.SelectedIndex == 0;

                DateTime fromDate = DateTime.Today;
                DateTime toDate = DateTime.Today;

                if (enableDate)
                {
                    if (isPersian)
                    {
                        var convertedFrom = DateConverter.PersianToGregorian(txtPersianFrom.Text.Trim());
                        var convertedTo = DateConverter.PersianToGregorian(txtPersianTo.Text.Trim());

                        if (!convertedFrom.HasValue || !convertedTo.HasValue)
                        {
                            MessageBox.Show("فرمت تاریخ شمسی نامعتبر است. نمونه معتبر: 1405/06/24", "خطای تاریخ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        fromDate = convertedFrom.Value;
                        toDate = convertedTo.Value;
                    }
                    else
                    {
                        fromDate = datePickerFrom.Value.Date;
                        toDate = datePickerTo.Value.Date;
                    }
                }

                string? selectedUser = cmbUser.SelectedIndex > 0 ? cmbUser.SelectedItem?.ToString() : null;
                string? selectedGroup = cmbGroup.SelectedIndex > 0 ? cmbGroup.SelectedItem?.ToString() : null;

                long? minUpload = numMinUpload.Value > 0 ? (long)numMinUpload.Value : null;
                long? maxUpload = numMaxUpload.Value > 0 ? (long)numMaxUpload.Value : null;
                long? minDownload = numMinDownload.Value > 0 ? (long)numMinDownload.Value : null;
                long? maxDownload = numMaxDownload.Value > 0 ? (long)numMaxDownload.Value : null;

                // 1. دریافت ریز رکوردهای تب اول
                var detailResult = await _dbService.GetDetailReportAsync(
                    enableDate, fromDate, toDate, isPersian, selectedUser, selectedGroup,
                    minUpload, maxUpload, minDownload, maxDownload
                );
                dgvDetails.DataSource = detailResult.Records;

                // 2. دریافت مجموع کارکرد کاربران تب دوم
                var summaryResult = await _dbService.GetUserSummaryReportAsync(
                    enableDate, fromDate, toDate, selectedUser, selectedGroup
                );
                dgvSummary.DataSource = summaryResult.Records;

                lblRecordCount.Text = $"تعداد نشست‌ها: {detailResult.Records.Count:N0} | تعداد کاربران: {summaryResult.Records.Count:N0}";
                lblTotalUsage.Text = $"کل آپلود: {FormatHelper.FormatBytes(detailResult.TotalUpload)} | کل دانلود: {FormatHelper.FormatBytes(detailResult.TotalDownload)}";
                lblStatus.Text = "گزارش‌گیری با موفقیت انجام شد.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "خطا در اجرای جستجو.";
                MessageBox.Show($"خطا هنگام گزارش‌گیری: {ex.Message}", "خطا", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnSearch.Enabled = true;
            }
        }

        private void btnReset_Click(object? sender, EventArgs? e)
        {
            ResetFilters();
        }

        private void ResetFilters()
        {
            chkEnableDate.Checked = false;
            cmbCalendarType.SelectedIndex = 0;

            txtPersianFrom.Text = DateConverter.ToPersianDate(DateTime.Today.AddDays(-30));
            txtPersianTo.Text = DateConverter.ToPersianDate(DateTime.Today);

            datePickerFrom.Value = DateTime.Today.AddDays(-30);
            datePickerTo.Value = DateTime.Today;

            if (cmbUser.Items.Count > 0) cmbUser.SelectedIndex = 0;
            if (cmbGroup.Items.Count > 0) cmbGroup.SelectedIndex = 0;

            numMinUpload.Value = 0;
            numMaxUpload.Value = 0;
            numMinDownload.Value = 0;
            numMaxDownload.Value = 0;
        }

        private void ExportGridToCsv(DataGridView grid, string filePrefix)
        {
            if (grid.Rows.Count == 0)
            {
                MessageBox.Show("اطلاعاتی برای خروجی وجود ندارد.", "هشدار", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "فایل CSV (*.csv)|*.csv",
                FileName = $"{filePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    StringBuilder sb = new StringBuilder();

                    // Header
                    List<string> headers = new List<string>();
                    foreach (DataGridViewColumn col in grid.Columns)
                    {
                        headers.Add($"\"{col.HeaderText}\"");
                    }
                    sb.AppendLine(string.Join(",", headers));

                    // Rows
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        List<string> cells = new List<string>();
                        foreach (DataGridViewCell cell in row.Cells)
                        {
                            cells.Add($"\"{cell.Value}\"");
                        }
                        sb.AppendLine(string.Join(",", cells));
                    }

                    File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                    MessageBox.Show("خروجی با موفقیت ذخیره شد.", "اطلاع", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"خطا در ذخیره خروجی: {ex.Message}", "خطا", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    // ==========================================
    // 5. Program Entry Point
    // ==========================================
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }
}