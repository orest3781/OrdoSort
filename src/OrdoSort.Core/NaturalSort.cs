namespace OrdoSort.Core;

/// <summary>
/// Culture-invariant "file2" &lt; "file10" ordering for anything the app
/// lists by name. A plain Ordinal or OrdinalIgnoreCase sort puts "file10"
/// before "file2" because it compares character-by-character, and Windows'
/// StrCmpLogicalW isn't an option here — Core has to build and test without
/// P/Invoke or a Windows-only dependency. This walks both strings run by
/// run instead: a run of ASCII digits is compared as a NUMBER (by
/// significant-digit count, then lexically if that ties — never by parsing
/// it into a numeric type, so a run with more digits than fits in a long
/// still compares correctly), anything else is compared one run at a time
/// with OrdinalIgnoreCase.
/// </summary>
public sealed class NaturalSort : IComparer<string>
{
    public static readonly NaturalSort Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;   // null sorts before non-null
        if (b is null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (IsAsciiDigit(a[i]) && IsAsciiDigit(b[j]))
            {
                var aStart = i;
                while (i < a.Length && IsAsciiDigit(a[i])) i++;
                var bStart = j;
                while (j < b.Length && IsAsciiDigit(b[j])) j++;

                var cmp = CompareDigitRun(a, aStart, i - aStart, b, bStart, j - bStart);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = string.Compare(a, i, b, j, 1, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    /// <summary>Numeric comparison of two digit runs WITHOUT ever parsing
    /// either one into a number — a run can be arbitrarily long (longer
    /// than long.MaxValue's 19 digits, which would overflow long.Parse
    /// outright). Leading zeros are trimmed first so "007" and "7" line up
    /// as equal-length, then the runs compare by significant-digit count
    /// and, only if that ties, lexically — which for equal-length all-digit
    /// runs is exactly the same ordering as comparing their numeric
    /// value.</summary>
    private static int CompareDigitRun(string a, int aStart, int aLen, string b, int bStart, int bLen)
    {
        while (aLen > 1 && a[aStart] == '0') { aStart++; aLen--; }
        while (bLen > 1 && b[bStart] == '0') { bStart++; bLen--; }

        if (aLen != bLen) return aLen.CompareTo(bLen);
        for (var k = 0; k < aLen; k++)
        {
            if (a[aStart + k] != b[bStart + k]) return a[aStart + k] - b[bStart + k];
        }
        return 0;
    }
}
