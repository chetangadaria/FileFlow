using System.Security.Cryptography;

namespace FileFlow.Core;

/// <summary>
/// A polling transfer engine. It never deletes a source before destination verification.
/// Files are copied to a temporary sibling and then promoted to their final names.
/// </summary>
public sealed class TransferEngine
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromSeconds(2);
    private const int BufferSize = 128 * 1024;
    private readonly DailyJournal _journal;

    public TransferEngine(DailyJournal journal) => _journal = journal;

    public async Task<TransferSummary> ExecuteAsync(TransferRule rule, CancellationToken cancellationToken)
    {
        var validation = RuleValidation.Check(rule);
        if (validation is not null) throw new ArgumentException(validation);
        if (!Directory.Exists(rule.SourceDirectory))
            throw new DirectoryNotFoundException("Source folder unavailable: " + rule.SourceDirectory);
        Directory.CreateDirectory(rule.DestinationDirectory);

        var summary = new TransferSummary();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = rule.IncludeSubdirectories,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var source in Directory.EnumerateFiles(rule.SourceDirectory, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(rule.SourceDirectory, source);
            var destination = Path.Combine(rule.DestinationDirectory, relative);
            try
            {
                var result = await TransferOneAsync(rule, source, destination, cancellationToken);
                switch (result.Kind)
                {
                    case FileOutcome.Copied: summary.Copied++; summary.TransferredBytes += result.Bytes; break;
                    case FileOutcome.Moved: summary.Moved++; summary.TransferredBytes += result.Bytes; break;
                    case FileOutcome.Deferred: summary.Deferred++; break;
                    case FileOutcome.Skipped: summary.Skipped++; break;
                }
            }
            catch (IOException ex) when ((uint)ex.HResult is 0x80070020u or 0x80070021u)
            {
                // Normal producer/consumer sharing contention: retry when the writer closes.
                summary.Deferred++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or CryptographicException)
            {
                summary.Errors++;
                var detail = $"{source}: {ex.GetType().Name}: {ex.Message}";
                if (summary.ErrorMessages.Count < 5) summary.ErrorMessages.Add(detail);
                _journal.Write("ERROR", rule.Name, rule.Action.ToString(), source, destination, detail);
            }
        }
        return summary;
    }

    private async Task<(FileOutcome Kind, long Bytes)> TransferOneAsync(
        TransferRule rule, string source, string destination, CancellationToken ct)
    {
        var info = new FileInfo(source);
        if (!info.Exists) return (FileOutcome.Deferred, 0);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            return (FileOutcome.Skipped, 0);
        if (DateTime.UtcNow - info.LastWriteTimeUtc < MinimumAge)
            return (FileOutcome.Deferred, 0);

        if (File.Exists(destination))
        {
            var existing = new FileInfo(destination);
            if (rule.Action == TransferAction.Copy && SameMetadata(info, existing))
                return (FileOutcome.Skipped, 0);

            // If a previous MOVE completed the copy but could not delete its source,
            // finish it safely on the next scan after checking actual file hashes.
            if (rule.Action == TransferAction.Move && await SameContentAsync(source, destination, ct))
            {
                VerifySourceUnchanged(source, info.Length, info.LastWriteTimeUtc);
                File.Delete(source);
                _journal.Write("MOVED", rule.Name, "Move", source, destination,
                    "Identical destination verified (SHA-256); source removed.");
                return (FileOutcome.Moved, info.Length);
            }

            if (rule.OnConflict == ConflictPolicy.Skip)
                return (FileOutcome.Skipped, 0);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new IOException("Invalid destination folder."));

        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".fileflow-part";
        var sourceTime = info.LastWriteTimeUtc;
        var sourceLength = info.Length;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                // A writer holding the file open cannot coexist with our FileShare.Read handle.
                if (input.Length != sourceLength) return (FileOutcome.Deferred, 0);

                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, BufferSize, ct);
                    await output.FlushAsync(ct);
                    if (output.Length != sourceLength)
                        throw new IOException("Source size changed during transfer.");
                }

                VerifySourceUnchanged(source, sourceLength, sourceTime);
                if (rule.Action == TransferAction.Move)
                {
                    input.Position = 0;
                    var sourceHash = await SHA256.HashDataAsync(input, ct);
                    await using var tempReader = new FileStream(temp, FileMode.Open, FileAccess.Read,
                        FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var targetHash = await SHA256.HashDataAsync(tempReader, ct);
                    if (!sourceHash.AsSpan().SequenceEqual(targetHash))
                        throw new IOException("SHA-256 verification failed; source was retained.");
                }
            }

            VerifySourceUnchanged(source, sourceLength, sourceTime);
            File.SetLastWriteTimeUtc(temp, sourceTime);
            File.Move(temp, destination, overwrite: rule.OnConflict == ConflictPolicy.Overwrite);

            if (rule.Action == TransferAction.Move)
            {
                VerifySourceUnchanged(source, sourceLength, sourceTime);
                File.Delete(source);
                _journal.Write("MOVED", rule.Name, "Move", source, destination,
                    $"Verified with SHA-256, {sourceLength} bytes.");
                return (FileOutcome.Moved, sourceLength);
            }

            _journal.Write("COPIED", rule.Name, "Copy", source, destination,
                $"Copied {sourceLength} bytes.");
            return (FileOutcome.Copied, sourceLength);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* A failed cleanup cannot overwrite the transfer failure. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool SameMetadata(FileInfo source, FileInfo target)
    {
        target.Refresh();
        return source.Length == target.Length
            && source.LastWriteTimeUtc == target.LastWriteTimeUtc;
    }

    private static void VerifySourceUnchanged(string filename, long length, DateTime lastWriteUtc)
    {
        var info = new FileInfo(filename);
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != lastWriteUtc)
            throw new IOException("Source changed during processing; source was retained where possible.");
    }

    private static async Task<bool> SameContentAsync(string first, string second, CancellationToken ct)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        await using var one = new FileStream(first, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var two = new FileStream(second, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var oneHash = await SHA256.HashDataAsync(one, ct);
        var twoHash = await SHA256.HashDataAsync(two, ct);
        return oneHash.AsSpan().SequenceEqual(twoHash);
    }

    private enum FileOutcome { Copied, Moved, Skipped, Deferred }
}
