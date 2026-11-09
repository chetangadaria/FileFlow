using FileFlow.Core;

namespace FileFlow.Manager;

public sealed class TaskEditor : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _source = new();
    private readonly TextBox _destination = new();
    private readonly ComboBox _action = new();
    private readonly ComboBox _conflict = new();
    private readonly CheckBox _enabled = new() { Text = "Enabled" };
    private readonly CheckBox _repeat = new() { Text = "Repeat every scan (5 seconds)" };
    private readonly CheckBox _recursive = new() { Text = "Include subfolders and preserve their structure" };
    public TransferRule Rule { get; }

    public TaskEditor(TransferRule? existing = null)
    {
        Rule = existing is null ? new TransferRule() : new TransferRule
        {
            Id = existing.Id,
            RunVersion = existing.RunVersion,
            Name = existing.Name,
            SourceDirectory = existing.SourceDirectory,
            DestinationDirectory = existing.DestinationDirectory,
            Action = existing.Action,
            OnConflict = existing.OnConflict,
            Enabled = existing.Enabled,
            Repeat = existing.Repeat,
            IncludeSubdirectories = existing.IncludeSubdirectories
        };

        Text = existing is null ? "Add transfer task" : "Edit transfer task";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(720, 500);
        Size = new Size(820, 520);
        MaximizeBox = false;
        MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.White;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 16, 20, 4),
            ColumnCount = 3,
            RowCount = 9
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        for (int i = 0; i < 8; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _name.Text = Rule.Name;
        _source.Text = Rule.SourceDirectory;
        _destination.Text = Rule.DestinationDirectory;
        _action.DropDownStyle = ComboBoxStyle.DropDownList;
        _action.DataSource = Enum.GetValues<TransferAction>();
        _action.SelectedItem = Rule.Action;
        _conflict.DropDownStyle = ComboBoxStyle.DropDownList;
        _conflict.DataSource = Enum.GetValues<ConflictPolicy>();
        _conflict.SelectedItem = Rule.OnConflict;
        _enabled.Checked = Rule.Enabled;
        _repeat.Checked = Rule.Repeat;
        _recursive.Checked = Rule.IncludeSubdirectories;

        AddTextRow(table, 0, "Task name", _name);
        AddFolderRow(table, 1, "Source folder", _source);
        AddFolderRow(table, 2, "Destination folder", _destination);
        AddTextRow(table, 3, "Action", _action);
        AddTextRow(table, 4, "If destination exists", _conflict);
        table.Controls.Add(_enabled, 1, 5);
        table.Controls.Add(_repeat, 1, 6);
        table.Controls.Add(_recursive, 1, 7);

        var hint = new Label
        {
            Text = "Network folders: type a UNC path such as \\\\SERVER\\Share\\Inbox. "
                + "Use a service account with permissions on both shares. "
                + "For Copy, existing files with matching size/time are skipped. "
                + "For Move, the source is deleted only after SHA-256 verification.",
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray
        };
        table.Controls.Add(hint, 0, 8);
        table.SetColumnSpan(hint, 3);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 8, 16, 5),
            BackColor = Color.FromArgb(246, 248, 250)
        };
        var save = Button("Save task", true);
        save.Click += (_, _) => SaveRule();
        var cancel = Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(table);
        Controls.Add(footer);
    }

    private static void AddTextRow(TableLayoutPanel panel, int row, string title, Control input)
    {
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(3, 6, 4, 7);
        panel.Controls.Add(input, 1, row);
    }

    private static void AddFolderRow(TableLayoutPanel panel, int row, string title, TextBox field)
    {
        AddTextRow(panel, row, title, field);
        var browse = Button("Browse...");
        browse.Margin = new Padding(2, 6, 0, 7);
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (Directory.Exists(field.Text)) picker.SelectedPath = field.Text;
            if (picker.ShowDialog(panel.FindForm()) == DialogResult.OK)
                field.Text = picker.SelectedPath;
        };
        panel.Controls.Add(browse, 2, row);
    }

    private static Button Button(string title, bool primary = false) => new()
    {
        Text = title,
        Width = primary ? 112 : 96,
        Height = 33,
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Color.FromArgb(34, 93, 177) : Color.White,
        ForeColor = primary ? Color.White : Color.Black
    };

    private void SaveRule()
    {
        Rule.Name = _name.Text.Trim();
        Rule.SourceDirectory = _source.Text.Trim();
        Rule.DestinationDirectory = _destination.Text.Trim();
        Rule.Action = (TransferAction)_action.SelectedItem!;
        Rule.OnConflict = (ConflictPolicy)_conflict.SelectedItem!;
        Rule.Enabled = _enabled.Checked;
        Rule.Repeat = _repeat.Checked;
        Rule.IncludeSubdirectories = _recursive.Checked;

        var error = RuleValidation.Check(Rule);
        if (error is not null)
        {
            MessageBox.Show(this, error, "Check task settings",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Editing a one-time task intentionally arms it for a new one-time pass.
        Rule.RunVersion = Guid.NewGuid();
        DialogResult = DialogResult.OK;
        Close();
    }
}
