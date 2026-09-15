namespace LeaseBook.Web.Onboarding;

/// <summary>The terminal classification of one staged import row.</summary>
internal enum ImportOutcomeKind
{
    Posted,
    AlreadyPosted,
    Unchanged,
    Superseded,
    Skipped,
    Error,
}

internal sealed record ImportBatchSummary(
    int RowCount,
    int ErrorCount,
    string Status,
    ImportOutcomeCounts Counts)
{
    /// <summary>Derives the persisted batch status and response counts from immutable row outcomes.</summary>
    internal static ImportBatchSummary From(IReadOnlyList<ImportOutcomeKind> outcomes)
    {
        var posted = 0;
        var alreadyPosted = 0;
        var unchanged = 0;
        var superseded = 0;
        var skipped = 0;
        var errors = 0;

        foreach (var outcome in outcomes)
        {
            switch (outcome)
            {
                case ImportOutcomeKind.Posted:
                    posted++;
                    break;
                case ImportOutcomeKind.AlreadyPosted:
                    alreadyPosted++;
                    break;
                case ImportOutcomeKind.Unchanged:
                    unchanged++;
                    break;
                case ImportOutcomeKind.Superseded:
                    superseded++;
                    break;
                case ImportOutcomeKind.Skipped:
                    skipped++;
                    break;
                case ImportOutcomeKind.Error:
                    errors++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown import outcome kind.");
            }
        }

        return new ImportBatchSummary(
            RowCount: outcomes.Count,
            ErrorCount: errors,
            Status: errors == 0 ? "posted" : "posted_with_errors",
            Counts: new ImportOutcomeCounts(posted, alreadyPosted, unchanged, superseded, skipped, errors));
    }
}
