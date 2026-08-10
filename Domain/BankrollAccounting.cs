namespace FaustusControllerLite.Domain;

public static class BankrollAccounting
{
    public static bool TrySettleTerminal(
        BankrollState state,
        string offeredMetadata,
        long originalOfferedAmount,
        long remainingOfferedAmount,
        string wantedMetadata,
        long receivedWantedAmount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (originalOfferedAmount <= 0 || remainingOfferedAmount < 0 ||
            remainingOfferedAmount > originalOfferedAmount || receivedWantedAmount < 0 ||
            !MetadataPairIsValid(offeredMetadata, wantedMetadata) ||
            !TryReadReserved(state, offeredMetadata, chaosMetadata, divineMetadata, out var reserved) ||
            reserved != originalOfferedAmount ||
            !CanUseCompletedBucket(offeredMetadata) || !CanUseCompletedBucket(wantedMetadata))
        {
            return false;
        }

        var shadow = Copy(state);
        AddReserved(shadow, offeredMetadata, -originalOfferedAmount, chaosMetadata, divineMetadata);
        AddCompleted(shadow, offeredMetadata, remainingOfferedAmount, chaosMetadata, divineMetadata);
        AddCompleted(shadow, wantedMetadata, receivedWantedAmount, chaosMetadata, divineMetadata);
        CopyAccounting(shadow, state);
        return true;
    }

    public static bool TryCreditCollected(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(metadata) ||
            !TryReadCompleted(state, metadata, chaosMetadata, divineMetadata, out var completed) ||
            completed < amount)
        {
            return false;
        }

        var shadow = Copy(state);
        AddCompleted(shadow, metadata, -amount, chaosMetadata, divineMetadata);
        AddAvailable(shadow, metadata, amount, chaosMetadata, divineMetadata);
        CopyAccounting(shadow, state);
        return true;
    }

    public static bool TryReserve(
        BankrollState state,
        string offeredMetadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(offeredMetadata))
        {
            return false;
        }

        if (!TryReadAvailable(state, offeredMetadata, chaosMetadata, divineMetadata, out var available) ||
            available < amount)
        {
            return false;
        }

        var shadow = Copy(state);
        AddAvailable(shadow, offeredMetadata, -amount, chaosMetadata, divineMetadata);
        AddReserved(shadow, offeredMetadata, amount, chaosMetadata, divineMetadata);
        CopyAccounting(shadow, state);
        return true;
    }

    /// <summary>
    /// Credits stash currency the sweep has just counted with its own eyes, so that the normal
    /// arming path can reserve it. Without this a swept stack can never be reserved: the bankroll
    /// seeds chaos and divine only, and <see cref="TryReserve"/> refuses metadata it has never
    /// seen. The credit is a statement that the stack exists, so the caller must supply a
    /// same-frame scan, and only the amount it is about to offer.
    /// </summary>
    /// <remarks>
    /// Core currency is refused deliberately. Chaos and divine are seeded and reconciled by the
    /// operator, and letting a stash read top them up would silently inflate the bankroll the
    /// arbitrage feature spends.
    /// </remarks>
    public static bool TryCreditSweptCustody(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount <= 0 ||
            string.IsNullOrWhiteSpace(metadata) ||
            metadata == chaosMetadata ||
            metadata == divineMetadata)
        {
            return false;
        }

        var shadow = Copy(state);
        AddAvailable(shadow, metadata, amount, chaosMetadata, divineMetadata);
        CopyAccounting(shadow, state);
        return true;
    }

    /// <summary>
    /// Reverses a credit that was never reserved, so a refused arm cannot leave phantom available
    /// balance behind. Refuses unless the full credited amount is still sitting in available -
    /// if it has already moved, the reversal is not ours to make.
    /// </summary>
    public static bool TryReverseSweptCustody(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount <= 0 ||
            string.IsNullOrWhiteSpace(metadata) ||
            metadata == chaosMetadata ||
            metadata == divineMetadata ||
            !TryReadAvailable(state, metadata, chaosMetadata, divineMetadata, out var available) ||
            available < amount)
        {
            return false;
        }

        var shadow = Copy(state);
        AddAvailable(shadow, metadata, -amount, chaosMetadata, divineMetadata);
        CopyAccounting(shadow, state);
        return true;
    }

    public static bool TryCompleteUncollected(
        BankrollState state,
        string offeredMetadata,
        long offeredAmount,
        string wantedMetadata,
        long wantedAmount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (offeredAmount <= 0 || wantedAmount <= 0 ||
            !MetadataPairIsValid(offeredMetadata, wantedMetadata) ||
            !TryReadReserved(state, offeredMetadata, chaosMetadata, divineMetadata, out var reserved) ||
            reserved != offeredAmount)
        {
            return false;
        }

        var shadow = Copy(state);
        AddReserved(shadow, offeredMetadata, -offeredAmount, chaosMetadata, divineMetadata);
        AddCompleted(shadow, wantedMetadata, wantedAmount, chaosMetadata, divineMetadata);

        CopyAccounting(shadow, state);
        return true;
    }

    private static bool TryReadReserved(
        BankrollState state,
        string metadata,
        string chaosMetadata,
        string divineMetadata,
        out long amount)
    {
        if (metadata == chaosMetadata) amount = state.ReservedChaos;
        else if (metadata == divineMetadata) amount = state.ReservedDivine;
        else if (state.NonCoreBalances.TryGetValue(metadata, out var balance)) amount = balance.Reserved;
        else { amount = 0; return false; }
        return true;
    }

    private static bool TryReadAvailable(
        BankrollState state,
        string metadata,
        string chaosMetadata,
        string divineMetadata,
        out long amount)
    {
        if (metadata == chaosMetadata) amount = state.AvailableChaos;
        else if (metadata == divineMetadata) amount = state.AvailableDivine;
        else if (state.NonCoreBalances.TryGetValue(metadata, out var balance)) amount = balance.Available;
        else { amount = 0; return false; }
        return true;
    }

    private static bool TryReadCompleted(
        BankrollState state,
        string metadata,
        string chaosMetadata,
        string divineMetadata,
        out long amount)
    {
        if (metadata == chaosMetadata) amount = state.CompletedUncollectedChaos;
        else if (metadata == divineMetadata) amount = state.CompletedUncollectedDivine;
        else if (state.NonCoreBalances.TryGetValue(metadata, out var balance)) amount = balance.CompletedUncollected;
        else { amount = 0; return false; }
        return true;
    }

    private static bool CanUseCompletedBucket(string metadata) => !string.IsNullOrWhiteSpace(metadata);

    private static bool MetadataPairIsValid(string offeredMetadata, string wantedMetadata) =>
        !string.IsNullOrWhiteSpace(offeredMetadata) && !string.IsNullOrWhiteSpace(wantedMetadata) &&
        !string.Equals(offeredMetadata, wantedMetadata, StringComparison.Ordinal);

    private static void AddReserved(
        BankrollState state,
        string metadata,
        long delta,
        string chaosMetadata,
        string divineMetadata)
    {
        if (metadata == chaosMetadata) state.ReservedChaos = checked(state.ReservedChaos + delta);
        else if (metadata == divineMetadata) state.ReservedDivine = checked(state.ReservedDivine + delta);
        else GetOrCreate(state, metadata).Reserved = checked(GetOrCreate(state, metadata).Reserved + delta);
    }

    private static void AddCompleted(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount == 0) return;
        if (metadata == chaosMetadata) state.CompletedUncollectedChaos = checked(state.CompletedUncollectedChaos + amount);
        else if (metadata == divineMetadata) state.CompletedUncollectedDivine = checked(state.CompletedUncollectedDivine + amount);
        else GetOrCreate(state, metadata).CompletedUncollected =
            checked(GetOrCreate(state, metadata).CompletedUncollected + amount);
    }

    private static void AddAvailable(
        BankrollState state,
        string metadata,
        long delta,
        string chaosMetadata,
        string divineMetadata)
    {
        if (metadata == chaosMetadata) state.AvailableChaos = checked(state.AvailableChaos + delta);
        else if (metadata == divineMetadata) state.AvailableDivine = checked(state.AvailableDivine + delta);
        else GetOrCreate(state, metadata).Available = checked(GetOrCreate(state, metadata).Available + delta);
    }

    private static NonCoreBalanceState GetOrCreate(BankrollState state, string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) throw new InvalidDataException("Non-core metadata is required.");
        if (!state.NonCoreBalances.TryGetValue(metadata, out var balance))
        {
            balance = new NonCoreBalanceState();
            state.NonCoreBalances.Add(metadata, balance);
        }
        return balance;
    }

    private static BankrollState Copy(BankrollState state) => new()
    {
        AvailableChaos = state.AvailableChaos,
        AvailableDivine = state.AvailableDivine,
        ReservedChaos = state.ReservedChaos,
        ReservedDivine = state.ReservedDivine,
        CompletedUncollectedChaos = state.CompletedUncollectedChaos,
        CompletedUncollectedDivine = state.CompletedUncollectedDivine,
        NonCoreBalances = state.CloneNonCoreBalances(),
    };

    private static void CopyAccounting(BankrollState source, BankrollState destination)
    {
        destination.AvailableChaos = source.AvailableChaos;
        destination.AvailableDivine = source.AvailableDivine;
        destination.ReservedChaos = source.ReservedChaos;
        destination.ReservedDivine = source.ReservedDivine;
        destination.CompletedUncollectedChaos = source.CompletedUncollectedChaos;
        destination.CompletedUncollectedDivine = source.CompletedUncollectedDivine;
        destination.NonCoreBalances = source.CloneNonCoreBalances();
    }
}
