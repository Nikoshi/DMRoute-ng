using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DMRoute_ng.Services;

public sealed record RadioCheckOutboxEntry
{
    public ulong MessageId { get; init; }
    public int OriginZone { get; init; }
    public int HomeZone { get; init; }
    public int SourceId { get; init; }
    public int TargetId { get; init; }
    public long CreatedAtTicks { get; init; }
    public long ExpiresAtTicks { get; init; }
    public long LastAttemptTicks { get; set; }
}

public sealed record RadioCheckOutboxDocument
{
    public List<RadioCheckOutboxEntry> Entries { get; init; } = [];
}

public sealed partial class RadioCheckOutbox
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(10);
    private readonly ILogger<RadioCheckOutbox> _logger;
    private readonly string _path;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly List<RadioCheckOutboxEntry> _entries;

    public RadioCheckOutbox(ILogger<RadioCheckOutbox> logger, string path, int capacity, long nowTicks)
    {
        _logger = logger;
        _path = Path.GetFullPath(path);
        _capacity = capacity;
        _entries = Load(nowTicks);
    }

    public int Count
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    public bool TryAdd(in RadioCheckOutboxEntry entry)
    {
        lock (_lock)
        {
            for (var index = 0; index < _entries.Count; index++)
            {
                var existing = _entries[index];
                if (existing.MessageId == entry.MessageId) return true;
                if (existing.SourceId == entry.SourceId && existing.TargetId == entry.TargetId &&
                    existing.CreatedAtTicks >= entry.CreatedAtTicks - DuplicateWindow.Ticks) return true;
            }

            if (_entries.Count >= _capacity) return false;
            _entries.Add(entry);
            if (SaveLocked()) return true;
            _entries.RemoveAt(_entries.Count - 1);
            return false;
        }
    }

    public RadioCheckOutboxEntry[] Snapshot(long nowTicks)
    {
        lock (_lock)
        {
            var changed = _entries.RemoveAll(entry => entry.ExpiresAtTicks <= nowTicks) > 0;
            if (changed) SaveLocked();
            return [.. _entries];
        }
    }

    public bool MarkAttempt(ulong messageId, long attemptAtTicks)
    {
        lock (_lock)
        {
            var entry = _entries.Find(candidate => candidate.MessageId == messageId);
            if (entry is null) return false;
            entry.LastAttemptTicks = attemptAtTicks;
            return SaveLocked();
        }
    }

    public bool Remove(ulong messageId)
    {
        lock (_lock)
        {
            var index = _entries.FindIndex(entry => entry.MessageId == messageId);
            if (index < 0) return false;
            var entry = _entries[index];
            _entries.RemoveAt(index);
            if (SaveLocked()) return true;
            _entries.Insert(index, entry);
            return false;
        }
    }

    private List<RadioCheckOutboxEntry> Load(long nowTicks)
    {
        if (!File.Exists(_path)) return new List<RadioCheckOutboxEntry>(_capacity);
        try
        {
            using var stream = File.OpenRead(_path);
            var document = JsonSerializer.Deserialize(stream, RadioCheckOutboxJsonContext.Default.RadioCheckOutboxDocument);
            var result = document?.Entries ?? [];
            result.RemoveAll(entry => entry.ExpiresAtTicks <= nowTicks);
            if (result.Count > _capacity) result.RemoveRange(_capacity, result.Count - _capacity);
            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            LogLoadFailed(_logger, _path, exception.Message);
            return new List<RadioCheckOutboxEntry>(_capacity);
        }
    }

    private bool SaveLocked()
    {
        var directory = Path.GetDirectoryName(_path);
        try
        {
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var document = new RadioCheckOutboxDocument { Entries = _entries };
                JsonSerializer.Serialize(stream, document, RadioCheckOutboxJsonContext.Default.RadioCheckOutboxDocument);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, _path, exception.Message);
            return false;
        }
    }

    [LoggerMessage(3201, LogLevel.Error, "Radio-Check-Outbox {Path} konnte nicht geladen werden: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, string path, string error);

    [LoggerMessage(3202, LogLevel.Error, "Radio-Check-Outbox {Path} konnte nicht gespeichert werden: {Error}")]
    private static partial void LogSaveFailed(ILogger logger, string path, string error);
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RadioCheckOutboxDocument))]
internal sealed partial class RadioCheckOutboxJsonContext : JsonSerializerContext;
