using System.Collections.Concurrent;

namespace CodexApiBridge;

public sealed class BridgeState
{
    private readonly object _statsLock = new();
    private readonly object _logLock = new();
    private readonly string _logDirectory;
    private readonly ConcurrentQueue<LogEntry> _logs = new();

    public BridgeStats Stats { get; } = new();
    public event Action? StatsChanged;

    public BridgeState(string logDirectory)
    {
        _logDirectory = string.IsNullOrWhiteSpace(logDirectory) ? "logs" : logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public void Info(string message) => Add("INFO", message);
    public void Warn(string message) => Add("WARN", message);
    public void Error(string message) => Add("ERROR", message);

    public void Add(string level, string message)
    {
        Append(new LogEntry(DateTimeOffset.Now, level, message));
    }

    public IReadOnlyList<LogEntry> GetRecentLogs() => _logs.ToArray();

    public void PrintStats()
    {
        var stats = Snapshot();
        Info($"统计：总请求={stats.TotalRequests}, Responses={stats.ResponsesRequests}, Chat={stats.ChatRequests}, 流式={stats.StreamingRequests}, 失败={stats.FailedRequests}, Token={stats.TotalTokens}, 平均延迟={stats.AverageLatencyMs:0}ms");
    }

    public void MarkStarted()
    {
        lock (_statsLock)
        {
            Stats.StartedAt = DateTimeOffset.Now;
        }

        RaiseStatsChanged();
    }

    public void MarkRequest(string kind, bool streaming)
    {
        lock (_statsLock)
        {
            Stats.TotalRequests++;
            if (kind == "responses")
            {
                Stats.ResponsesRequests++;
            }
            else if (kind == "chat")
            {
                Stats.ChatRequests++;
            }

            if (streaming)
            {
                Stats.StreamingRequests++;
            }
        }

        RaiseStatsChanged();
    }

    public void MarkFailure()
    {
        lock (_statsLock)
        {
            Stats.FailedRequests++;
        }

        RaiseStatsChanged();
    }

    public void AddUsage(long prompt, long completion, long total, double latencyMs)
    {
        lock (_statsLock)
        {
            Stats.PromptTokens += prompt;
            Stats.CompletionTokens += completion;
            Stats.TotalTokens += total;
        }

        RaiseStatsChanged();
    }

    public void RecordCompletedRequest(string kind, bool streaming, bool success, int statusCode, double latencyMs)
    {
        lock (_statsLock)
        {
            Stats.LastRequestAt = DateTimeOffset.Now;
            Stats.LastStatusCode = statusCode;
            Stats.MaxLatencyMs = Math.Max(Stats.MaxLatencyMs, latencyMs);
            Stats.AverageLatencyMs = Stats.TotalRequests <= 1
                ? latencyMs
                : (Stats.AverageLatencyMs * (Stats.TotalRequests - 1) + latencyMs) / Stats.TotalRequests;
        }

        RaiseStatsChanged();
    }

    public BridgeStats Snapshot()
    {
        lock (_statsLock)
        {
            return Stats.Snapshot();
        }
    }

    private void Append(LogEntry entry)
    {
        _logs.Enqueue(entry);
        while (_logs.Count > 5000 && _logs.TryDequeue(out _))
        {
        }

        var line = entry.ToString();
        Console.WriteLine(line);

        lock (_logLock)
        {
            var file = Path.Combine(_logDirectory, $"bridge-{DateTimeOffset.Now:yyyyMMdd}.log");
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    private void RaiseStatsChanged()
    {
        StatsChanged?.Invoke();
    }
}
