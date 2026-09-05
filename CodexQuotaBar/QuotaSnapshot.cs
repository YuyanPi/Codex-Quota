namespace CodexQuotaBar;

public sealed record QuotaWindow(
    int? RemainingPercent,
    int? WindowDurationMinutes,
    DateTimeOffset? ResetsAt);

public sealed record QuotaSnapshot(
    string ModelName,
    QuotaWindow FiveHour,
    QuotaWindow Weekly,
    DateTimeOffset FetchedAt,
    string SourceName);
