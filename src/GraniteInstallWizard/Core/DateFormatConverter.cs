using System.Text;

namespace GraniteInstallWizard.Core;

/// <summary>
/// Converts Web Desktop's date format to the Business API's.
/// </summary>
/// <remarks>
/// The release's GraniteBusinessAPI\appsettings.json says so directly:
/// "DateTimeFormat: this needs to be the same as in the WebDesktop
/// AppConfig.json ... the WebDesktop requires capital DD/MM/YYYY and the
/// below format as follows dd'/'MM'/'yyyy". Web Desktop formats with
/// moment.js, the API with .NET, and the two spell the same format
/// differently. The installer picks one format and writes both spellings,
/// so they can't drift apart.
/// </remarks>
public static class DateFormatConverter
{
    /// <summary>Formats offered on Step 1.</summary>
    public static IReadOnlyList<string> Choices { get; } = new[] { "DD/MM/YYYY", "MM/DD/YYYY", "YYYY/MM/DD", "YYYY-MM-DD" };

    /// <summary>DD/MM/YYYY becomes dd'/'MM'/'yyyy (separators quoted as literals, as the release's own sample does).</summary>
    public static string ToDotNet(string webDesktopFormat)
    {
        var sb = new StringBuilder();
        int i = 0;
        string f = webDesktopFormat;
        while (i < f.Length)
        {
            if (f.AsSpan(i).StartsWith("YYYY")) { sb.Append("yyyy"); i += 4; continue; }
            if (f.AsSpan(i).StartsWith("DD")) { sb.Append("dd"); i += 2; continue; }
            if (f.AsSpan(i).StartsWith("MM")) { sb.Append("MM"); i += 2; continue; }
            char c = f[i];
            if ("/-. ".Contains(c)) sb.Append('\'').Append(c).Append('\'');
            else sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
