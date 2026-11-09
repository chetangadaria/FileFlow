using System.ComponentModel;
using System.Diagnostics;
using FileFlow.Core;

namespace FileFlow.Manager;

public sealed class MainForm : Form
{
    private readonly ConfigurationStore _store = new();
    private readonly HealthStore _healthStore = new();
    private readonly SnmpV2Traps _snmp = new();
    private readonly BindingList<TransferRule> _rules = [];
    private readonly DataGridView _grid = new();
    private readonly CheckBox _snmpEnabled = new() { Text = "Enable SNMPv2c traps" };
    private readonly TextBox _snmpServer = new();
    private readonly TextBox _snmpCommunity = new();
    private readonly NumericUpDown _snmpPort = new() { Minimum = 1, Maximum = 65535, Value = 162 };
    private readonly NumericUpDown _snmpHeartbeat = new() { Minimum = 15, Maximum = 3600, Value = 60 };
    private readonly RichTextBox _dailyLogs = new();
    private readonly Label _serviceState = new();
    private readonly Label _lastScan = new();
    private readonly TabControl _tabs = new();
    private readonly NotifyIcon _notify;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private FileFlowConfig _currentConfig = new();
    private bool _pendingChanges;
    private bool _exiting;

    public MainForm(bool launchInTray)
    {
        Text = "FileFlow Manager";
        Size = new Size(1200, 730);
        MinimumSize = new Size(890, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var header = BuildHeader();
        _tabs.Dock = DockStyle.Fill;
        _tabs.Font = new Font("Segoe UI", 10f);
        _tabs.TabPages.Add(BuildTasksTab());
        _tabs.TabPages.Add(BuildMonitoringTab());
        _tabs.TabPages.Add(BuildLogsTab());
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == 2) RefreshDailyLog();
        };
        Controls.Add(_tabs);
        Controls.Add(header);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => RestoreWindow());
        menu.Items.Add("Open daily log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray application (service keeps running)", null, (_, _) => ExitManager());
        _notify = new NotifyIcon
        {
            Icon = Icon,
            Text = "FileFlow - transfers run in Windows Service",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notify.DoubleClick += (_, _) => RestoreWindow();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) HideToTray();
        };
        FormClosing += OnClosing;
        _timer.Tick += (_, _) => RefreshServiceHealth();

        LoadFromDisk();
        RefreshServiceHealth();
        _timer.Start();
        if (launchInTray) Shown += (_, _) => BeginInvoke(new Action(HideToTray));
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Height = 104,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(22, 43, 74)
        };
        header.Controls.Add(new Label
        {
            Text = "FileFlow  |  Automated File Transfer",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(22, 13),
            AutoSize = true
        });
        header.Controls.Add(new Label
        {
            Text = "Local and UNC network folders  -  5-second scan  -  independent Windows Service",
            ForeColor = Color.FromArgb(190, 213, 238),
            Location = new Point(24, 56),
            AutoSize = true
        });
        _serviceState.AutoSize = true;
        _serviceState.ForeColor = Color.White;
        _serviceState.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        _serviceState.Text = "Service: checking...";
        _serviceState.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _serviceState.Location = new Point(785, 23);
        _lastScan.AutoSize = true;
        _lastScan.ForeColor = Color.FromArgb(190, 213, 238);
        _lastScan.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _lastScan.Location = new Point(785, 58);
        header.Resize += (_, _) =>
        {
            _serviceState.Left = Math.Max(640, header.ClientSize.Width - 400);
            _lastScan.Left = _serviceState.Left;
        };
        header.Controls.Add(_serviceState);
        header.Controls.Add(_lastScan);
        return header;
    }

    private TabPage BuildTasksTab()
    {
        var page = new TabPage("Transfer tasks") { BackColor = Color.White, Padding = new Padding(12) };
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 53,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(3, 5, 3, 3)
        };
        AddButton(bar, "Add task", (_, _) => AddTask(), primary: true);
        AddButton(bar, "Edit selected", (_, _) => EditTask());
        AddButton(bar, "Delete selected", (_, _) => DeleteTask());
        AddButton(bar, "Save all", (_, _) => SaveAll(), primary: true);
        AddButton(bar, "Reload", (_, _) => LoadFromDisk());
        AddButton(bar, "Open logs", (_, _) => OpenLogFolder());

        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.RowTemplate.Height = 31;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 238, 246);
        _grid.ColumnHeadersHeight = 37;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        AddColumn("Name", nameof(TransferRule.Name), 150);
        AddColumn("Enabled", nameof(TransferRule.Enabled), 66);
        AddColumn("Action", nameof(TransferRule.Action), 74);
        AddColumn("Source folder", nameof(TransferRule.SourceDirectory), 218);
        AddColumn("Destination folder", nameof(TransferRule.DestinationDirectory), 218);
        AddColumn("Repeat", nameof(TransferRule.Repeat), 66);
        AddColumn("Subfolders", nameof(TransferRule.IncludeSubdirectories), 82);
        AddColumn("Conflict", nameof(TransferRule.OnConflict), 85);
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Service status",
            Name = "Health",
            Width = 155,
            ReadOnly = true
        });
        _grid.DataSource = _rules;
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditTask(); };

        var tip = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 37,
            Text = "Repeat off = one pass only (completion saved across service restarts). "
                + "Editing and saving a task re-arms a one-time pass. "
                + "The service continues when this window closes.",
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        page.Controls.Add(_grid);
        page.Controls.Add(tip);
        page.Controls.Add(bar);
        return page;
    }

    private void AddColumn(string header, string field, int width)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            DataPropertyName = field,
            Width = width,
            ReadOnly = true
        });
    }

    private TabPage BuildMonitoringTab()
    {
        var page = new TabPage("SNMP and monitoring") { BackColor = Color.White, Padding = new Padding(23, 20, 23, 20) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = "SNMPv2c outgoing traps",
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            AutoSize = true
        }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        layout.Controls.Add(_snmpEnabled, 1, 1);
        AddField(layout, 2, "SNMP monitoring server", _snmpServer);
        AddField(layout, 3, "Destination UDP port", _snmpPort);
        AddField(layout, 4, "Community", _snmpCommunity);
        AddField(layout, 5, "Heartbeat (seconds)", _snmpHeartbeat);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        AddButton(buttons, "Save configuration", (_, _) => SaveAll(), primary: true);
        AddButton(buttons, "Send test trap", async (_, _) => await SendTestTrapAsync());
        layout.Controls.Add(buttons, 1, 6);
        var message = new Label
        {
            Dock = DockStyle.Fill,
            Text = "The service sends STARTED, TASK_FAILED, RECOVERED, HEARTBEAT and STOPPING traps. "
                + "Monitor the last heartbeat on your SNMP server; a crashed/offline service cannot send "
                + "a final failure trap. This app does not expose an SNMP GET agent. SNMPv2c is not encrypted. "
                + "The example OIDs use the experimental 1.3.6.1.3.9999 tree; use your registered enterprise OID in production.",
            ForeColor = Color.DimGray
        };
        layout.Controls.Add(message, 0, 8);
        layout.SetColumnSpan(message, 2);
        page.Controls.Add(layout);
        return page;
    }

    private static void AddField(TableLayoutPanel table, int row, string caption, Control value)
    {
        table.Controls.Add(new Label
        {
            Text = caption,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        }, 0, row);
        value.Width = 360;
        value.Anchor = AnchorStyles.Left;
        table.Controls.Add(value, 1, row);
    }

    private TabPage BuildLogsTab()
    {
        var page = new TabPage("Daily transfer logs") { BackColor = Color.White, Padding = new Padding(12) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(3, 5, 3, 3) };
        AddButton(bar, "Refresh today's log", (_, _) => RefreshDailyLog(), primary: true);
        AddButton(bar, "Open logs folder", (_, _) => OpenLogFolder());
        _dailyLogs.Dock = DockStyle.Fill;
        _dailyLogs.ReadOnly = true;
        _dailyLogs.WordWrap = false;
        _dailyLogs.Font = new Font("Consolas", 9.5f);
        _dailyLogs.BackColor = Color.FromArgb(249, 250, 252);
        _dailyLogs.BorderStyle = BorderStyle.FixedSingle;
        page.Controls.Add(_dailyLogs);
        page.Controls.Add(bar);
        return page;
    }

    private static void AddButton(FlowLayoutPanel panel, string caption, EventHandler click, bool primary = false)
    {
        var button = new Button
        {
            Text = caption,
            AutoSize = true,
            MinimumSize = new Size(104, 32),
            Margin = new Padding(3, 2, 5, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(34, 93, 177) : Color.White,
            ForeColor = primary ? Color.White : Color.Black
        };
        button.Click += click;
        panel.Controls.Add(button);
    }

    private TransferRule? SelectedTask()
        => _grid.CurrentRow?.DataBoundItem as TransferRule;

    private void AddTask()
    {
        using var dialog = new TaskEditor();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _rules.Add(dialog.Rule);
        MarkChanged();
    }

    private void EditTask()
    {
        var existing = SelectedTask();
        if (existing is null) return;
        using var dialog = new TaskEditor(existing);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var index = _rules.IndexOf(existing);
        if (index >= 0) _rules[index] = dialog.Rule;
        MarkChanged();
    }

    private void DeleteTask()
    {
        var selected = SelectedTask();
        if (selected is null) return;
        if (MessageBox.Show(this, $"Delete task '{selected.Name}'?", "Delete transfer task",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _rules.Remove(selected);
        MarkChanged();
    }

    private void MarkChanged()
    {
        _pendingChanges = true;
        Text = "FileFlow Manager * - Unsaved changes";
    }

    private void LoadFromDisk()
    {
        if (_pendingChanges && MessageBox.Show(this, "Discard unsaved task changes?", "Reload",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            _currentConfig = _store.Load();
            _rules.Clear();
            foreach (var rule in _currentConfig.Tasks) _rules.Add(rule);
            _snmpEnabled.Checked = _currentConfig.Snmp.Enabled;
            _snmpServer.Text = _currentConfig.Snmp.Server;
            _snmpCommunity.Text = _currentConfig.Snmp.Community;
            _snmpPort.Value = Math.Clamp(_currentConfig.Snmp.Port, 1, 65535);
            _snmpHeartbeat.Value = Math.Clamp(_currentConfig.Snmp.HeartbeatSeconds, 15, 3600);
            _pendingChanges = false;
            Text = "FileFlow Manager";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot read configuration: " + ex.Message,
                "FileFlow configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private SnmpSettings ReadSnmpControls() => new()
    {
        Enabled = _snmpEnabled.Checked,
        Server = _snmpServer.Text.Trim(),
        Port = (int)_snmpPort.Value,
        Community = _snmpCommunity.Text.Trim(),
        HeartbeatSeconds = (int)_snmpHeartbeat.Value
    };

    private void SaveAll()
    {
        try
        {
            var next = new FileFlowConfig
            {
                ScanIntervalSeconds = 5,
                Tasks = _rules.ToList(),
                Snmp = ReadSnmpControls()
            };
            _store.Save(next);
            _currentConfig = next;
            _pendingChanges = false;
            Text = "FileFlow Manager";
            MessageBox.Show(this, "Configuration saved. The Windows Service will read it on the next scan.",
                "FileFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Configuration is protected for security. Exit the tray application, "
                + "then launch FileFlow.Manager.exe using 'Run as administrator' to edit and save tasks.",
                "Administrator permission needed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SendTestTrapAsync()
    {
        var settings = ReadSnmpControls();
        settings.Enabled = true; // Explicit test action sends even before saving Enable.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _snmp.SendAsync(settings, TrapEvent.Test, "Manager",
                "User-initiated test trap from FileFlow Manager.", timeout.Token);
            MessageBox.Show(this, "SNMPv2c UDP test trap sent. Confirm receipt in your monitoring tool; "
                + "UDP does not acknowledge delivery.", "SNMP test",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "SNMP test failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshServiceHealth()
    {
        var health = _healthStore.TryLoad();
        var fresh = health is not null && DateTimeOffset.UtcNow - health.LastHeartbeatUtc < TimeSpan.FromSeconds(20);
        var state = health is null ? "No heartbeat yet"
            : !fresh ? "OFFLINE or unavailable (heartbeat stale)"
            : health.State;
        _serviceState.Text = "Service: " + state;
        _serviceState.ForeColor = fresh && health?.State == "Running"
            ? Color.FromArgb(145, 232, 168)
            : Color.FromArgb(255, 191, 128);
        _lastScan.Text = health is null ? "Waiting for status.json"
            : $"Last scan: {health.LastScanUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

        Dictionary<Guid, TaskHealth> statuses = health is null
            ? new() : health.Tasks.ToDictionary(t => t.TaskId);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is not TransferRule rule) continue;
            var stateCell = row.Cells["Health"];
            stateCell.Value = !fresh ? "No service heartbeat"
                : statuses.TryGetValue(rule.Id, out var taskStatus) ? taskStatus.State : "Pending";
            stateCell.Style.ForeColor = stateCell.Value?.ToString() == "Error"
                ? Color.DarkRed : Color.Black;
        }
    }

    private void RefreshDailyLog()
    {
        try
        {
            var filename = Path.Combine(FileFlowPaths.Logs, DateTime.Now.ToString("yyyy-MM-dd") + ".csv");
            _dailyLogs.Text = File.Exists(filename)
                ? string.Join(Environment.NewLine, File.ReadLines(filename).TakeLast(300))
                : "No log file has been created for today.\n\n" + filename;
            _dailyLogs.SelectionStart = _dailyLogs.Text.Length;
            _dailyLogs.ScrollToCaret();
        }
        catch (Exception ex)
        {
            _dailyLogs.Text = "Cannot read daily log: " + ex.Message;
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(FileFlowPaths.Logs);
            Process.Start(new ProcessStartInfo(FileFlowPaths.Logs) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open logs", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        RefreshServiceHealth();
    }

    private void ExitManager()
    {
        _exiting = true;
        Close();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _timer.Stop();
        _timer.Dispose();
        _notify.Visible = false;
        _notify.Dispose();
    }
}
