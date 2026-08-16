using FaustusControllerLite.Domain;

namespace FaustusControllerLite;

public enum SellRejectionReason
{
    None,
    ZeroHolding,
    MissingEdge,
    MissingDivineBenchmark,
    InvalidQuote,
    StaleQuote,
    SessionMismatch,
    AreaMismatch,
    NoWholeLot,
    ArithmeticOverflow,
    ProceedsBelowMinimum,
    UnbackedCompetingHead,
}

public enum SellSweepExecutionMode
{
    MostCurrency,
    FastestFillMarketRate,
}

public static class SellSweepExecutionModes
{
    public const string MostCurrencyLabel = "Most Currency";
    public const string FastestFillMarketRateLabel = "Fastest Fill (Market Rate)";

    public static IReadOnlyList<string> Labels { get; } =
        new[] { MostCurrencyLabel, FastestFillMarketRateLabel };

    public static string ToLabel(SellSweepExecutionMode mode) => mode switch
    {
        SellSweepExecutionMode.MostCurrency => MostCurrencyLabel,
        SellSweepExecutionMode.FastestFillMarketRate => FastestFillMarketRateLabel,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static QuoteExecutionIntent ToExecutionIntent(SellSweepExecutionMode mode) => mode switch
    {
        SellSweepExecutionMode.MostCurrency => QuoteExecutionIntent.Competing,
        SellSweepExecutionMode.FastestFillMarketRate => QuoteExecutionIntent.Immediate,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool TryParse(string? label, out SellSweepExecutionMode mode)
    {
        if (string.Equals(label, MostCurrencyLabel, StringComparison.Ordinal))
        {
            mode = SellSweepExecutionMode.MostCurrency;
            return true;
        }
        if (string.Equals(label, FastestFillMarketRateLabel, StringComparison.Ordinal))
        {
            mode = SellSweepExecutionMode.FastestFillMarketRate;
            return true;
        }

        mode = default;
        return false;
    }
}

public sealed record SellCandidateRequest(
    CurrencyIdentity Chaos,
    CurrencyIdentity Divine,
    CurrencyIdentity Target,
    long Holding,
    IReadOnlyCollection<DirectedExchangeEdge> Edges,
    DateTimeOffset Now,
    TimeSpan MaximumQuoteAge,
    string SessionId,
    string AreaId,
    long MinimumSaleChaos = 10,
    SellSweepExecutionMode ExecutionMode = SellSweepExecutionMode.MostCurrency,
    long MinCompetingQueue = CompetingLiquidityGate.DefaultMinCompetingQueue,
    long MaxCompetingSpread = CompetingLiquidityGate.DefaultMaxCompetingSpread);

public sealed record SellMarketQuote(
    DirectedExchangeEdge Edge,
    long Lots,
    long InputSpent,
    long Output,
    long InputRemainder,
    Rational ProceedsToChaosRate,
    long ProceedsChaos)
{
    public CurrencyIdentity Proceeds => Edge.To;

    public string Signature =>
        $"{Edge.From.Metadata}>{Edge.To.Metadata}:{Edge.ExecutionIntent}:{Edge.Rate}";
}

public sealed record SellMarketEvaluation(
    CurrencyIdentity Proceeds,
    SellMarketQuote? Quote,
    SellRejectionReason RejectionReason,
    string Detail)
{
    public bool Accepted => Quote is not null;
}

public sealed record SellCandidateResult(
    CurrencyIdentity Target,
    long Holding,
    SellMarketQuote? Best,
    IReadOnlyList<SellMarketEvaluation> Evaluations,
    SellRejectionReason RejectionReason,
    string Detail)
{
    public bool Accepted => Best is not null;
}

public static class FaustusSellPlanner
{
    private static readonly SellRejectionReason[] ReasonPriority =
    {
        SellRejectionReason.ArithmeticOverflow,
        SellRejectionReason.ProceedsBelowMinimum,
        SellRejectionReason.NoWholeLot,
        SellRejectionReason.StaleQuote,
        SellRejectionReason.SessionMismatch,
        SellRejectionReason.AreaMismatch,
        SellRejectionReason.UnbackedCompetingHead,
        SellRejectionReason.InvalidQuote,
        SellRejectionReason.MissingDivineBenchmark,
        SellRejectionReason.MissingEdge,
    };

    /// <summary>
    /// Every market's verdict, not just the one the priority table surfaced.
    ///
    /// A holding is skipped only when *both* proceeds markets refuse it, and they usually refuse for
    /// different reasons. <see cref="ReasonPriority"/> then reports one of them, which is fine for a
    /// single-line summary and actively misleading in a log: a Divine market rejected on lot size
    /// outranks a Chaos market rejected on an unbacked head, so the operator reads a lot-size
    /// complaint about a currency they were never going to be paid in, and never learns what
    /// actually blocked the market that mattered.
    /// </summary>
    public static string DescribeRejections(SellCandidateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Evaluations.Count == 0
            ? result.Detail
            : string.Join("; ", result.Evaluations.Select(evaluation => evaluation.Accepted
                ? $"{evaluation.Proceeds.Metadata}: accepted"
                : $"{evaluation.Proceeds.Metadata}: {evaluation.RejectionReason} ({evaluation.Detail})"));
    }

    public static SellCandidateResult Evaluate(SellCandidateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        if (request.Holding == 0)
        {
            return new SellCandidateResult(
                request.Target, request.Holding, null, Array.Empty<SellMarketEvaluation>(),
                SellRejectionReason.ZeroHolding, $"No {request.Target.Metadata} held.");
        }

        var modeIntent = SellSweepExecutionModes.ToExecutionIntent(request.ExecutionMode);
        var divineBenchmark = SelectEdge(request, request.Divine, request.Chaos, modeIntent, out _);
        var evaluations = new[]
        {
            EvaluateMarket(request, request.Chaos, divineBenchmark),
            EvaluateMarket(request, request.Divine, divineBenchmark),
        };

        var best = evaluations
            .Where(evaluation => evaluation.Accepted)
            .Select(evaluation => evaluation.Quote!)
            .OrderByDescending(quote => quote.ProceedsChaos)
            .ThenBy(quote => quote.Signature, StringComparer.Ordinal)
            .FirstOrDefault();

        if (best is not null)
        {
            return new SellCandidateResult(
                request.Target, request.Holding, best, evaluations, SellRejectionReason.None,
                $"Sell {best.InputSpent} {request.Target.Metadata} for {best.Output} " +
                $"{best.Proceeds.Metadata} worth {best.ProceedsChaos} chaos.");
        }

        var reason = SelectReason(evaluations.Select(evaluation => evaluation.RejectionReason));
        var detail = evaluations.First(evaluation => evaluation.RejectionReason == reason).Detail;
        return new SellCandidateResult(request.Target, request.Holding, null, evaluations, reason, detail);
    }

    private static SellMarketEvaluation EvaluateMarket(
        SellCandidateRequest request,
        CurrencyIdentity proceeds,
        DirectedExchangeEdge? divineBenchmark)
    {
        var intent = SellSweepExecutionModes.ToExecutionIntent(request.ExecutionMode);
        var intentName = intent.ToString().ToLowerInvariant();
        var edge = SelectEdge(request, request.Target, proceeds, intent, out var edgeReason);
        if (edge is null)
        {
            return new SellMarketEvaluation(proceeds, null, edgeReason,
                $"No usable {intentName} {request.Target.Metadata}->{proceeds.Metadata} quote.");
        }

        // A competing order is priced at a head it then has to wait behind, so the head has to be a real
        // market before the holding is sized against it. The sibling immediate edge is the anchor: it is
        // the only rate in this direction with a counterparty already resting on it. Rejecting one market
        // is enough - Evaluate scores Chaos and Divine independently and takes the better, so a trolled
        // Divine book simply loses to a healthy Chaos one.
        var immediateAnchor = SelectEdge(
            request, request.Target, proceeds, QuoteExecutionIntent.Immediate, out _);
        if (!CompetingLiquidityGate.IsBelievable(
                edge, immediateAnchor, request.MinCompetingQueue, request.MaxCompetingSpread, out var gateReason))
        {
            return new SellMarketEvaluation(proceeds, null, SellRejectionReason.UnbackedCompetingHead,
                $"{request.Target.Metadata}->{proceeds.Metadata} skipped: {gateReason}.");
        }

        try
        {
            var conversion = request.ExecutionMode == SellSweepExecutionMode.FastestFillMarketRate
                ? edge.Rate.ConvertWholeLots(request.Holding)
                : edge.Rate.ConvertWholeLots(request.Holding, edge.InputLimit);
            if (conversion.InputSpent == 0)
            {
                return new SellMarketEvaluation(proceeds, null, SellRejectionReason.NoWholeLot,
                    $"{request.Holding} {request.Target.Metadata} cannot fill one lot of " +
                    $"{edge.Rate.Denominator} for {proceeds.Metadata}.");
            }

            Rational proceedsToChaosRate;
            if (proceeds.Equals(request.Chaos))
            {
                proceedsToChaosRate = new Rational(1, 1);
            }
            else if (divineBenchmark is null)
            {
                return new SellMarketEvaluation(proceeds, null, SellRejectionReason.MissingDivineBenchmark,
                    $"No usable {intentName} {request.Divine.Metadata}->{request.Chaos.Metadata} quote " +
                    "to value the proceeds.");
            }
            else
            {
                proceedsToChaosRate = divineBenchmark.Rate;
            }

            var proceedsChaos = proceedsToChaosRate.FloorMultiply(conversion.Output);

            if (proceedsChaos < request.MinimumSaleChaos)
            {
                return new SellMarketEvaluation(proceeds, null, SellRejectionReason.ProceedsBelowMinimum,
                    $"{proceedsChaos} chaos from {proceeds.Metadata} is below the " +
                    $"{request.MinimumSaleChaos} minimum.");
            }

            var quote = new SellMarketQuote(edge, conversion.Lots, conversion.InputSpent,
                conversion.Output, conversion.InputRemainder, proceedsToChaosRate, proceedsChaos);
            return new SellMarketEvaluation(proceeds, quote, SellRejectionReason.None,
                $"{conversion.InputSpent} for {conversion.Output} {proceeds.Metadata} " +
                $"worth {proceedsChaos} chaos, {conversion.InputRemainder} left over.");
        }
        catch (OverflowException exception)
        {
            return new SellMarketEvaluation(proceeds, null, SellRejectionReason.ArithmeticOverflow,
                exception.Message);
        }
    }

    private static DirectedExchangeEdge? SelectEdge(
        SellCandidateRequest request,
        CurrencyIdentity from,
        CurrencyIdentity to,
        QuoteExecutionIntent intent,
        out SellRejectionReason reason)
    {
        var directed = request.Edges
            .Where(edge => edge.From.Equals(from) && edge.To.Equals(to) &&
                           edge.ExecutionIntent == intent)
            .ToArray();
        if (directed.Length == 0)
        {
            reason = SellRejectionReason.MissingEdge;
            return null;
        }

        var validations = directed.Select(edge => (Edge: edge, Reason: ValidateEdge(request, edge))).ToArray();
        var usable = validations
            .Where(pair => pair.Reason == SellRejectionReason.None)
            .Select(pair => pair.Edge)
            .ToArray();
        if (usable.Length == 0)
        {
            reason = SelectReason(validations.Select(pair => pair.Reason));
            return null;
        }

        reason = SellRejectionReason.None;

        // The freshest capture is the live book. An older capture may carry a better rate that no
        // longer leads the selected head, so recency outranks rate here.
        return usable
            .OrderByDescending(edge => edge.CapturedAt)
            .ThenByDescending(edge => edge.Rate)
            .ThenBy(edge => edge.CaptureId)
            .First();
    }

    private static SellRejectionReason ValidateEdge(SellCandidateRequest request, DirectedExchangeEdge edge)
    {
        if (edge.ImmediateInputDepth < 0 || edge.CompetingQueueAhead < 0)
        {
            return SellRejectionReason.InvalidQuote;
        }

        if (edge.ExecutionIntent == QuoteExecutionIntent.Immediate && edge.ImmediateInputDepth == 0)
        {
            return SellRejectionReason.InvalidQuote;
        }

        if (!StringComparer.Ordinal.Equals(edge.SessionId, request.SessionId))
        {
            return SellRejectionReason.SessionMismatch;
        }

        if (!StringComparer.Ordinal.Equals(edge.AreaId, request.AreaId))
        {
            return SellRejectionReason.AreaMismatch;
        }

        var age = request.Now - edge.CapturedAt;
        return age < TimeSpan.Zero || age > request.MaximumQuoteAge
            ? SellRejectionReason.StaleQuote
            : SellRejectionReason.None;
    }

    private static SellRejectionReason SelectReason(IEnumerable<SellRejectionReason> reasons)
    {
        var present = reasons.ToHashSet();
        return ReasonPriority.FirstOrDefault(present.Contains, SellRejectionReason.MissingEdge);
    }

    private static void ValidateRequest(SellCandidateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Chaos);
        ArgumentNullException.ThrowIfNull(request.Divine);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.Edges);
        _ = SellSweepExecutionModes.ToExecutionIntent(request.ExecutionMode);

        if (request.Chaos.Equals(request.Divine))
        {
            throw new ArgumentException("Chaos and Divine must be distinct currencies.", nameof(request));
        }

        if (request.Target.Equals(request.Chaos) || request.Target.Equals(request.Divine))
        {
            throw new ArgumentException("The sell target must not be a numeraire currency.", nameof(request));
        }

        if (request.Holding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Holding cannot be negative.");
        }

        if (request.MinimumSaleChaos < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Minimum sale cannot be negative.");
        }

        if (request.MinCompetingQueue < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Minimum competing queue cannot be negative.");
        }

        if (request.MaxCompetingSpread < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), "Maximum competing spread must be at least one.");
        }

        if (request.MaximumQuoteAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum quote age must be positive.");
        }

        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.AreaId))
        {
            throw new ArgumentException("Session and area are required.", nameof(request));
        }
    }
}
