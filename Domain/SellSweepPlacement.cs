using FaustusControllerLite.Orders;
using FaustusControllerLite.Probing;

namespace FaustusControllerLite.Domain;

public sealed record SellSweepPlacementToken(
    Guid SweepId,
    int CandidateIndex,
    string CandidateMetadata,
    Guid ProbeSessionId,
    string League,
    int AreaInstanceId,
    string PreparedSignature,
    CurrencyIdentity From,
    CurrencyIdentity To,
    QuoteExecutionIntent ExecutionIntent,
    Rational Rate,
    QuoteBookSource SourceBook,
    long InputAvailable,
    long InputSpent,
    long Output,
    long InputRemainder,
    CurrencyIdentity Proceeds,
    Rational ProceedsToChaosRate,
    DateTimeOffset ExpiresAtUtc);

public static class SellSweepPlacement
{
    public static bool TryValidatePrepared(
        SellSweepState sweep,
        SellSweepPlacementToken? token,
        RouteLegResult stagedLeg,
        Guid currentProbeSessionId,
        string currentLeague,
        int currentAreaInstanceId,
        DateTimeOffset now,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(stagedLeg);
        var candidate = sweep.Current;
        if (token is null || now > token.ExpiresAtUtc || !sweep.IsActive ||
            candidate is null || candidate.Outcome != SellSweepCandidateOutcome.Pending ||
            sweep.IsMetadataSlotted(candidate.Metadata) ||
            token.SweepId != sweep.SweepId || token.CandidateIndex != sweep.CurrentIndex ||
            token.CandidateIndex != candidate.Index ||
            !string.Equals(token.CandidateMetadata, candidate.Metadata, StringComparison.Ordinal) ||
            token.ProbeSessionId != currentProbeSessionId ||
            token.ProbeSessionId != sweep.OriginProbeSessionId ||
            !string.Equals(token.League, currentLeague, StringComparison.Ordinal) ||
            !string.Equals(token.League, sweep.League, StringComparison.Ordinal) ||
            token.AreaInstanceId != currentAreaInstanceId ||
            string.IsNullOrWhiteSpace(token.PreparedSignature) ||
            !string.Equals(token.PreparedSignature, sweep.PreparedSignature, StringComparison.Ordinal) ||
            !string.Equals(token.PreparedSignature, candidate.PlannedSignature, StringComparison.Ordinal))
        {
            failure = "Sweep placement token expired or its sweep, candidate, session, league, area, or signature changed.";
            return false;
        }

        if (!IdentityMatches(token.From, stagedLeg.Edge.From) || !IdentityMatches(token.To, stagedLeg.Edge.To) ||
            !IdentityMatches(token.To, token.Proceeds) ||
            !string.Equals(token.From.Metadata, candidate.Metadata, StringComparison.Ordinal) ||
            !string.Equals(stagedLeg.Edge.SessionId, token.ProbeSessionId.ToString("D"), StringComparison.Ordinal) ||
            !string.Equals(stagedLeg.Edge.AreaId,
                token.AreaInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            token.ExecutionIntent != SellSweepExecutionModes.ToExecutionIntent(sweep.ExecutionMode) ||
            stagedLeg.Edge.ExecutionIntent != token.ExecutionIntent ||
            stagedLeg.Edge.Rate != token.Rate || stagedLeg.Edge.SourceBook != token.SourceBook ||
            stagedLeg.InputAvailable != token.InputAvailable || stagedLeg.InputSpent != token.InputSpent ||
            stagedLeg.Output != token.Output || stagedLeg.InputRemainder != token.InputRemainder ||
            token.InputAvailable != candidate.HoldingAtScan ||
            token.InputSpent != candidate.PlannedInputSpent || token.Output != candidate.PlannedOutput ||
            token.InputRemainder != candidate.PlannedInputRemainder ||
            !string.Equals(token.Proceeds.Metadata, candidate.PlannedProceedsMetadata, StringComparison.Ordinal) ||
            token.ProceedsToChaosRate != candidate.PlannedProceedsToChaosRate)
        {
            failure = "Sweep placement edge identity, intent, rate, amounts, proceeds, or valuation changed.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool TryValidateLiveMarket(
        SellSweepPlacementToken token,
        MarketCapture capture,
        DateTimeOffset now,
        out string failure,
        long minCompetingQueue = CompetingLiquidityGate.DefaultMinCompetingQueue,
        long maxCompetingSpread = CompetingLiquidityGate.DefaultMaxCompetingSpread)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(capture);
        if (now > token.ExpiresAtUtc || capture.SessionId != token.ProbeSessionId ||
            !string.Equals(capture.League, token.League, StringComparison.Ordinal) ||
            capture.AreaInstanceId != token.AreaInstanceId ||
            !capture.Pair.Equals(new CurrencyPairKey(token.From, token.To)))
        {
            failure = "Final market capture did not match the token session, league, area, pair, or expiry.";
            return false;
        }

        DirectedExchangeEdge? edge;
        DirectedExchangeEdge? immediateAnchor;
        try
        {
            var edges = MarketCaptureNormalizer.CreateEdges(capture);
            edge = edges.SingleOrDefault(candidate =>
                IdentityMatches(candidate.From, token.From) && IdentityMatches(candidate.To, token.To) &&
                candidate.ExecutionIntent == token.ExecutionIntent);
            immediateAnchor = edges.SingleOrDefault(candidate =>
                IdentityMatches(candidate.From, token.From) && IdentityMatches(candidate.To, token.To) &&
                candidate.ExecutionIntent == QuoteExecutionIntent.Immediate);
        }
        catch (Exception exception) when (exception is OverflowException or InvalidOperationException)
        {
            failure = $"Final market normalization failed: {exception.Message}";
            return false;
        }

        if (edge is null || edge.Rate != token.Rate ||
            !IdentityMatches(edge.From, token.From) || !IdentityMatches(edge.To, token.To))
        {
            failure = edge is null
                ? $"Final capture had no {token.ExecutionIntent} {token.From.Metadata}->{token.To.Metadata} edge."
                : $"Final {token.ExecutionIntent} rate {edge.Rate} differed from prepared rate {token.Rate}.";
            return false;
        }

        if (token.ExecutionIntent == QuoteExecutionIntent.Immediate && edge.ImmediateInputDepth <= 0)
        {
            failure = "Final immediate edge had no positive readable depth at the prepared head rate.";
            return false;
        }

        // The same gate the planner applied, re-run on the final book: an order priced against a believable
        // head at plan time must still be priced against one at click time, because the queue behind it can
        // drain or be cancelled in between.
        if (!CompetingLiquidityGate.IsBelievable(
                edge, immediateAnchor, minCompetingQueue, maxCompetingSpread, out var gateReason))
        {
            failure = $"Final competing edge is no longer backed: {gateReason}.";
            return false;
        }

        try
        {
            var conversion = token.ExecutionIntent == QuoteExecutionIntent.Immediate
                ? edge.Rate.ConvertWholeLots(token.InputAvailable)
                : edge.Rate.ConvertWholeLots(token.InputAvailable, edge.InputLimit);
            if (conversion.InputSpent != token.InputSpent || conversion.Output != token.Output ||
                conversion.InputRemainder != token.InputRemainder)
            {
                failure = $"Final {token.ExecutionIntent} edge no longer produces the prepared input, output, and remainder.";
                return false;
            }
        }
        catch (OverflowException exception)
        {
            failure = $"Final sweep conversion overflowed: {exception.Message}";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool TryValidateCustody(
        SellSweepState sweep,
        SellSweepPlacementToken token,
        StashTabScan scan,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(scan);
        scan.TryGetHolding(token.CandidateMetadata, out var holding);
        return TryValidateCustodyEvidence(
            sweep, token, scan.Readable, scan.Visible, scan.TabType.ToString(), holding, out failure);
    }

    public static bool TryValidateCustodyEvidence(
        SellSweepState sweep,
        SellSweepPlacementToken token,
        bool readable,
        bool visible,
        string visibleTabType,
        StashTabHolding? holding,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(token);
        var candidate = sweep.Current;
        if (candidate is null || token.SweepId != sweep.SweepId || token.CandidateIndex != sweep.CurrentIndex ||
            !string.Equals(token.CandidateMetadata, candidate.Metadata, StringComparison.Ordinal) ||
            !readable || !visible ||
            !StashCustodyPolicy.TryResolveHomeTabType(candidate.Metadata, out var homeTabType) ||
            !string.Equals(visibleTabType, homeTabType, StringComparison.Ordinal) || holding is null ||
            holding.UnreadableSlots != 0 || holding.Amount != candidate.HoldingAtScan ||
            token.InputSpent <= 0 || token.InputSpent > holding.Amount)
        {
            failure = "Visible home-tab custody was unreadable or no longer matched the prepared holding exactly.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool IdentityMatches(CurrencyIdentity left, CurrencyIdentity right) =>
        string.Equals(left.Metadata, right.Metadata, StringComparison.Ordinal) && left.Hash == right.Hash;
}
