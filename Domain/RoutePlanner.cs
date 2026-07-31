namespace FaustusControllerLite;

public enum RouteRejectionReason
{
    None,
    ZeroBankroll,
    MissingEdge,
    MissingDivineBenchmark,
    StaleQuote,
    SessionMismatch,
    AreaMismatch,
    TooManyCompetingEdges,
    NoWholeLot,
    NoImmediateDepth,
    ArithmeticOverflow,
    ProfitBelowMinimum,
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
    long MinimumProfitChaos = 5);

public sealed record RouteLegResult(
    DirectedExchangeEdge Edge,
    long InputAvailable,
    long InputSpent,
    long Output,
    long InputRemainder);

public sealed record RouteCandidate(
    IReadOnlyList<CurrencyIdentity> Path,
    IReadOnlyList<RouteLegResult> Legs,
    long StartingPrincipal,
    long BenchmarkChaos,
    long RealizedChaos,
    long ProfitChaos,
    IReadOnlyDictionary<CurrencyIdentity, long> Remainders,
    int CompetingEdgeCount,
    long CompetingQueueAhead)
{
    public string Signature => string.Join(">", Path.Select(currency => currency.Metadata));

    public string ExecutionSignature => string.Join("|", Legs.Select(leg =>
        $"{leg.Edge.From.Metadata}>{leg.Edge.To.Metadata}:{leg.Edge.Provenance}:{leg.Edge.Rate}"));
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
                .ThenBy(edge => edge.Provenance)
                .ThenByDescending(edge => edge.CapturedAt)
                .ToArray());

        var paths = new[]
        {
            new[] { request.Chaos, request.Target, request.Chaos },
            new[] { request.Chaos, request.Divine, request.Chaos },
            new[] { request.Chaos, request.Divine, request.Target, request.Chaos },
            new[] { request.Chaos, request.Target, request.Divine, request.Chaos },
            new[] { request.Divine, request.Target, request.Chaos },
        };

        var evaluations = paths.SelectMany(path => EvaluatePathVariants(request, path, edgeLookup)).ToArray();
        var candidates = evaluations
            .Where(evaluation => evaluation.Candidate is not null)
            .Select(evaluation => evaluation.Candidate!)
            .OrderByDescending(candidate => candidate.ProfitChaos)
            .ThenBy(candidate => candidate.CompetingEdgeCount)
            .ThenBy(candidate => candidate.CompetingQueueAhead)
            .ThenByDescending(candidate => candidate.RealizedChaos)
            .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ExecutionSignature, StringComparer.Ordinal)
            .ToArray();

        return new RoutePlannerResult(candidates, evaluations);
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

        return combinations.Select(pathEdges => EvaluatePath(request, path, pathEdges, edges)).ToArray();
    }

    private static RouteEvaluation EvaluatePath(
        RoutePlannerRequest request,
        CurrencyIdentity[] path,
        IReadOnlyList<DirectedExchangeEdge> pathEdges,
        IReadOnlyDictionary<(CurrencyIdentity From, CurrencyIdentity To), IReadOnlyList<DirectedExchangeEdge>> edges)
    {
        foreach (var edge in pathEdges)
        {
            var validation = ValidateEdge(request, edge);
            if (validation != RouteRejectionReason.None)
            {
                return Reject(path, validation, $"Invalid {edge.From.Metadata}->{edge.To.Metadata} quote.");
            }
        }

        var competingCount = pathEdges.Count(edge => edge.Provenance == QuoteProvenance.Competing);
        if (competingCount > 1)
        {
            return Reject(path, RouteRejectionReason.TooManyCompetingEdges, "A route may contain at most one competing edge.");
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
            var legs = new List<RouteLegResult>(pathEdges.Count);
            var remainders = new Dictionary<CurrencyIdentity, long>();
            foreach (var edge in pathEdges)
            {
                if (edge.Provenance == QuoteProvenance.Immediate && edge.ImmediateInputDepth == 0)
                {
                    return Reject(path, RouteRejectionReason.NoImmediateDepth, $"No immediate depth for {edge.From.Metadata}->{edge.To.Metadata}.");
                }

                var conversion = edge.Rate.ConvertWholeLots(amount, edge.InputLimit);
                if (conversion.InputSpent == 0)
                {
                    return Reject(path, RouteRejectionReason.NoWholeLot, $"No whole lot for {edge.From.Metadata}->{edge.To.Metadata}.");
                }

                if (conversion.InputRemainder > 0)
                {
                    AddRemainder(remainders, edge.From, conversion.InputRemainder);
                }

                legs.Add(new RouteLegResult(edge, amount, conversion.InputSpent, conversion.Output, conversion.InputRemainder));
                amount = conversion.Output;
            }

            var principal = legs[0].InputSpent;
            long benchmark;
            if (path[0].Equals(request.Chaos))
            {
                benchmark = principal;
            }
            else
            {
                if (!edges.TryGetValue((request.Divine, request.Chaos), out var benchmarkChoices))
                {
                    return Reject(path, RouteRejectionReason.MissingDivineBenchmark, "A Divine start requires a Divine->Chaos benchmark.");
                }

                var benchmarkEdge = benchmarkChoices.FirstOrDefault(edge =>
                    edge.Provenance == QuoteProvenance.Immediate && ValidateEdge(request, edge) == RouteRejectionReason.None);
                if (benchmarkEdge is null)
                {
                    var immediate = benchmarkChoices.FirstOrDefault(edge => edge.Provenance == QuoteProvenance.Immediate);
                    var validation = immediate is null ? RouteRejectionReason.None : ValidateEdge(request, immediate);
                    return Reject(path, validation == RouteRejectionReason.None
                            ? RouteRejectionReason.MissingDivineBenchmark
                            : validation,
                        "The Divine->Chaos benchmark must be fresh, matching, and immediate.");
                }

                var benchmarkConversion = benchmarkEdge.Rate.ConvertWholeLots(principal, benchmarkEdge.ImmediateInputDepth);
                if (benchmarkConversion.InputSpent == 0)
                {
                    return Reject(path, RouteRejectionReason.NoWholeLot, "Divine principal cannot form a benchmark whole lot.");
                }

                benchmark = benchmarkConversion.Output;
            }

            var profit = checked(amount - benchmark);
            if (profit < request.MinimumProfitChaos)
            {
                return Reject(path, RouteRejectionReason.ProfitBelowMinimum,
                    $"Realized profit {profit} is below {request.MinimumProfitChaos} Chaos.");
            }

            var queue = pathEdges
                .Where(edge => edge.Provenance == QuoteProvenance.Competing)
                .Aggregate(0L, (total, edge) => checked(total + edge.CompetingQueueAhead));
            var candidate = new RouteCandidate(path, legs, principal, benchmark, amount, profit, remainders,
                competingCount, queue);
            return new RouteEvaluation(path, candidate, RouteRejectionReason.None, string.Empty);
        }
        catch (OverflowException exception)
        {
            return Reject(path, RouteRejectionReason.ArithmeticOverflow, exception.Message);
        }
    }

    private static RouteRejectionReason ValidateEdge(RoutePlannerRequest request, DirectedExchangeEdge edge)
    {
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

        _ = request.ChaosBankroll.Available;
        _ = request.DivineBankroll.Available;
    }
}
