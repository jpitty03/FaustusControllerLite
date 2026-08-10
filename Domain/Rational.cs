using System.Numerics;

namespace FaustusControllerLite;

public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>
{
    public Rational(long numerator, long denominator)
    {
        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), "Numerator must be positive.");
        }

        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "Denominator must be positive.");
        }

        RawNumerator = numerator;
        RawDenominator = denominator;
        var divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public long RawNumerator { get; }

    public long RawDenominator { get; }

    public long Numerator { get; }

    public long Denominator { get; }

    public static long GreatestCommonDivisor(long left, long right)
    {
        if (left <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(left));
        }

        if (right <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(right));
        }

        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    public Rational Reverse() => new(RawDenominator, RawNumerator);

    public WholeLotConversion ConvertWholeLots(long availableInput, long inputLimit = long.MaxValue)
    {
        if (availableInput < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableInput));
        }

        if (inputLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputLimit));
        }

        var usable = Math.Min(availableInput, inputLimit);
        var lots = usable / Denominator;
        var spent = CheckedLong((BigInteger)lots * Denominator);
        var output = CheckedLong((BigInteger)lots * Numerator);
        return new WholeLotConversion(lots, spent, output, availableInput - spent);
    }

    public bool TryGetInputForExactOutput(long output, out long input)
    {
        if (output < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(output));
        }

        if (output % Numerator != 0)
        {
            input = 0;
            return false;
        }

        input = CheckedLong((BigInteger)(output / Numerator) * Denominator);
        return true;
    }

    public long FloorMultiply(long input)
    {
        if (input < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input));
        }

        return CheckedLong((BigInteger)input * Numerator / Denominator);
    }

    public int CompareTo(Rational other) =>
        ((BigInteger)Numerator * other.Denominator).CompareTo((BigInteger)other.Numerator * Denominator);

    public bool Equals(Rational other) => Numerator == other.Numerator && Denominator == other.Denominator;

    public override bool Equals(object? obj) => obj is Rational other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    public override string ToString() => $"{Numerator}/{Denominator}";

    public static bool operator ==(Rational left, Rational right) => left.Equals(right);

    public static bool operator !=(Rational left, Rational right) => !left.Equals(right);

    public static bool operator <(Rational left, Rational right) => left.CompareTo(right) < 0;

    public static bool operator >(Rational left, Rational right) => left.CompareTo(right) > 0;

    public static bool operator <=(Rational left, Rational right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Rational left, Rational right) => left.CompareTo(right) >= 0;

    private static long CheckedLong(BigInteger value)
    {
        if (value > long.MaxValue || value < long.MinValue)
        {
            throw new OverflowException("Exact rational result does not fit in Int64.");
        }

        return (long)value;
    }
}

public readonly record struct WholeLotConversion(long Lots, long InputSpent, long Output, long InputRemainder);
