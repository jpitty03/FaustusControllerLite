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
            offeredMetadata == wantedMetadata ||
            !TryReadReserved(state, offeredMetadata, chaosMetadata, divineMetadata, out var reserved) ||
            reserved < originalOfferedAmount ||
            !CanUseCompletedBucket(state, offeredMetadata, remainingOfferedAmount, chaosMetadata, divineMetadata) ||
            !CanUseCompletedBucket(state, wantedMetadata, receivedWantedAmount, chaosMetadata, divineMetadata))
        {
            return false;
        }

        AddReserved(state, offeredMetadata, -originalOfferedAmount, chaosMetadata, divineMetadata);
        AddCompleted(state, offeredMetadata, remainingOfferedAmount, chaosMetadata, divineMetadata);
        AddCompleted(state, wantedMetadata, receivedWantedAmount, chaosMetadata, divineMetadata);
        return true;
    }

    public static bool TryCreditCollected(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount <= 0) return false;
        if (metadata == chaosMetadata)
        {
            if (state.CompletedUncollectedChaos < amount) return false;
            state.CompletedUncollectedChaos -= amount;
            state.AvailableChaos = checked(state.AvailableChaos + amount);
            return true;
        }
        if (metadata == divineMetadata)
        {
            if (state.CompletedUncollectedDivine < amount) return false;
            state.CompletedUncollectedDivine -= amount;
            state.AvailableDivine = checked(state.AvailableDivine + amount);
            return true;
        }
        if (state.TargetMetadata == metadata && state.CompletedUncollectedTarget >= amount)
        {
            state.CompletedUncollectedTarget -= amount;
            state.AvailableTarget = checked(state.AvailableTarget + amount);
            return true;
        }
        return false;
    }

    public static bool TryReserve(
        BankrollState state,
        string offeredMetadata,
        long amount,
        string chaosMetadata,
        string divineMetadata)
    {
        if (amount <= 0)
        {
            return false;
        }

        if (offeredMetadata == chaosMetadata)
        {
            if (state.AvailableChaos < amount) return false;
            state.AvailableChaos -= amount;
            state.ReservedChaos = checked(state.ReservedChaos + amount);
            return true;
        }

        if (offeredMetadata == divineMetadata)
        {
            if (state.AvailableDivine < amount) return false;
            state.AvailableDivine -= amount;
            state.ReservedDivine = checked(state.ReservedDivine + amount);
            return true;
        }

        return false;
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
        if (offeredAmount <= 0 || wantedAmount <= 0)
        {
            return false;
        }

        if (offeredMetadata == chaosMetadata)
        {
            if (state.ReservedChaos < offeredAmount) return false;
            state.ReservedChaos -= offeredAmount;
        }
        else if (offeredMetadata == divineMetadata)
        {
            if (state.ReservedDivine < offeredAmount) return false;
            state.ReservedDivine -= offeredAmount;
        }
        else
        {
            return false;
        }

        if (wantedMetadata == chaosMetadata)
        {
            state.CompletedUncollectedChaos = checked(state.CompletedUncollectedChaos + wantedAmount);
        }
        else if (wantedMetadata == divineMetadata)
        {
            state.CompletedUncollectedDivine = checked(state.CompletedUncollectedDivine + wantedAmount);
        }
        else
        {
            if (string.IsNullOrEmpty(state.TargetMetadata)) state.TargetMetadata = wantedMetadata;
            if (state.TargetMetadata != wantedMetadata) return false;
            state.CompletedUncollectedTarget = checked(state.CompletedUncollectedTarget + wantedAmount);
        }

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
        else if (metadata == state.TargetMetadata) amount = state.ReservedTarget;
        else { amount = 0; return false; }
        return true;
    }

    private static bool CanUseCompletedBucket(
        BankrollState state,
        string metadata,
        long amount,
        string chaosMetadata,
        string divineMetadata) =>
        amount == 0 || metadata == chaosMetadata || metadata == divineMetadata ||
        string.IsNullOrEmpty(state.TargetMetadata) || state.TargetMetadata == metadata;

    private static void AddReserved(
        BankrollState state,
        string metadata,
        long delta,
        string chaosMetadata,
        string divineMetadata)
    {
        if (metadata == chaosMetadata) state.ReservedChaos = checked(state.ReservedChaos + delta);
        else if (metadata == divineMetadata) state.ReservedDivine = checked(state.ReservedDivine + delta);
        else state.ReservedTarget = checked(state.ReservedTarget + delta);
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
        else
        {
            if (string.IsNullOrEmpty(state.TargetMetadata)) state.TargetMetadata = metadata;
            state.CompletedUncollectedTarget = checked(state.CompletedUncollectedTarget + amount);
        }
    }
}
