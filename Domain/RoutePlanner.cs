using System.Numerics;

namespace FaustusControllerLite;

public enum RouteRejectionReason
{
    None,
    ZeroBankroll,
    MissingEdge,
    MissingDivineBenchmark,
    InvalidQuote,
    StaleQuote,
    SessionMismatch,
    AreaMismatch,
    TooManyCompetingEdges,
    NoWholeLot,
    NoImmediateDepth,
    Underfunded,
    UnderLiquid,
    ArithmeticOverflow,
    ProfitBelowMinimum,
    NoPriceImprovement,
}

public sealed record CurrencyBankroll(long LedgerAmount, long LiveAmount)
{
    public long Available
    {
        get
        {
            if (LedgerAmount < 0 || LiveAmount < 0)
            {
                throw new InvalidOperationException("Bankroll amounts cannot be negative.");
            }

            return Math.Min(LedgerAmount, LiveAmount);
        }
    }
}

public sealed record RoutePlannerRequest(
    CurrencyIdentity Chaos,
    CurrencyIdentity Divine,
    CurrencyIdentity Target,
    CurrencyBankroll ChaosBankroll,
    CurrencyBankroll DivineBankroll,
    IReadOnlyCollection<DirectedExchangeEdge> Edges,
    DateTimeOffset Now,
    TimeSpan MaximumQuoteAge,
    string SessionId,
    string AreaId,
    long MinimumProfitChaos = 5,
    long? ExpectedGoldPerLeg = null,
    int MaximumCompetingEdges = 2,
    bool EnableDirectDivineCycles = false,
    long MaximumDirectDivinePrincipal = 1_000,
    bool PrioritizeValuedProfit = false,
    bool EnableCompetingPriceImprovement = false);

public enum RouteCycleKind
{
    ChaosCycle = 1,
    DivinePrincipalRestoration = 2,
    SameAssetDivineCycle = 3,
}

public sealed record RouteLegResult(
    DirectedExchangeEdge Edge,
    long InputAvailable,
    long InputSpent,
    long Output,
    long InputRemainder,
    long? ExpectedGold);

public sealed record RouteCandidate(
    IReadOnlyList<CurrencyIdentity> Path,
    IReadOnlyList<RouteLegResult> Legs,
    long StartingPrincipal,
    long BenchmarkChaos,
    long RealizedChaos,
    long ProfitChaos,
    IReadOnlyDictionary<CurrencyIdentity, long> Remainders,
    int CompetingEdgeCount,
    long CompetingQueueAhead,
    long? ExpectedGold,
    int ChaosRealizationLegIndex = -1,
    long RestorationPrincipal = 0,
    long PlannedRestorationSpendChaos = 0,
    RouteCycleKind CycleKind = RouteCycleKind.ChaosCycle,
    string ProfitMetadata = "",
    long ProfitAmount = 0,
    long ValuedProfitChaos = 0,
    long PlannedSettlementAmount = 0,
    DirectedExchangeEdge? ProfitValuationEdge = null,
    int ImprovedCompetingLegCount = 0)
{
    public string Signature => string.Join(">", Path.Select(currency => currency.Metadata));

    public string ExecutionSignature => string.Join("|", Legs.Select(leg =>
        $"{leg.Edge.From.Metadata}>{leg.Edge.To.Metadata}:{leg.Edge.ExecutionIntent}:{leg.Edge.Rate}"));
}

public sealed record RouteEvaluation(
    IReadOnlyList<CurrencyIdentity> Path,
    RouteCandidate? Candidate,
    RouteRejectionReason RejectionReason,
    string Detail)
{
    public bool Accepted => Candidate is not null;
}

public sealed record RoutePlannerResult(
    IReadOnlyList<RouteCandidate> Candidates,
    IReadOnlyList<RouteEvaluation> Evaluations)
{
    public RouteCandidate? Best => Candidates.Count == 0 ? null : Candidates[0];
}

public static class FaustusRoutePlanner
{
    public static RoutePlannerResult Evaluate(RoutePlannerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var edgeLookup = request.Edges
            .GroupBy(edge => (edge.From, edge.To))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DirectedExchangeEdge>)group
                .OrderByDescending(edge => edge.Rate)
                .ThenBy(edge => edge.ExecutionIntent)
                .ThenByDescending(edge => edge.CapturedAt)
                .ToArray());

        var paths = new[]
        {
            new[] { request.Chaos, request.Target, request.Chaos },
            new[] { request.Chaos, request.Divine, request.Target, request.Chaos },
            new[] { request.Chaos, request.Target, request.Divine, request.Chaos },
            new[] { request.Divine, request.Target, request.Chaos, request.Divine },
        };

        var evaluations = paths.SelectMany(path => EvaluatePathVariants(request, path, edgeLookup)).ToList();
        if (request.EnableDirectDivineCycles)
            evaluations.AddRange(EvaluateDirectDivineCycleVariants(request, edgeLookup));
        var accepted = evaluations
            .Where(evaluation => evaluation.Candidate is not null)
            .Select(evaluation => evaluation.Candidate!);
        // A constant key when improvement is off leaves the established order untouched; when it is
        // on, more improved legs win outright because queue position is the point of the feature.
        var ranked = accepted.OrderByDescending(candidate =>
            request.EnableCompetingPriceImprovement ? candidate.ImprovedCompetingLegCount : 0);
        var candidates = request.PrioritizeValuedProfit
            ? ranked.ThenByDescending(candidate => candidate.ValuedProfitChaos)
                .ThenBy(candidate => candidate.CompetingEdgeCount)
                .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ExecutionSignature, StringComparer.Ordinal)
                .ToArray()
            : ranked.ThenBy(candidate => candidate.CompetingEdgeCount)
                .ThenByDescending(candidate => candidate.ProfitChaos)
                .ThenByDescending(candidate => candidate.RealizedChaos)
                .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ExecutionSignature, StringComparer.Ordinal)
                .ToArray();

        return new RoutePlannerResult(candidates, evaluations);
    }

    /// <summary>
    /// Builds the minimum one-unit price improvement for a single competing leg. The order either
    /// offers one more input unit for the same output or wants one less output unit for the same
    /// input; both strictly lower the rate, and a lower rate is what places the order ahead of the
    /// competing head instead of behind it. Whichever candidate keeps the higher rate is chosen,
    /// because that sacrifices the least. The improvement is refused unless the improved rate stays
    /// strictly above the immediate rate for the same direction: at or below it the order would
    /// cross the spread and merely take liquidity the immediate edge already quoted.
    /// </summary>
    public static bool TryImproveCompetingLeg(
        DirectedExchangeEdge competing,
        DirectedExchangeEdge? immediate,
        long inputAvailable,
        long inputSpent,
        long output,
        out DirectedExchangeEdge? improved,
        out long improvedInputSpent,
        out long improvedOutput)
    {
        ArgumentNullException.ThrowIfNull(competing);
        improved = null;
        improvedInputSpent = 0;
        improvedOutput = 0;
        if (competing.ExecutionIntent != QuoteExecutionIntent.Competing ||
            inputSpent <= 0 || output <= 0 || inputAvailable < inputSpent || inputSpent == long.MaxValue)
        {
            return false;
        }

        if (immediate is null || immediate.ExecutionIntent != QuoteExecutionIntent.Immediate ||
            !immediate.From.Equals(competing.From) || !immediate.To.Equals(competing.To))
        {
            return false;
        }

        if (immediate.Rate >= new Rational(output, inputSpent))
        {
            return false;
        }

        var candidates = new List<(long InputSpent, long Output)>(2);
        if (inputSpent < inputAvailable)
        {
            candidates.Add((inputSpent + 1, output));
        }

        if (output > 1)
        {
            candidates.Add((inputSpent, output - 1));
        }

        var bestRate = default(Rational);
        foreach (var candidate in candidates)
        {
            var rate = new Rational(candidate.Output, candidate.InputSpent);
            if (rate <= immediate.Rate || improved is not null && rate <= bestRate)
            {
                continue;
            }

            bestRate = rate;
            improvedInputSpent = candidate.InputSpent;
            improvedOutput = candidate.Output;
            improved = competing with
            {
                Rate = rate,
                CompetingQueueAhead = 0,
                SourceBook = QuoteBookSource.ImprovedCompeting,
            };
        }

        return improved is not null;
    }

    /// <summary>
    /// The freshest valid immediate quote for the competing leg's own direction, highest rate first
    /// so the improvement is bounded by the strictest reading of the spread available.
    /// </summary>
    private static DirectedExchangeEdge? SelectImmediate(
        RoutePlannerRequest request,
        IReadOnlyDictionary<(CurrencyIdentity From, CurrencyIdentity To), IReadOnlyList<DirectedExchangeEdge>> edges,
        DirectedExchangeEdge competing) =>
        edges.TryGetValue((competing.From, competing.To), out var choices)
            ? choices.FirstOrDefault(edge => edge.ExecutionIntent == QuoteExecutionIntent.Immediate &&
                ValidateEdge(request, edge) == RouteRejectionReason.None)
            : null;

    private static IEnumerable<RouteEvaluation> EvaluateDirectDivineCycleVariants(
        RoutePlannerRequest request,
        IReadOnlyDictionary<(CurrencyIdentity From, CurrencyIdentity To), IReadOnlyList<DirectedExchangeEdge>> edges)
    {
        var path = new[] { request.Divine, request.Target, request.Divine };
        if (!edges.TryGetValue((request.Divine, request.Target), out var openingChoices) ||
            !edges.TryGetValue((request.Target, request.Divine), out var closingChoices))
            return [Reject(path, RouteRejectionReason.MissingEdge, "Direct Divine cycle requires both Divine/target directions.")];

        openingChoices = openingChoices.Where(edge => edge.ExecutionIntent == QuoteExecutionIntent.Competing).ToArray();
        closingChoices = closingChoices.Where(edge => edge.ExecutionIntent == QuoteExecutionIntent.Competing).ToArray();
        if (openingChoices.Count == 0 || closingChoices.Count == 0)
            return [Reject(path, RouteRejectionReason.MissingEdge, "Direct Divine cycle requires competing quotes in both directions.")];

        if (!edges.TryGetValue((request.Divine, request.Chaos), out var valuationChoices))
            return [Reject(path, RouteRejectionReason.MissingDivineBenchmark, "Direct Divine profit requires an Immediate Divine/Chaos valuation.")];
        var valuation = valuationChoices.FirstOrDefault(edge =>
            edge.ExecutionIntent == QuoteExecutionIntent.Immediate && ValidateEdge(request, edge) == RouteRejectionReason.None);
        if (valuation is null)
            return [Reject(path, RouteRejectionReason.MissingDivineBenchmark, "Direct Divine profit valuation must be fresh, matching, and Immediate.")];

        (bool Opening, bool Closing)[] improvements = request.EnableCompetingPriceImprovement
            ? [(false, false), (true, false), (false, true), (true, true)]
            : [(false, false)];
        return (from opening in openingChoices
                from closing in closingChoices
                from improvement in improvements
                select EvaluateDirectDivineCycle(
                    request, path, opening, closing, valuation, edges,
                    improvement.Opening, improvement.Closing)).ToArray();
    }

    private static RouteEvaluation EvaluateDirectDivineCycle(
        RoutePlannerRequest request,
        CurrencyIdentity[] path,
        DirectedExchangeEdge opening,
        DirectedExchangeEdge closing,
        DirectedExchangeEdge valuation,
        IReadOnlyDictionary<(CurrencyIdentity From, CurrencyIdentity To), IReadOnlyList<DirectedExchangeEdge>> edges,
        bool improveOpening,
        bool improveClosing)
    {
        foreach (var edge in new[] { opening, closing, valuation })
        {
            var validation = ValidateEdge(request, edge);
            if (validation != RouteRejectionReason.None)
                return Reject(path, validation, $"Invalid direct-cycle quote {edge.From.Metadata}->{edge.To.Metadata}.");
        }
        if (request.MaximumCompetingEdges < 2)
            return Reject(path, RouteRejectionReason.TooManyCompetingEdges, "Direct Divine cycle requires two competing legs.");

        try
        {
            var available = Math.Min(request.DivineBankroll.Available, request.MaximumDirectDivinePrincipal);
            if (available <= 0) return Reject(path, RouteRejectionReason.ZeroBankroll, "No Divine principal is available.");
            var buy = opening.Rate.ConvertWholeLots(available, opening.InputLimit);
            if (buy.InputSpent <= 0 || buy.Output <= 0)
                return Reject(path, RouteRejectionReason.Underfunded, "Divine principal cannot form one direct-cycle opening lot.");
            var openingEdge = opening;
            var openingInput = buy.InputSpent;
            var openingOutput = buy.Output;
            var openingRemainder = buy.InputRemainder;
            if (improveOpening)
            {
                if (!TryImproveCompetingLeg(
                        opening, SelectImmediate(request, edges, opening), available, openingInput, openingOutput,
                        out var improvedOpening, out var improvedInput, out var improvedOutput))
                {
                    return Reject(path, RouteRejectionReason.NoPriceImprovement,
                        "No safe one-unit improvement for the direct-cycle opening leg.");
                }

                openingEdge = improvedOpening!;
                openingInput = improvedInput;
                openingOutput = improvedOutput;
                openingRemainder = checked(available - improvedInput);
            }

            var sell = closing.Rate.ConvertWholeLots(openingOutput, closing.InputLimit);
            if (sell.InputSpent <= 0 || sell.Output <= openingInput)
                return Reject(path, RouteRejectionReason.ProfitBelowMinimum, "Direct Divine cycle does not return more Divine than it spends.");
            var closingEdge = closing;
            var closingInput = sell.InputSpent;
            var closingOutput = sell.Output;
            var closingRemainder = sell.InputRemainder;
            if (improveClosing)
            {
                if (!TryImproveCompetingLeg(
                        closing, SelectImmediate(request, edges, closing), openingOutput, closingInput, closingOutput,
                        out var improvedClosing, out var improvedInput, out var improvedOutput))
                {
                    return Reject(path, RouteRejectionReason.NoPriceImprovement,
                        "No safe one-unit improvement for the direct-cycle closing leg.");
                }

                closingEdge = improvedClosing!;
                closingInput = improvedInput;
                closingOutput = improvedOutput;
                closingRemainder = checked(openingOutput - improvedInput);
                if (closingOutput <= openingInput)
                    return Reject(path, RouteRejectionReason.ProfitBelowMinimum,
                        "The improved direct Divine cycle does not return more Divine than it spends.");
            }

            var profitDivine = checked(closingOutput - openingInput);
            var valued = valuation.Rate.ConvertWholeLots(profitDivine, valuation.InputLimit);
            if (valued.InputSpent != profitDivine || valued.Output <= 0)
                return Reject(path, RouteRejectionReason.UnderLiquid, "Immediate Divine/Chaos depth cannot value the full direct-cycle profit.");
            if (valued.Output < request.MinimumProfitChaos)
                return Reject(path, RouteRejectionReason.ProfitBelowMinimum,
                    $"Direct-cycle profit {profitDivine} Divine is valued at {valued.Output} Chaos, below {request.MinimumProfitChaos}.");

            var remainders = new Dictionary<CurrencyIdentity, long>();
            if (openingRemainder > 0) AddRemainder(remainders, request.Divine, openingRemainder);
            if (closingRemainder > 0) AddRemainder(remainders, request.Target, closingRemainder);
            var legs = new[]
            {
                new RouteLegResult(openingEdge, available, openingInput, openingOutput, openingRemainder, request.ExpectedGoldPerLeg),
                new RouteLegResult(closingEdge, openingOutput, closingInput, closingOutput, closingRemainder, request.ExpectedGoldPerLeg),
            };
            var expectedGold = request.ExpectedGoldPerLeg is null
                ? (long?)null
                : checked(request.ExpectedGoldPerLeg.Value * 2);
            var queue = checked(openingEdge.CompetingQueueAhead + closingEdge.CompetingQueueAhead);
            var candidate = new RouteCandidate(
                path, legs, openingInput, 0, 0, valued.Output, remainders, 2, queue, expectedGold,
                -1, 0, 0, RouteCycleKind.SameAssetDivineCycle, request.Divine.Metadata,
                profitDivine, valued.Output, closingOutput, valuation,
                (improveOpening ? 1 : 0) + (improveClosing ? 1 : 0));
            return new RouteEvaluation(path, candidate, RouteRejectionReason.None, string.Empty);
        }
        catch (OverflowException exception)
        {
            return Reject(path, RouteRejectionReason.ArithmeticOverflow, exception.Message);
        }
    }

    private static IEnumerable<RouteEvaluation> EvaluatePathVariants(
        RoutePlannerRequest request,
        CurrencyIdentity[] path,
        IReadOnlyDictionary<(CurrencyIdentity From, CurrencyIdentity To), IReadOnlyList<DirectedExchangeEdge>> edges)
    {
        var edgeChoices = new List<IReadOnlyList<DirectedExchangeEdge>>(path.Length - 1);
        for (var index = 0; index < path.Length - 1; index++)
        {
            if (!edges.TryGetValue((path[index], path[index + 1]), out var choices))
            {
                return new[]
                {
                    Reject(path, RouteRejectionReason.MissingEdge,
                        $"Missing {path[index].Metadata}->{path[index + 1].Metadata}."),
                };
            }

            if (path[0].Equals(request.Divine) && index == path.Length - 2)
            {
                choices = choices.Where(edge => edge.ExecutionIntent == QuoteExecutionIntent.Immediate).ToArray();
                if (choices.Count == 0)
                {
                    return new[]
                    {
                        Reject(path, RouteRejectionReason.NoImmediateDepth,
                            $"Closing {path[index].Metadata}->{path[index + 1].Metadata} has no Immediate quote."),
                    };
                }
            }

            edgeChoices.Add(choices);
        }

        IEnumerable<IReadOnlyList<DirectedExchangeEdge>> combinations =
            new[] { (IReadOnlyList<DirectedExchangeEdge>)Array.Empty<DirectedExchangeEdge>() };
        foreach (var choices in edgeChoices)
        {
            combinations = combinations.SelectMany(
                selected => choices,
                (selected, choice) => (IReadOnlyList<DirectedExchangeEdge>)selected.Append(choice).ToArray());
        }

        return combinations
            .SelectMany(pathEdges => EvaluateImprovementVariants(request, path, pathEdges, edges))
            .ToArray();
    }

    /// <summary>
    /// One evaluation per independent choice of which competing legs are improved. The all-original
    /// variant is always produced, so a variant whose improvement is unsafe or unprofitable is only
    /// outranked by that fallback rather than costing the route entirely.
    /// </summary>
    private static IEnumerable<RouteEvaluation> EvaluateImprovementVariants(
        RoutePlannerRequest request,
        CurrencyIdentity[] path,
        IReadOnlyList<DirectedExchangeEdge> pathEdges,
        IReadOnlyDictionary<(CurrencyIdentity From, CurrencyIdentity To), IReadOnlyList<DirectedExchangeEdge>> edges)
    {
        var original = EvaluatePath(request, path, pathEdges, edges, improvementMask: 0);
        if (!request.EnableCompetingPriceImprovement)
        {
            return [original];
        }

        var competingIndices = new List<int>(pathEdges.Count);
        for (var index = 0; index < pathEdges.Count; index++)
        {
            if (pathEdges[index].ExecutionIntent == QuoteExecutionIntent.Competing)
            {
                competingIndices.Add(index);
            }
        }

        if (competingIndices.Count == 0)
        {
            return [original];
        }

        var variants = new List<RouteEvaluation>(1 << competingIndices.Count) { original };
        for (var subset = 1; subset < 1 << competingIndices.Count; subset++)
        {
            var improvementMask = 0;
            for (var bit = 0; bit < competingIndices.Count; bit++)
            {
                if ((subset & 1 << bit) != 0)
                {
                    improvementMask |= 1 << competingIndices[bit];
                }
            }

            variants.Add(EvaluatePath(request, path, pathEdges, edges, improvementMask));
        }

        return variants;
    }

    private static RouteEvaluation EvaluatePath(
        RoutePlannerRequest request,
        CurrencyIdentity[] path,
        IReadOnlyList<DirectedExchangeEdge> pathEdges,
        IReadOnlyDictionary<(CurrencyIdentity From, CurrencyIdentity To), IReadOnlyList<DirectedExchangeEdge>> edges,
        int improvementMask)
    {
        foreach (var edge in pathEdges)
        {
            var validation = ValidateEdge(request, edge);
            if (validation != RouteRejectionReason.None)
            {
                return Reject(path, validation, $"Invalid {edge.From.Metadata}->{edge.To.Metadata} quote.");
            }
        }

        var competingCount = pathEdges.Count(edge => edge.ExecutionIntent == QuoteExecutionIntent.Competing);
        if (competingCount > request.MaximumCompetingEdges)
        {
            return Reject(path, RouteRejectionReason.TooManyCompetingEdges,
                $"A route may contain at most {request.MaximumCompetingEdges} competing edges.");
        }

        var startingBankroll = path[0].Equals(request.Chaos)
            ? request.ChaosBankroll.Available
            : request.DivineBankroll.Available;
        if (startingBankroll == 0)
        {
            return Reject(path, RouteRejectionReason.ZeroBankroll, "The ledger/live-capped starting bankroll is zero.");
        }

        try
        {
            var amount = startingBankroll;
            var divineStart = path[0].Equals(request.Divine);
            var closingEdge = divineStart ? pathEdges[^1] : null;
            if (closingEdge is not null)
            {
                var firstEdge = pathEdges[0];
                if (closingEdge.ExecutionIntent != QuoteExecutionIntent.Immediate ||
                    closingEdge.ImmediateInputDepth < closingEdge.Rate.Denominator ||
                    firstEdge.ExecutionIntent == QuoteExecutionIntent.Immediate &&
                    firstEdge.ImmediateInputDepth < firstEdge.Rate.Denominator)
                {
                    return Reject(path, RouteRejectionReason.UnderLiquid,
                        "Divine principal cannot form one whole lot within opening and restoration depth.");
                }

                var inputLimit = amount;
                if (firstEdge.ExecutionIntent == QuoteExecutionIntent.Immediate)
                {
                    inputLimit = Math.Min(inputLimit, firstEdge.ImmediateInputDepth);
                }

                var restorationLots = closingEdge.ImmediateInputDepth / closingEdge.Rate.Denominator;
                var restorablePrincipal = checked(restorationLots * closingEdge.Rate.Numerator);
                inputLimit = Math.Min(inputLimit, restorablePrincipal);

                var commonLot = LeastCommonMultiple(
                    firstEdge.Rate.Denominator,
                    closingEdge.Rate.Numerator);
                amount = checked(inputLimit / commonLot * commonLot);
                if (amount == 0)
                {
                    return Reject(path, RouteRejectionReason.Underfunded,
                        "Divine bankroll cannot form a whole lot shared by opening and restoration.");
                }
            }

            var legs = new List<RouteLegResult>(pathEdges.Count);
            var remainders = new Dictionary<CurrencyIdentity, long>();
            if (path[0].Equals(request.Divine) && startingBankroll > amount)
            {
                AddRemainder(remainders, request.Divine, startingBankroll - amount);
            }

            var ordinaryLegCount = divineStart ? pathEdges.Count - 1 : pathEdges.Count;
            for (var edgeIndex = 0; edgeIndex < ordinaryLegCount; edgeIndex++)
            {
                var edge = pathEdges[edgeIndex];
                if (amount < edge.Rate.Denominator)
                {
                    return Reject(path, RouteRejectionReason.Underfunded,
                        $"Available {edge.From.Metadata} cannot form one quote lot.");
                }

                if (edge.ExecutionIntent == QuoteExecutionIntent.Immediate &&
                    edge.ImmediateInputDepth < edge.Rate.Denominator)
                {
                    return Reject(path, RouteRejectionReason.UnderLiquid,
                        $"Immediate depth cannot fill one {edge.From.Metadata}->{edge.To.Metadata} lot.");
                }

                var conversion = edge.Rate.ConvertWholeLots(amount, edge.InputLimit);
                if (conversion.InputSpent == 0)
                {
                    return Reject(path, RouteRejectionReason.NoWholeLot, $"No whole lot for {edge.From.Metadata}->{edge.To.Metadata}.");
                }

                var legEdge = edge;
                var legInput = conversion.InputSpent;
                var legOutput = conversion.Output;
                var legRemainder = conversion.InputRemainder;
                if ((improvementMask & 1 << edgeIndex) != 0)
                {
                    if (!TryImproveCompetingLeg(
                            edge, SelectImmediate(request, edges, edge), amount, legInput, legOutput,
                            out var improvedEdge, out var improvedInput, out var improvedOutput))
                    {
                        return Reject(path, RouteRejectionReason.NoPriceImprovement,
                            $"No safe one-unit improvement for {edge.From.Metadata}->{edge.To.Metadata}.");
                    }

                    legEdge = improvedEdge!;
                    legInput = improvedInput;
                    legOutput = improvedOutput;
                    legRemainder = checked(amount - improvedInput);
                }

                if (legRemainder > 0)
                {
                    AddRemainder(remainders, edge.From, legRemainder);
                }

                legs.Add(new RouteLegResult(
                    legEdge,
                    amount,
                    legInput,
                    legOutput,
                    legRemainder,
                    request.ExpectedGoldPerLeg));
                amount = legOutput;
            }

            var principal = legs[0].InputSpent;
            var chaosRealizationLegIndex = legs.Count - 1;
            var realizedChaos = amount;
            long benchmark;
            long restorationPrincipal;
            long restorationSpend;
            long profit;
            if (!divineStart)
            {
                benchmark = principal;
                restorationPrincipal = 0;
                restorationSpend = 0;
                profit = checked(realizedChaos - benchmark);
            }
            else
            {
                if (closingEdge is null || !closingEdge.Rate.TryGetInputForExactOutput(principal, out restorationSpend))
                {
                    return Reject(path, RouteRejectionReason.NoWholeLot,
                        "The Divine principal is incompatible with the closing quote output lot.");
                }
                if (restorationSpend <= 0 || restorationSpend > closingEdge.ImmediateInputDepth)
                {
                    return Reject(path, RouteRejectionReason.UnderLiquid,
                        "Immediate closing depth cannot restore the full Divine principal.");
                }
                if (restorationSpend > realizedChaos)
                {
                    return Reject(path, RouteRejectionReason.Underfunded,
                        "Route-produced Chaos cannot restore the full Divine principal.");
                }

                restorationPrincipal = principal;
                benchmark = restorationSpend;
                profit = checked(realizedChaos - restorationSpend);
                legs.Add(new RouteLegResult(
                    closingEdge,
                    realizedChaos,
                    restorationSpend,
                    restorationPrincipal,
                    profit,
                    request.ExpectedGoldPerLeg));
            }

            if (profit < request.MinimumProfitChaos)
            {
                return Reject(path, RouteRejectionReason.ProfitBelowMinimum,
                    $"Realized profit {profit} is below {request.MinimumProfitChaos} Chaos.");
            }

            // Improved legs rest ahead of the whole queue, so the aggregate reads the legs that were
            // actually planned rather than the quoted heads they were derived from.
            var queue = legs
                .Where(leg => leg.Edge.ExecutionIntent == QuoteExecutionIntent.Competing)
                .Aggregate(0L, (total, leg) => checked(total + leg.Edge.CompetingQueueAhead));
            var expectedGold = request.ExpectedGoldPerLeg is null
                ? (long?)null
                : checked(request.ExpectedGoldPerLeg.Value * pathEdges.Count);
            var cycleKind = divineStart
                ? RouteCycleKind.DivinePrincipalRestoration
                : RouteCycleKind.ChaosCycle;
            var candidate = new RouteCandidate(path, legs, principal, benchmark, realizedChaos, profit, remainders,
                competingCount, queue, expectedGold, chaosRealizationLegIndex, restorationPrincipal, restorationSpend,
                cycleKind, request.Chaos.Metadata, profit, profit, realizedChaos, null,
                BitOperations.PopCount((uint)improvementMask));
            return new RouteEvaluation(path, candidate, RouteRejectionReason.None, string.Empty);
        }
        catch (OverflowException exception)
        {
            return Reject(path, RouteRejectionReason.ArithmeticOverflow, exception.Message);
        }
    }

    private static RouteRejectionReason ValidateEdge(RoutePlannerRequest request, DirectedExchangeEdge edge)
    {
        if (edge.ImmediateInputDepth < 0 || edge.CompetingQueueAhead < 0)
        {
            return RouteRejectionReason.InvalidQuote;
        }

        if (!StringComparer.Ordinal.Equals(edge.SessionId, request.SessionId))
        {
            return RouteRejectionReason.SessionMismatch;
        }

        if (!StringComparer.Ordinal.Equals(edge.AreaId, request.AreaId))
        {
            return RouteRejectionReason.AreaMismatch;
        }

        var age = request.Now - edge.CapturedAt;
        return age < TimeSpan.Zero || age > request.MaximumQuoteAge
            ? RouteRejectionReason.StaleQuote
            : RouteRejectionReason.None;
    }

    private static long LeastCommonMultiple(long left, long right)
    {
        var divisor = Rational.GreatestCommonDivisor(left, right);
        var value = (BigInteger)(left / divisor) * right;
        if (value > long.MaxValue)
        {
            throw new OverflowException("Required common quote lot exceeds Int64.");
        }

        return (long)value;
    }

    private static void AddRemainder(IDictionary<CurrencyIdentity, long> remainders, CurrencyIdentity currency, long amount)
    {
        remainders.TryGetValue(currency, out var existing);
        remainders[currency] = checked(existing + amount);
    }

    private static RouteEvaluation Reject(
        IReadOnlyList<CurrencyIdentity> path,
        RouteRejectionReason reason,
        string detail) => new(path, null, reason, detail);

    private static void ValidateRequest(RoutePlannerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Chaos);
        ArgumentNullException.ThrowIfNull(request.Divine);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(request.ChaosBankroll);
        ArgumentNullException.ThrowIfNull(request.DivineBankroll);
        ArgumentNullException.ThrowIfNull(request.Edges);
        if (request.Chaos.Equals(request.Divine) || request.Chaos.Equals(request.Target) || request.Divine.Equals(request.Target))
        {
            throw new ArgumentException("Chaos, Divine, and target must be distinct currencies.", nameof(request));
        }

        if (request.MaximumQuoteAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumQuoteAge));
        }

        if (request.MinimumProfitChaos < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MinimumProfitChaos));
        }

        if (request.ExpectedGoldPerLeg < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedGoldPerLeg));
        }

        if (request.MaximumCompetingEdges is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumCompetingEdges));
        }
        if (request.MaximumDirectDivinePrincipal <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumDirectDivinePrincipal));

        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.AreaId))
        {
            throw new ArgumentException("Session and area identities are required.", nameof(request));
        }

        _ = request.ChaosBankroll.Available;
        _ = request.DivineBankroll.Available;
    }
}
