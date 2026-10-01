using System.Text.RegularExpressions;

namespace GraniteBiDeployWizard.Core;

/// <summary>
/// Sorts file names the way a person expects (01_, 02_, ... 10_) instead of
/// plain ordinal sort, which would still work for the current two-digit
/// prefixes but breaks the moment a script set grows past 99.
/// </summary>
public sealed partial class NaturalFileNameComparer : IComparer<string?>
{
    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex TokenPattern();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);

        var xTokens = TokenPattern().Matches(x).Select(m => m.Value).ToList();
        var yTokens = TokenPattern().Matches(y).Select(m => m.Value).ToList();

        int count = Math.Min(xTokens.Count, yTokens.Count);
        for (int i = 0; i < count; i++)
        {
            string xt = xTokens[i], yt = yTokens[i];
            int cmp;

            if (int.TryParse(xt, out int xn) && int.TryParse(yt, out int yn))
                cmp = xn.CompareTo(yn);
            else
                cmp = string.Compare(xt, yt, StringComparison.OrdinalIgnoreCase);

            if (cmp != 0) return cmp;
        }

        return xTokens.Count.CompareTo(yTokens.Count);
    }
}
