using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileFlow.Core;

public static class FileFlowPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FileFlow");
    public static string Configuration => Path.Combine(Root, "config.json");
    public static string Health => Path.Combine(Root, "status.json");
    public static string OneShot => Path.Combine(Root, "oneshot.json");
    public static string Logs => Path.Combine(Root, "Logs");
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static void WriteAtomically<T>(string path, T data)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("A full file path is required.", nameof(path));
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Options), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

public sealed class ConfigurationStore
{
    private readonly string _filename;

    public ConfigurationStore(string? filename = null)
    {
        _filename = filename ?? FileFlowPaths.Configuration;
    }

    public FileFlowConfig Load()
    {
        if (!File.Exists(_filename)) return new FileFlowConfig();
        var settings = JsonSerializer.Deserialize<FileFlowConfig>(
            File.ReadAllText(_filename), JsonFiles.Options)
            ?? throw new InvalidDataException("Configuration file was empty.");
        settings.Tasks ??= [];
        settings.Snmp ??= new SnmpSettings();
        return settings;
    }

    public void Save(FileFlowConfig settings)
    {
        if (settings.ScanIntervalSeconds != 5)
            throw new ArgumentException("FileFlow's scan interval must be 5 seconds.");

        var ids = new HashSet<Guid>();
        foreach (var rule in settings.Tasks)
        {
            var error = RuleValidation.Check(rule);
            if (error is not null) throw new ArgumentException($"{rule.Name}: {error}");
            if (!ids.Add(rule.Id)) throw new ArgumentException("Duplicate task IDs are not allowed.");
        }

        if (settings.Snmp.Enabled)
        {
            if (string.IsNullOrWhiteSpace(settings.Snmp.Server))
                throw new ArgumentException("SNMP server is required.");
            if (settings.Snmp.Port is < 1 or > 65535)
                throw new ArgumentException("SNMP port must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(settings.Snmp.Community))
                throw new ArgumentException("SNMP community must not be empty.");
            if (settings.Snmp.HeartbeatSeconds < 15)
                throw new ArgumentException("SNMP heartbeat must be at least 15 seconds.");
        }
        JsonFiles.WriteAtomically(_filename, settings);
    }
}

public static class RuleValidation
{
    public static string? Check(TransferRule rule)
    {
        if (rule.Id == Guid.Empty || rule.RunVersion == Guid.Empty)
            return "Task identity is invalid.";
        if (string.IsNullOrWhiteSpace(rule.Name))
            return "Enter a task name.";
        if (string.IsNullOrWhiteSpace(rule.SourceDirectory)
            || string.IsNullOrWhiteSpace(rule.DestinationDirectory))
            return "Both source and destination folders are required.";

        try
        {
            var source = Path.GetFullPath(rule.SourceDirectory);
            var destination = Path.GetFullPath(rule.DestinationDirectory);
            if (!Path.IsPathFullyQualified(rule.SourceDirectory)
                || !Path.IsPathFullyQualified(rule.DestinationDirectory))
                return "Use full local paths or UNC network paths, not relative paths.";

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(source, destination, comparison))
                return "Source and destination cannot be the same folder.";

            var sourceRoot = Path.TrimEndingDirectorySeparator(source);
            var sourcePrefix = Path.EndsInDirectorySeparator(sourceRoot)
                ? sourceRoot : sourceRoot + Path.DirectorySeparatorChar;
            if (destination.StartsWith(sourcePrefix, comparison))
                return "Destination cannot be inside the source folder.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Invalid folder path: " + ex.Message;
        }
        return null;
    }
}

public sealed class HealthStore
{
    private readonly string _filename;

    public HealthStore(string? filename = null) => _filename = filename ?? FileFlowPaths.Health;

    public ServiceHealth? TryLoad()
    {
        try
        {
            if (!File.Exists(_filename)) return null;
            return JsonSerializer.Deserialize<ServiceHealth>(File.ReadAllText(_filename), JsonFiles.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(ServiceHealth status) => JsonFiles.WriteAtomically(_filename, status);
}

public sealed class OneShotStore
{
    private readonly string _filename;
    private readonly HashSet<string> _completed;

    public OneShotStore(string? filename = null)
    {
        _filename = filename ?? FileFlowPaths.OneShot;
        try
        {
            _completed = File.Exists(_filename)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_filename)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "Cannot read one-shot completion history. To avoid duplicate one-time transfers, "
                + "repair or restore " + _filename, ex);
        }
    }

    private static string Key(TransferRule rule) => $"{rule.Id:N}:{rule.RunVersion:N}";

    public bool IsComplete(TransferRule rule) => _completed.Contains(Key(rule));

    public void MarkComplete(TransferRule rule)
    {
        var key = Key(rule);
        if (_completed.Add(key))
        {
            try { JsonFiles.WriteAtomically(_filename, _completed); }
            catch { _completed.Remove(key); throw; }
        }
    }
}
