using System.Globalization;
using System.Text.Json;

namespace Sfg.Toolkit.ReviewCalib;

/// <summary>The parsed, validated events export. Loud loader: a malformed
/// export never passes silently — the format is the contract (#77).</summary>
public sealed class ReviewCalibEventsExport
{
    public required string Path { get; init; }

    public required string FileName { get; init; }

    /// <summary>The raw export text — findings cite line numbers into it.</summary>
    public required string Raw { get; init; }

    public required IReadOnlyList<ReviewCalibReview> Reviews { get; init; }

    /// <summary>The recorded analysis anchor (source.today, yyyy-MM-dd) —
    /// staleness is measured against it, never against the wall clock.</summary>
    public required DateTimeOffset Today { get; init; }

    public string? Repository { get; init; }

    public string? Window { get; init; }

    /// <summary>1-based line of the first line containing the needle — the
    /// anchor every finding cites. A missing needle anchors on line 1 so the
    /// file:line contract holds even for absent rows.</summary>
    public string Anchor(string needle)
    {
        var line = 1;
        using var reader = new StringReader(Raw);
        while (reader.ReadLine() is { } current)
        {
            if (current.Contains(needle, StringComparison.Ordinal))
            {
                return $"{FileName}:{line}";
            }

            line++;
        }

        return $"{FileName}:1";
    }
}

/// <summary>
/// The reviewer calibration harness (#77). Reads a committed, recorded
/// export of review decisions — never an API live — and measures the human
/// reviewers of the queues: seeded-defect catch rates per role, approval
/// rate and decision-latency telemetry per queue, human-vs-judge
/// disagreement, and the rung evidence the autonomy ladder is earned on. A
/// calibration instrument, never a leaderboard.
/// </summary>
public static class ReviewCalibAnalyzer
{
    public static ReviewCalibEventsExport Load(ReviewCalibConfig config)
    {
        var path = config.EventsFullPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"review-calib events export not found: {path} — the harness reads a recorded export (produce one in the documented format; it never calls an API live)");
        }

        var raw = File.ReadAllText(path);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"review-calib events export is not valid JSON: {path}: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"review-calib events export must be a JSON object: {path}");
            }

            if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "review-calib events export needs a \"source\" object with \"today\" (yyyy-MM-dd) — the analysis anchor; the harness is deterministic and never reads the wall clock");
            }

            if (!source.TryGetProperty("today", out var todayElement) ||
                todayElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParseExact(
                    todayElement.GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var today))
            {
                throw new InvalidOperationException(
                    "review-calib events export needs a \"source.today\" date (yyyy-MM-dd, e.g. 2026-10-20) — the analysis anchor; the harness is deterministic and never reads the wall clock");
            }

            string? repository = null;
            string? window = null;
            if (source.TryGetProperty("repository", out var repositoryElement) &&
                repositoryElement.ValueKind == JsonValueKind.String)
            {
                repository = repositoryElement.GetString();
            }

            if (source.TryGetProperty("window", out var windowElement) && windowElement.ValueKind == JsonValueKind.String)
            {
                window = windowElement.GetString();
            }

            if (!root.TryGetProperty("reviews", out var reviews) || reviews.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"review-calib events export needs a \"reviews\" array: {path}");
            }

            var reviewList = new List<ReviewCalibReview>();
            foreach (var entry in reviews.EnumerateArray())
            {
                var id = RequireString(entry, "id", "a review");
                var decision = RequireString(entry, "decision", $"review '{id}'");
                if (decision is not ("approve" or "edit" or "reject" or "escalate"))
                {
                    throw new InvalidOperationException(
                        $"review '{id}' records decision '{decision}' which is not a decision (approve, edit, reject, escalate)");
                }

                var submittedAt = ReadTimestamp(entry, "submittedAt", $"review '{id}'");
                var decidedAt = ReadTimestamp(entry, "decidedAt", $"review '{id}'");
                if (decidedAt < submittedAt)
                {
                    throw new InvalidOperationException(
                        $"review '{id}' was decided before it was submitted — a decision cannot precede the question");
                }

                if (!entry.TryGetProperty("editsBeforeDecision", out var editsElement) ||
                    editsElement.ValueKind != JsonValueKind.Number ||
                    !editsElement.TryGetInt32(out var edits) || edits < 0)
                {
                    throw new InvalidOperationException(
                        $"review '{id}' needs a non-negative integer \"editsBeforeDecision\" — an examination leaves traces");
                }

                bool? judgeAgrees = null;
                if (entry.TryGetProperty("judgeAgrees", out var judgeElement) && judgeElement.ValueKind != JsonValueKind.Null)
                {
                    if (judgeElement.ValueKind != JsonValueKind.True && judgeElement.ValueKind != JsonValueKind.False)
                    {
                        throw new InvalidOperationException(
                            $"review '{id}' has an invalid \"judgeAgrees\" — it must be true, false, or null");
                    }

                    judgeAgrees = judgeElement.GetBoolean();
                }

                ReviewCalibSeed? seeded = null;
                if (entry.TryGetProperty("seeded", out var seededElement) && seededElement.ValueKind != JsonValueKind.Null)
                {
                    if (seededElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException(
                            $"review '{id}' has an invalid \"seeded\" — it must be an object (seedId, expectedDecision) or null");
                    }

                    var seedId = RequireString(seededElement, "seedId", $"review '{id}' seeded defect");
                    var expectedDecision = RequireString(seededElement, "expectedDecision", $"seeded defect '{seedId}'");
                    if (expectedDecision is not ("reject" or "edit"))
                    {
                        throw new InvalidOperationException(
                            $"seeded defect '{seedId}' expects '{expectedDecision}' — a seeded defect is expected to be rejected or edited, never approved");
                    }

                    seeded = new ReviewCalibSeed { SeedId = seedId, ExpectedDecision = expectedDecision };
                }

                reviewList.Add(new ReviewCalibReview
                {
                    Id = id,
                    Queue = RequireString(entry, "queue", $"review '{id}'"),
                    ReviewerRole = RequireString(entry, "reviewerRole", $"review '{id}'"),
                    Reviewer = RequireString(entry, "reviewer", $"review '{id}'"),
                    SubmittedAt = submittedAt,
                    DecidedAt = decidedAt,
                    Decision = decision,
                    EditsBeforeDecision = edits,
                    JudgeAgrees = judgeAgrees,
                    Seeded = seeded,
                });
            }

            foreach (var group in reviewList.GroupBy(r => r.Id, StringComparer.Ordinal))
            {
                if (group.Count() > 1)
                {
                    throw new InvalidOperationException(
                        $"duplicate review id '{group.Key}' — one row per review, ambiguous is never allowed");
                }
            }

            return new ReviewCalibEventsExport
            {
                Path = path,
                FileName = System.IO.Path.GetFileName(path),
                Raw = raw,
                // machine order: deterministic everywhere
                Reviews = reviewList
                    .OrderBy(r => r.DecidedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray(),
                Today = today,
                Repository = repository,
                Window = window,
            };
        }
    }

    public static ReviewCalibReport Analyze(ReviewCalibConfig config, ReviewCalibEventsExport export)
    {
        var findings = new List<ReviewCalibFinding>();
        string Anchor(ReviewCalibReview review) => export.Anchor($"\"id\": \"{review.Id}\"");

        var reviews = export.Reviews;

        // ── the seeded defects: a clean approve on a planted defect is the
        // one miss the whole instrument exists to catch (#77 done-when)
        var missedSeeds = reviews
            .Where(r => r.Seeded is not null && !r.CaughtSeed)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var missed in missedSeeds)
        {
            findings.Add(new ReviewCalibFinding(
                "calib/seed-missed", "error",
                $"seeded defect '{missed.Seeded!.SeedId}' expected '{missed.Seeded.ExpectedDecision}' but was approved cleanly — " +
                $"reviewer '{missed.Reviewer}' ({missed.ReviewerRole}) approved '{missed.Id}' in queue '{missed.Queue}' in " +
                $"{Seconds(missed.DecisionSeconds)}s with {missed.EditsBeforeDecision} edit(s) before the decision",
                [Anchor(missed)]));
        }

        // ── catch rates per role — a calibration floor, never a leaderboard
        var roles = new List<ReviewCalibRoleRow>();
        foreach (var group in reviews.GroupBy(r => r.ReviewerRole, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var seeded = group.Count(r => r.Seeded is not null);
            var caught = group.Count(r => r.CaughtSeed);
            var role = group.Key;
            roles.Add(new ReviewCalibRoleRow
            {
                Role = role,
                Reviews = group.Count(),
                Seeded = seeded,
                Caught = caught,
                CatchRate = seeded == 0 ? (double?)null : caught / (double)seeded,
            });

            if (seeded > 0 && caught / (double)seeded < config.CatchRateMin)
            {
                var missedAnchors = group
                    .Where(r => r.Seeded is not null && !r.CaughtSeed)
                    .OrderBy(r => r.Id, StringComparer.Ordinal)
                    .Take(3)
                    .Select(Anchor)
                    .ToArray();
                findings.Add(new ReviewCalibFinding(
                    "calib/catch-rate-below", "error",
                    $"role '{role}' caught {caught} of {seeded} seeded defect(s) ({Percent(caught / (double)seeded)}) — " +
                    $"below the {Percent(config.CatchRateMin)} calibration floor; a role that waves defects through is a rubber stamp with extra steps",
                    missedAnchors));
            }
        }

        // ── queue telemetry: approval rate and decision latency side by
        // side — a queue that approves nearly everything in under a second
        // is not reviewing, it is a broken loop
        var queues = new List<ReviewCalibQueueRow>();
        foreach (var group in reviews.GroupBy(r => r.Queue, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var queue = group.Key;
            var rows = group.OrderBy(r => r.DecidedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
            var approves = rows.Count(r => r.Decision == "approve");
            var seeded = rows.Count(r => r.Seeded is not null);
            var caught = rows.Count(r => r.CaughtSeed);
            var median = MedianSeconds(rows.Select(r => r.DecisionSeconds).ToArray());
            var approvalRate = approves / (double)rows.Length;
            queues.Add(new ReviewCalibQueueRow
            {
                Queue = queue,
                Reviews = rows.Length,
                Approves = approves,
                Edits = rows.Count(r => r.Decision == "edit"),
                Rejects = rows.Count(r => r.Decision == "reject"),
                Escalations = rows.Count(r => r.Decision == "escalate"),
                ApprovalRate = approvalRate,
                MedianSeconds = median,
                Seeded = seeded,
                Caught = caught,
                CatchRate = seeded == 0 ? (double?)null : caught / (double)seeded,
                LastSeededDecidedAt = rows.Where(r => r.Seeded is not null).Select(r => (DateTimeOffset?)r.DecidedAt).Max(),
            });

            var rubber = rows.Length >= config.RubberStampMinReviews &&
                         approvalRate >= config.RubberStampApprovalRate &&
                         median < config.RubberStampMedianSeconds;
            if (rubber)
            {
                findings.Add(new ReviewCalibFinding(
                    "calib/rubber-stamp-queue", "error",
                    $"queue '{queue}' approved {approves} of {rows.Length} review(s) ({Percent(approvalRate)}) at a median " +
                    $"decision latency of {Seconds(median)}s — this is not review, it is a broken loop rubber-stamping the queue",
                    [Anchor(rows[0]), Anchor(rows[^1])]));
            }
            else if (rows.Length >= 2 && median < config.RubberStampMedianSeconds)
            {
                var fastest = rows.OrderBy(r => r.DecisionSeconds).ThenBy(r => r.Id, StringComparer.Ordinal).First();
                findings.Add(new ReviewCalibFinding(
                    "calib/sub-second-approval", "warning",
                    $"queue '{queue}' has a median decision latency of {Seconds(median)}s — approvals this fast are clicks, not examinations; watch it before it becomes a broken loop",
                    [Anchor(fastest)]));
            }

            // ── staleness: a queue that is never measured cannot be said to work
            var freshSeeded = rows.Count(r =>
                r.Seeded is not null && r.DecidedAt <= export.Today &&
                r.DecidedAt >= export.Today.AddDays(-config.StalenessDays));
            if (freshSeeded == 0)
            {
                findings.Add(new ReviewCalibFinding(
                    "calib/stale-queue", "warning",
                    $"queue '{queue}' has no seeded-defect decision within {config.StalenessDays} days of {export.Today:yyyy-MM-dd} — " +
                    "the calibration evidence is stale; a queue that is never measured cannot be said to work",
                    [export.Anchor($"\"queue\": \"{queue}\"")]));
            }
        }

        // ── judge calibration: human decisions against the recorded judge —
        // disagreement is evidence, surfaced on the board, never a ranking
        var judged = reviews.Where(r => r.JudgeAgrees is not null).ToArray();
        var disagreements = judged.Where(r => r.JudgeAgrees == false)
            .Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var agreement = judged.Length == 0 ? (double?)null : judged.Count(r => r.JudgeAgrees == true) / (double)judged.Length;

        // ── the rung evidence export: what an earned-autonomy rung claim
        // must cite — the ladder is earned by people, the board only exports
        var rungs = queues.Select(q => new ReviewCalibRungRow
        {
            Queue = q.Queue,
            Seeded = q.Seeded,
            Caught = q.Caught,
            CatchRate = q.CatchRate,
            ApprovalRate = q.ApprovalRate,
            MedianSeconds = q.MedianSeconds,
            JudgeAgreement = AgreementOf(q.Queue),
            LastSeededDecidedAt = q.LastSeededDecidedAt,
        }).ToArray();

        double? AgreementOf(string queue)
        {
            var rows = reviews.Where(r => r.Queue == queue && r.JudgeAgrees is not null).ToArray();
            return rows.Length == 0 ? (double?)null : rows.Count(r => r.JudgeAgrees == true) / (double)rows.Length;
        }

        findings.Sort(ReviewCalibFinding.Comparer);
        return new ReviewCalibReport
        {
            Reviews = reviews,
            Roles = roles,
            Queues = queues,
            Rungs = rungs,
            SeededCount = reviews.Count(r => r.Seeded is not null),
            CaughtCount = reviews.Count(r => r.CaughtSeed),
            Agreement = agreement,
            JudgeSample = judged.Length,
            Disagreements = disagreements,
            Findings = findings,
            Cap = config.MaxFindings,
            Today = export.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Repository = export.Repository,
            Window = export.Window,
        };
    }

    private static double MedianSeconds(IReadOnlyList<double> samples)
    {
        var ordered = samples.OrderBy(s => s).ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0;
        return Round1(median);
    }

    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static string Percent(double fraction) =>
        Round1(fraction * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    // ── the loud export reader ─────────────────────────────────────────────

    private static string RequireString(JsonElement element, string name, string context)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"{context} needs a non-empty \"{name}\"");
        }

        return value.GetString()!;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement element, string name, string context)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{context} needs a \"{name}\" — timestamps are ISO 8601 UTC, e.g. 2026-10-05T09:00:00Z");
        }

        var text = value.GetString()!;
        if (!DateTimeOffset.TryParseExact(
                text,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new InvalidOperationException(
                $"{context} has an invalid \"{name}\" '{text}' — timestamps are ISO 8601 UTC, e.g. 2026-10-05T09:00:00Z");
        }

        return timestamp;
    }
}
