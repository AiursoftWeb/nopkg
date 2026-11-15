using System.Numerics;
namespace Aiursoft.Nopkg.Registry;

/// <summary>SemVer precedence: compare numeric identifiers numerically; stable follows prerelease.</summary>
public sealed class NpmVersionComparer : IComparer<string>
{
    public static NpmVersionComparer Instance { get; } = new();
    public int Compare(string? x, string? y)
    {
        if (x == y) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        var left = x.Split('+')[0].Split('-', 2);
        var right = y.Split('+')[0].Split('-', 2);
        var core = CompareParts(left[0].Split('.'), right[0].Split('.'));
        if (core != 0) return core;
        if (left.Length != right.Length) return left.Length == 1 ? 1 : -1;
        return left.Length == 1 ? 0 : CompareParts(left[1].Split('.'), right[1].Split('.'));
    }
    private static int CompareParts(string[] left, string[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var ln = BigInteger.TryParse(left[i], out var l);
            var rn = BigInteger.TryParse(right[i], out var r);
            var comparison = ln && rn ? l.CompareTo(r) : ln != rn ? ln ? -1 : 1 : StringComparer.Ordinal.Compare(left[i], right[i]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }
}
