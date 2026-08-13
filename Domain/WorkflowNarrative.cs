using FaustusControllerLite.Orders;
using System.Globalization;
using System.Text;

namespace FaustusControllerLite.Domain;

/// <summary>
/// Overlay strings for the trade a workflow is executing and the deadline its order is running against.
/// Pure so the harness can exercise it: every caller lives inside the ExileCore plugin class, which the
/// tests cannot instantiate.
/// </summary>
public static class WorkflowNarrative
{
    public const string Separator = " > ";
    public const string EmptyPath = "no legs";

    /// <summary>
    /// The live path, hop by hop, with the leg currently in flight wrapped in brackets:
    /// <c>2 Divine Orb &gt; 1520 Primal Crysta~ &gt; [720 Chaos Orb] &gt; 4 Divine Orb</c>.
    /// </summary>
    /// <remarks>
    /// Amounts come off the leg plans rather than the original quote. A leg's <see cref="WorkflowLegPlan.Output"/>
    /// is rewritten as the workflow refreshes and settles, so this tracks what the bot now expects to receive,
    /// not what it expected when the route was chosen.
    /// </remarks>
    public static string DescribeActivePath(WorkflowExecutionState workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.Legs.Count == 0)
        {
            return EmptyPath;
        }

        // A finished or stopped workflow indexes one past the last leg. Nothing is in flight, so nothing
        // is bracketed, but the path itself still renders as the post-mortem of what ran.
        var current = workflow.CurrentLegIndex;
        var head = workflow.Legs[0].InputSpent > 0 ? workflow.Legs[0].InputSpent : workflow.StartingPrincipal;
        var builder = new StringBuilder();
        builder.Append(Hop(head, workflow.Legs[0].FromName));
        for (var index = 0; index < workflow.Legs.Count; index++)
        {
            var leg = workflow.Legs[index];
            builder.Append(Separator);
            builder.Append(index == current ? $"[{Hop(leg.Output, leg.ToName)}]" : Hop(leg.Output, leg.ToName));
        }

        return builder.ToString();
    }

    /// <summary>
    /// The same walk over a route the planner accepted but the workflow has not started, so no hop is in
    /// flight and nothing is bracketed.
    /// </summary>
    public static string DescribePlannedPath(RouteCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Legs.Count == 0 || candidate.Path.Count == 0)
        {
            return EmptyPath;
        }

        var builder = new StringBuilder();
        builder.Append(Hop(candidate.StartingPrincipal, candidate.Path[0].Name));
        foreach (var leg in candidate.Legs)
        {
            builder.Append(Separator);
            builder.Append(Hop(leg.Output, leg.Edge.To.Name));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Time left before <see cref="TrackedOrderLifecycle"/> will call the order timed out, as <c>m:ss</c>,
    /// or <c>expired</c> once the deadline has passed.
    /// </summary>
    /// <remarks>
    /// Only <see cref="TrackedOrderStatus.Pending"/> is counted down. Armed placements have no deadline yet -
    /// <c>WaitUntilUtc</c> is first written at the moment a placement matches a real order - and every terminal
    /// status carries the field forward as history, where a countdown would be describing the past.
    /// <para>
    /// "expired" is a real state rather than a rounding artifact: the flip to
    /// <see cref="TrackedOrderStatus.TimedOut"/> happens on the next lifecycle observation, which needs a
    /// readable exchange panel, so the deadline can pass while the status has not yet caught up.
    /// </para>
    /// </remarks>
    public static bool TryDescribeOrderTimeout(TrackedOrderState? tracked, DateTimeOffset now, out string text)
    {
        if (tracked is not { Status: TrackedOrderStatus.Pending } || tracked.WaitUntilUtc is not { } deadline)
        {
            text = string.Empty;
            return false;
        }

        var remaining = deadline - now;
        if (remaining <= TimeSpan.Zero)
        {
            text = "expired";
            return true;
        }

        // Ceiling so a countdown never shows 0:00 while time genuinely remains.
        var seconds = (long)Math.Ceiling(remaining.TotalSeconds);
        text = string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
        return true;
    }

    private static string Hop(long amount, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"{amount} {MarketSweepScore.Abbreviate(name ?? string.Empty)}");
}
