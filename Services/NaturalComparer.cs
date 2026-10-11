using System.Collections.Generic;

namespace MediaPlayer.Services;

public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var i = 0;
        var j = 0;

        while (i < left.Length && j < right.Length)
        {
            if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
            {
                var startI = i;
                var startJ = j;

                while (i < left.Length && char.IsDigit(left[i]))
                {
                    i++;
                }

                while (j < right.Length && char.IsDigit(right[j]))
                {
                    j++;
                }

                var digitsLeft = left.AsSpan(startI, i - startI).TrimStart('0');
                var digitsRight = right.AsSpan(startJ, j - startJ).TrimStart('0');

                if (digitsLeft.Length != digitsRight.Length)
                {
                    return digitsLeft.Length - digitsRight.Length;
                }

                var digitComparison = digitsLeft.SequenceCompareTo(digitsRight);
                if (digitComparison != 0)
                {
                    return digitComparison;
                }

                continue;
            }

            var symbolLeft = char.ToUpperInvariant(left[i]);
            var symbolRight = char.ToUpperInvariant(right[j]);

            if (symbolLeft != symbolRight)
            {
                return symbolLeft.CompareTo(symbolRight);
            }

            i++;
            j++;
        }

        return (left.Length - i).CompareTo(right.Length - j);
    }
}
