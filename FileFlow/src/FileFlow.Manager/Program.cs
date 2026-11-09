using FileFlow.Manager;

using var mutex = new Mutex(true, @"Local\FileFlow.Manager", out var isFirstInstance);
if (!isFirstInstance)
{
    if (!args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        MessageBox.Show("FileFlow Manager is already running. Open it from the notification area, "
            + "or exit it there before opening an administrator instance.",
            "FileFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
    return;
}

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(args.Contains("--tray", StringComparer.OrdinalIgnoreCase)));
