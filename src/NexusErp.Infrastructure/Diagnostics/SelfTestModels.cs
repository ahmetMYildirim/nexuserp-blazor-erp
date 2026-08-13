namespace NexusErp.Infrastructure.Diagnostics;

public enum CheckOutcome
{
    Passed = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>
/// Tek bir kontrolün sonucu.
/// <paramref name="Detail"/> GERÇEK sayı içermeli ("340 birim ücretlendirildi"),
/// "başarılı" demek yeterli değil — kontrolün gerçekten çalıştığını okuyan kişi
/// ancak somut çıktıdan anlar.
/// </summary>
public sealed record CheckResult(
    string Category,
    string Name,
    CheckOutcome Outcome,
    string Detail,
    string Why,
    int DurationMs)
{
    public bool Passed => Outcome == CheckOutcome.Passed;
    public bool Failed => Outcome == CheckOutcome.Failed;

    public string OutcomeText => Outcome switch
    {
        CheckOutcome.Passed => "GEÇTİ",
        CheckOutcome.Failed => "KALDI",
        _ => "ATLANDI"
    };

    public string OutcomeVariant => Outcome switch
    {
        CheckOutcome.Passed => "accent-2",
        CheckOutcome.Failed => "outline",
        _ => "neutral"
    };
}

public sealed record SelfTestRun(
    DateTimeOffset StartedAt, int DurationMs, IReadOnlyList<CheckResult> Results)
{
    public int Total => Results.Count;
    public int PassedCount => Results.Count(r => r.Outcome == CheckOutcome.Passed);
    public int FailedCount => Results.Count(r => r.Outcome == CheckOutcome.Failed);
    public int SkippedCount => Results.Count(r => r.Outcome == CheckOutcome.Skipped);

    public bool AllPassed => FailedCount == 0;

    public IReadOnlyList<string> Categories =>
        [.. Results.Select(r => r.Category).Distinct()];

    public IEnumerable<CheckResult> InCategory(string category) =>
        Results.Where(r => r.Category == category);

    public string Summary => FailedCount == 0
        ? $"{PassedCount} kontrolün tamamı geçti ({DurationMs} ms)."
        : $"{FailedCount} kontrol KALDI, {PassedCount} geçti ({DurationMs} ms).";
}
