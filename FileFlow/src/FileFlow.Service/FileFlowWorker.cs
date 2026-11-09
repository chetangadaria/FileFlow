using FileFlow.Core;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileFlow.Service;

public sealed class FileFlowWorker : BackgroundService
{
    private readonly ConfigurationStore _configuration;
    private readonly HealthStore _healthStore;
    private readonly OneShotStore _oneShot;
    private readonly DailyJournal _journal;
    private readonly TransferEngine _engine;
    private readonly SnmpV2Traps _traps;
    private readonly ILogger<FileFlowWorker> _logger;
    private readonly ServiceHealth _health = new();
    private readonly Dictionary<Guid, TaskHealth> _taskHealth = [];
    private readonly HashSet<Guid> _failedTasks = [];
    private bool _configurationFailed;
    private SnmpSettings _lastSnmpSettings = new();
    private DateTimeOffset _lastHeartbeatTrap = DateTimeOffset.MinValue;

    public FileFlowWorker(ConfigurationStore configuration, HealthStore healthStore,
        OneShotStore oneShot, DailyJournal journal, TransferEngine engine,
        SnmpV2Traps traps, ILogger<FileFlowWorker> logger)
    {
        _configuration = configuration;
        _healthStore = healthStore;
        _oneShot = oneShot;
        _journal = journal;
        _engine = engine;
        _traps = traps;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(FileFlowPaths.Root);
        _health.StartedUtc = DateTimeOffset.UtcNow;
        _health.State = "Starting";
        _journal.Write("SERVICE_STARTED", "Service", "", "", "", "Windows Service started.");

        Task? heartbeatLoop = null;
        try
        {
            heartbeatLoop = RunHeartbeatLoopAsync(stoppingToken);
            try
            {
                _lastSnmpSettings = _configuration.Load().Snmp;
                await TrySendTrapAsync(_lastSnmpSettings, TrapEvent.Started,
                    "Service", "FileFlowService has started.", stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Initial configuration was unavailable.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var settings = _configuration.Load();
                    _lastSnmpSettings = settings.Snmp;
                    await RunTasksAsync(settings, stoppingToken);
                    _health.LastGlobalError = "";

                    if (_configurationFailed)
                    {
                        await TrySendTrapAsync(settings.Snmp, TrapEvent.Recovered,
                            "Configuration", "Configuration was loaded successfully.", stoppingToken);
                        _configurationFailed = false;
                        _journal.Write("RECOVERED", "Configuration", "", "", "", "Configuration recovered.");
                    }

                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected scan/configuration failure.");
                    _health.State = "Error";
                    _health.LastGlobalError = ex.Message;
                    if (!_configurationFailed)
                    {
                        _configurationFailed = true;
                        SafeWrite("ERROR", "Service", "", "", "", ex.ToString());
                        await TrySendTrapAsync(_lastSnmpSettings, TrapEvent.TaskFailed,
                            "Service", "Configuration or service cycle failed: " + ex.Message, stoppingToken);
                    }
                }
                finally
                {
                    _health.LastScanUtc = DateTimeOffset.UtcNow;
                }

                // One non-overlapping pass at a time. Runs approximately every 5 seconds when idle.
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            if (heartbeatLoop is not null)
            {
                try { await heartbeatLoop; }
                catch (OperationCanceledException) { }
            }
            _health.State = "Stopped";
            _health.LastScanUtc = DateTimeOffset.UtcNow;
            _health.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            try { _healthStore.Save(SnapshotHealth()); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not save stopped status."); }
            SafeWrite("SERVICE_STOPPED", "Service", "", "", "", "Service stopping normally.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await TrySendTrapAsync(_lastSnmpSettings, TrapEvent.Stopping,
                "Service", "FileFlowService is stopping normally.", timeout.Token);
        }
    }

    // Independent of transfer passes: long file copies must not suppress monitoring heartbeats.
    private async Task RunHeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            _health.LastHeartbeatUtc = DateTimeOffset.UtcNow;
            var snapshot = SnapshotHealth();
            try { _healthStore.Save(snapshot); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not write heartbeat status."); }

            var snmp = _lastSnmpSettings;
            if (snmp.Enabled && DateTimeOffset.UtcNow - _lastHeartbeatTrap
                >= TimeSpan.FromSeconds(Math.Max(15, snmp.HeartbeatSeconds)))
            {
                _lastHeartbeatTrap = DateTimeOffset.UtcNow;
                await TrySendTrapAsync(snmp, TrapEvent.Heartbeat, "Service",
                    $"State={snapshot.State}; last completed scan={snapshot.LastScanUtc:O}", token);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private ServiceHealth SnapshotHealth() => new()
    {
        ServiceName = _health.ServiceName,
        State = _health.State,
        Machine = _health.Machine,
        StartedUtc = _health.StartedUtc,
        LastScanUtc = _health.LastScanUtc,
        LastHeartbeatUtc = _health.LastHeartbeatUtc,
        LastGlobalError = _health.LastGlobalError,
        Tasks = _health.Tasks.Select(s => new TaskHealth
        {
            TaskId = s.TaskId,
            Name = s.Name,
            State = s.State,
            LastRunUtc = s.LastRunUtc,
            LastSuccessUtc = s.LastSuccessUtc,
            LastError = s.LastError,
            TotalCopied = s.TotalCopied,
            TotalMoved = s.TotalMoved,
            TotalErrors = s.TotalErrors,
            TotalBytes = s.TotalBytes,
            LastDeferred = s.LastDeferred,
            LastSkipped = s.LastSkipped
        }).ToList()
    };

    private async Task RunTasksAsync(FileFlowConfig settings, CancellationToken token)
    {
        var visible = new List<TaskHealth>();
        foreach (var task in settings.Tasks)
        {
            token.ThrowIfCancellationRequested();
            if (!_taskHealth.TryGetValue(task.Id, out var status))
            {
                status = new TaskHealth { TaskId = task.Id };
                _taskHealth[task.Id] = status;
            }
            status.Name = task.Name;
            visible.Add(status);

            if (!task.Enabled)
            {
                status.State = "Disabled";
                _failedTasks.Remove(task.Id);
                continue;
            }
            if (!task.Repeat && _oneShot.IsComplete(task))
            {
                status.State = "Completed (one-time)";
                _failedTasks.Remove(task.Id);
                continue;
            }

            status.LastRunUtc = DateTimeOffset.UtcNow;
            try
            {
                var summary = await _engine.ExecuteAsync(task, token);
                status.TotalCopied += summary.Copied;
                status.TotalMoved += summary.Moved;
                status.TotalErrors += summary.Errors;
                status.TotalBytes += summary.TransferredBytes;
                status.LastDeferred = summary.Deferred;
                status.LastSkipped = summary.Skipped;
                if (summary.Errors > 0)
                {
                    var error = string.Join(" | ", summary.ErrorMessages.Take(2));
                    status.State = "Error";
                    status.LastError = $"{summary.Errors} file(s): {error}";
                    await NotifyTaskFailureAsync(task, status.LastError, settings.Snmp, token);
                }
                else
                {
                    status.State = summary.Deferred > 0 ? "Waiting for file(s)" : "Healthy";
                    status.LastError = "";
                    status.LastSuccessUtc = DateTimeOffset.UtcNow;
                    if (!task.Repeat && summary.Deferred == 0)
                    {
                        _oneShot.MarkComplete(task);
                        status.State = "Completed (one-time)";
                    }
                    await NotifyRecoveryAsync(task, settings.Snmp, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                status.TotalErrors++;
                status.State = "Error";
                status.LastError = ex.Message;
                _logger.LogWarning(ex, "Task {TaskName} failed.", task.Name);
                SafeWrite("ERROR", task.Name, task.Action.ToString(),
                    task.SourceDirectory, task.DestinationDirectory, ex.ToString());
                await NotifyTaskFailureAsync(task, ex.Message, settings.Snmp, token);
            }
        }

        _health.Tasks = visible;
        _health.State = visible.Any(t => t.State == "Error") ? "Degraded" : "Running";
    }

    private async Task NotifyTaskFailureAsync(TransferRule task, string error,
        SnmpSettings settings, CancellationToken token)
    {
        if (_failedTasks.Add(task.Id))
        {
            _journal.Write("TASK_FAILED", task.Name, task.Action.ToString(),
                task.SourceDirectory, task.DestinationDirectory, error);
            await TrySendTrapAsync(settings, TrapEvent.TaskFailed, task.Name, error, token);
        }
    }

    private async Task NotifyRecoveryAsync(TransferRule task, SnmpSettings settings, CancellationToken token)
    {
        if (_failedTasks.Remove(task.Id))
        {
            _journal.Write("RECOVERED", task.Name, task.Action.ToString(),
                task.SourceDirectory, task.DestinationDirectory, "Task processing recovered.");
            await TrySendTrapAsync(settings, TrapEvent.Recovered,
                task.Name, "Task is healthy again.", token);
        }
    }

    private async Task TrySendTrapAsync(SnmpSettings settings, TrapEvent kind,
        string task, string message, CancellationToken token)
    {
        if (!settings.Enabled) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await _traps.SendAsync(settings, kind, task, message, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or
            OperationCanceledException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not send SNMPv2c {Event} trap.", kind);
            SafeWrite("SNMP_ERROR", task, kind.ToString(), "", settings.Server, ex.Message);
        }
    }

    private void SafeWrite(string eventType, string task, string action,
        string source, string destination, string message)
    {
        try { _journal.Write(eventType, task, action, source, destination, message); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not write daily journal."); }
    }
}
