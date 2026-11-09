namespace FileFlow.Core;

public enum TransferAction
{
    Copy,
    Move
}

public enum ConflictPolicy
{
    Skip,
    Overwrite
}

public sealed class TransferRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunVersion { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New task";
    public string SourceDirectory { get; set; } = "";
    public string DestinationDirectory { get; set; } = "";
    public TransferAction Action { get; set; } = TransferAction.Copy;
    public ConflictPolicy OnConflict { get; set; } = ConflictPolicy.Skip;
    public bool Enabled { get; set; } = true;
    public bool Repeat { get; set; } = true;
    public bool IncludeSubdirectories { get; set; } = false;
}

public sealed class SnmpSettings
{
    public bool Enabled { get; set; } = false;
    public string Server { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 162;
    public string Community { get; set; } = "public";
    public int HeartbeatSeconds { get; set; } = 60;
}

public sealed class FileFlowConfig
{
    public int ScanIntervalSeconds { get; set; } = 5;
    public List<TransferRule> Tasks { get; set; } = [];
    public SnmpSettings Snmp { get; set; } = new();
}

public sealed class TransferSummary
{
    public int Copied { get; set; }
    public int Moved { get; set; }
    public int Skipped { get; set; }
    public int Deferred { get; set; }
    public int Errors { get; set; }
    public long TransferredBytes { get; set; }
    public List<string> ErrorMessages { get; set; } = [];
}

public sealed class TaskHealth
{
    public Guid TaskId { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "Waiting";
    public DateTimeOffset? LastRunUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string LastError { get; set; } = "";
    public long TotalCopied { get; set; }
    public long TotalMoved { get; set; }
    public long TotalErrors { get; set; }
    public long TotalBytes { get; set; }
    public int LastDeferred { get; set; }
    public int LastSkipped { get; set; }
}

public sealed class ServiceHealth
{
    public string ServiceName { get; set; } = "FileFlowService";
    public string State { get; set; } = "Starting";
    public string Machine { get; set; } = Environment.MachineName;
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastScanUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastHeartbeatUtc { get; set; } = DateTimeOffset.MinValue;
    public string LastGlobalError { get; set; } = "";
    public List<TaskHealth> Tasks { get; set; } = [];
}
