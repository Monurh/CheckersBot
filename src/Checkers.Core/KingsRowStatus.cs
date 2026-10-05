using System.Globalization;
using System.Text.RegularExpressions;

namespace Checkers.Core;

/// <summary>
/// Parsed KingsRow search status line, for example:
/// "value=302,  depth 26/21.5/38,  1.1s,  11597 kN/s,  pv 14x23 27x18 16x23 25-21".
/// The value is in KingsRow's units and, as observed, from Black's point of view.
/// When the move came from the opening book the line lists book moves instead, e.g. "11-15(1/25)*  12-16(-1/37)*".
/// </summary>
public sealed record KingsRowStatus(
    int? Value,
    char Bound,
    int? Depth,
    double? Seconds,
    long? KiloNodesPerSecond,
    IReadOnlyList<string> Pv,
    bool IsBook,
    bool NoMoves)
{
    static readonly Regex ValueRx = new(@"value\s*([<>=])\s*(-?\d+)", RegexOptions.Compiled);
    static readonly Regex DepthRx = new(@"depth\s+(\d+)", RegexOptions.Compiled);
    static readonly Regex TimeRx = new(@"(?:^|,)\s*(\d+(?:\.\d+)?)s\s*(?:,|$)", RegexOptions.Compiled);
    static readonly Regex SpeedRx = new(@"(\d+)\s*kN/s", RegexOptions.Compiled);
    static readonly Regex PvRx = new(@"\bpv\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex MoveRx = new(@"^\d{1,2}(?:[-x]\d{1,2})+$", RegexOptions.Compiled);
    static readonly Regex BookRx = new(@"^\s*(\d{1,2}[-x]\d{1,2})\(-?\d+/\d+\)\*", RegexOptions.Compiled);
    static readonly Regex DbPiecesRx = new(@"(\d+)\s*pieces", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static KingsRowStatus Parse(string? line)
    {
        line ??= "";
        if (line.Trim().Equals("No moves", StringComparison.OrdinalIgnoreCase))
            return new(null, '=', null, null, null, Array.Empty<string>(), false, true);

        var book = BookRx.Match(line);
        if (book.Success)
            return new(null, '=', null, null, null, new[] { book.Groups[1].Value }, true, false);

        int? value = null;
        char bound = '=';
        var v = ValueRx.Match(line);
        if (v.Success)
        {
            bound = v.Groups[1].Value[0];
            value = int.Parse(v.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        int? depth = null;
        var d = DepthRx.Match(line);
        if (d.Success) depth = int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture);

        double? seconds = null;
        var t = TimeRx.Match(line);
        if (t.Success) seconds = double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);

        long? knps = null;
        var s = SpeedRx.Match(line);
        if (s.Success) knps = long.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);

        var pv = new List<string>();
        var p = PvRx.Match(line);
        if (p.Success)
        {
            foreach (string tok in p.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!MoveRx.IsMatch(tok)) break;
                pv.Add(tok);
            }
        }
        return new(value, bound, depth, seconds, knps, pv, false, false);
    }

    /// <summary>Largest endgame database size mentioned in a line such as "Kingsrow WLD database found for 8 pieces".</summary>
    public static int? ParseDbPieces(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int? best = null;
        foreach (Match m in DbPiecesRx.Matches(line))
        {
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (n >= 2 && n <= 24 && (best == null || n > best)) best = n;
        }
        return best;
    }
}
