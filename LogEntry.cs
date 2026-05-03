namespace CodexApiBridge;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message)
{
    public override string ToString() => $"[{Time:HH:mm:ss}] {Level,-5} {Message}";
}

public sealed class BridgeStats
{
    public long TotalRequests { get; set; }
    public long ResponsesRequests { get; set; }
    public long ChatRequests { get; set; }
    public long StreamingRequests { get; set; }
    public long FailedRequests { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public double AverageLatencyMs { get; set; }
    public double MaxLatencyMs { get; set; }
    public int LastStatusCode { get; set; }
    public DateTimeOffset? LastRequestAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }

    public BridgeStats Snapshot() => new()
    {
        TotalRequests = TotalRequests,
        ResponsesRequests = ResponsesRequests,
        ChatRequests = ChatRequests,
        StreamingRequests = StreamingRequests,
        FailedRequests = FailedRequests,
        PromptTokens = PromptTokens,
        CompletionTokens = CompletionTokens,
        TotalTokens = TotalTokens,
        AverageLatencyMs = AverageLatencyMs,
        MaxLatencyMs = MaxLatencyMs,
        LastStatusCode = LastStatusCode,
        LastRequestAt = LastRequestAt,
        StartedAt = StartedAt
    };
}
