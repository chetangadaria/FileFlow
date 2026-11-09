using FileFlow.Core;

var root = Path.Combine(Path.GetTempPath(), "FileFlowSmokeTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var journal = new DailyJournal(Path.Combine(root, "Logs"));
var engine = new TransferEngine(journal);
try
{
    var from = Path.Combine(root, "CopySource");
    var to = Path.Combine(root, "CopyTarget");
    Directory.CreateDirectory(from);
    Directory.CreateDirectory(to);
    var first = Path.Combine(from, "example.txt");
    File.WriteAllText(first, "Hello FileFlow");
    File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(-20));
    var rule = new TransferRule
    {
        Name = "Copy test",
        SourceDirectory = from,
        DestinationDirectory = to,
        Action = TransferAction.Copy,
        OnConflict = ConflictPolicy.Overwrite
    };
    var copy = await engine.ExecuteAsync(rule, CancellationToken.None);
    Check(copy.Copied == 1, "Initial copy");
    Check(File.Exists(first), "Copy retains its source");
    Check(File.ReadAllText(Path.Combine(to, "example.txt")) == "Hello FileFlow", "Copied contents");

    var repeat = await engine.ExecuteAsync(rule, CancellationToken.None);
    Check(repeat.Copied == 0 && repeat.Skipped == 1, "Unchanged copy not repeated");

    File.WriteAllText(first, "Updated version");
    File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(-10));
    var overwrite = await engine.ExecuteAsync(rule, CancellationToken.None);
    Check(overwrite.Copied == 1, "Overwrite changed file");
    Check(File.ReadAllText(Path.Combine(to, "example.txt")) == "Updated version", "Changed destination");

    var moveFrom = Path.Combine(root, "MoveSource");
    var moveTo = Path.Combine(root, "MoveTarget");
    Directory.CreateDirectory(moveFrom);
    var second = Path.Combine(moveFrom, "move.txt");
    File.WriteAllText(second, "Keep my data safe");
    File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddSeconds(-20));
    var moveRule = new TransferRule
    {
        Name = "Move test", SourceDirectory = moveFrom,
        DestinationDirectory = moveTo, Action = TransferAction.Move
    };
    var move = await engine.ExecuteAsync(moveRule, CancellationToken.None);
    Check(move.Moved == 1, "Move transfer completed");
    Check(!File.Exists(second) && File.ReadAllText(Path.Combine(moveTo, "move.txt")) == "Keep my data safe",
        "Move verified and source removed");

    rule.DestinationDirectory = Path.Combine(from, "Nested");
    Check(RuleValidation.Check(rule) is not null, "Disallow destination below source");

    var shotsFile = Path.Combine(root, "oneshot.json");
    var once = new OneShotStore(shotsFile);
    Check(!once.IsComplete(moveRule), "One-shot initially pending");
    once.MarkComplete(moveRule);
    Check(new OneShotStore(shotsFile).IsComplete(moveRule), "One-shot completion persisted");

    var packet = SnmpV2Traps.BuildPacket("public", TrapEvent.TaskFailed, "Task A", "Oops");
    Check(packet[0] == 0x30 && packet.Contains((byte)0xA7), "SNMPv2 trap BER generated");
    Check(System.Text.Encoding.ASCII.GetString(packet).Contains("public"), "SNMPv2 community included");
    Check(Directory.GetFiles(Path.Combine(root, "Logs"), "*.csv").Length == 1, "Daily CSV created");

    Console.WriteLine("PASS: 14 FileFlow core smoke-test assertions");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}

static void Check(bool condition, string title)
{
    if (!condition) throw new Exception("FAILED: " + title);
    Console.WriteLine("PASS: " + title);
}
