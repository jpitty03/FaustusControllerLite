namespace FaustusControllerLite.Core;

/// <summary>
/// Every controller-level input gate answers the same ownership question: is this click driven by
/// a single hand-pressed action that holds no other permission, or by exactly one authorized
/// coordinator that holds every permission its multi-step run needs?
///
/// Two coordinator permissions enabled at once is deliberately not an answer. Arbitrage's full
/// workflow and the sell sweep drive the same controllers from different plans, so "either one
/// could own this" is ambiguity, not permission, and reads here as no owner at all.
/// </summary>
public readonly record struct CoordinatorOwnership(
    bool FullWorkflow,
    bool WorkflowAuthorized,
    bool SellSweep,
    bool SweepAuthorized)
{
    /// <summary>
    /// No coordinator permission is enabled, so the step must justify itself as a standalone
    /// manual action with every neighbouring permission switched off.
    /// </summary>
    public bool None => !FullWorkflow && !SellSweep;

    /// <summary>
    /// Exactly one coordinator permission is enabled and that coordinator has been authorized for
    /// the run in progress. Authorization is per-run state the plugin revokes on any refusal, so
    /// a stale toggle alone never satisfies this.
    /// </summary>
    public bool Authorized =>
        FullWorkflow && !SellSweep && WorkflowAuthorized ||
        SellSweep && !FullWorkflow && SweepAuthorized;
}
