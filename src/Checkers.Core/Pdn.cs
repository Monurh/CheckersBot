using System.Text;

namespace Checkers.Core;

/// <summary>Thrown for a position or move string that cannot be parsed or is not acceptable.</summary>
public sealed class PdnException : FormatException
{
    public PdnException(string message) : base(message) { }
}

/// <summary>PDN FEN for English checkers, e.g. "B:W18,19,K22:B1-12".</summary>
public static class Pdn
{
    /// <summary>
    /// Parse a PDN FEN. Accepts a [FEN "..."] tag, spaces, a trailing period, lower case,
    /// K for kings and square ranges such as 1-12. Does not check that the position is sensible: see <see cref="Validate"/>.
    /// </summary>
    public static Position ParseFen(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new PdnException("Position is empty.");
        string s = text.Trim();

        if (s.StartsWith('['))
        {
            int q1 = s.IndexOf('"'), q2 = s.LastIndexOf('"');
            if (!s.StartsWith("[FEN", StringComparison.OrdinalIgnoreCase) || q1 < 0 || q2 <= q1)
                throw new PdnException("Expected a [FEN \"...\"] tag.");
            s = s.Substring(q1 + 1, q2 - q1 - 1);
        }

        s = s.Replace(" ", "").Replace("\t", "").TrimEnd('.');
        if (s.Length == 0) throw new PdnException("Position is empty.");

        string[] parts = s.Split(':');
        Side stm = char.ToUpperInvariant(parts[0].Length == 1 ? parts[0][0] : '?') switch
        {
            'W' => Side.White,
            'B' => Side.Black,
            _ => throw new PdnException($"Side to move must be 'B' or 'W', got '{parts[0]}'."),
        };
        if (parts.Length > 3) throw new PdnException("Too many ':' separators; expected B:W...:B... .");

        var board = new byte[33];
        bool seenWhite = false, seenBlack = false;
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0) throw new PdnException("Empty piece list.");
            char c = char.ToUpperInvariant(part[0]);
            Side color;
            if (c == 'W') { if (seenWhite) throw new PdnException("White pieces listed twice."); seenWhite = true; color = Side.White; }
            else if (c == 'B') { if (seenBlack) throw new PdnException("Black pieces listed twice."); seenBlack = true; color = Side.Black; }
            else throw new PdnException($"Piece list must start with 'W' or 'B', got '{part}'.");

            string list = part.Substring(1);
            if (list.Length == 0) continue;
            foreach (string raw in list.Split(','))
            {
                if (raw.Length == 0) throw new PdnException("Empty entry in piece list.");
                string item = raw;
                bool king = false;
                if (char.ToUpperInvariant(item[0]) == 'K') { king = true; item = item.Substring(1); }

                int from, to;
                int dash = item.IndexOf('-');
                if (dash >= 0)
                {
                    from = ParseSquare(item.Substring(0, dash), raw);
                    to = ParseSquare(item.Substring(dash + 1), raw);
                    if (to < from) throw new PdnException($"Bad range '{raw}'.");
                }
                else
                {
                    from = to = ParseSquare(item, raw);
                }

                for (int sq = from; sq <= to; sq++)
                {
                    if (board[sq] != Piece.Empty) throw new PdnException($"Square {sq} is occupied twice.");
                    board[sq] = (byte)((byte)color | (king ? Piece.King : Piece.Man));
                }
            }
        }
        return new Position(board, stm);
    }

    static int ParseSquare(string s, string raw)
    {
        if (s.Length == 0 || s.Length > 2 || !int.TryParse(s, out int sq) || sq < 1 || sq > 32)
            throw new PdnException($"Bad square '{raw}': squares are numbered 1 to 32.");
        return sq;
    }

    /// <summary>Canonical form: side to move, white pieces, black pieces, ascending, kings with K. Example: B:W21,K30:B1,2.</summary>
    public static string ToFen(Position pos)
    {
        var sb = new StringBuilder(80);
        sb.Append(pos.SideToMove.Letter());
        foreach (var side in new[] { Side.White, Side.Black })
        {
            sb.Append(':').Append(side.Letter());
            bool first = true;
            for (int sq = 1; sq <= 32; sq++)
            {
                byte p = pos[sq];
                if (!Piece.Is(p, side)) continue;
                if (!first) sb.Append(',');
                if (Piece.IsKing(p)) sb.Append('K');
                sb.Append(sq);
                first = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Problems that make a position unusable for play: too many pieces, a man on its crowning row,
    /// or a side with no pieces at all. Returns an empty list for an acceptable position.
    /// </summary>
    public static List<string> Validate(Position pos)
    {
        var errors = new List<string>();
        foreach (var side in new[] { Side.White, Side.Black })
        {
            int n = pos.Count(side);
            string name = side == Side.White ? "White" : "Black";
            if (n > 12) errors.Add($"{name} has {n} pieces; the maximum is 12.");
            if (n == 0) errors.Add($"{name} has no pieces.");
        }
        for (int sq = 1; sq <= 4; sq++)
            if (pos[sq] == Piece.WhiteMan) errors.Add($"White man on square {sq} should be a king (crowning row).");
        for (int sq = 29; sq <= 32; sq++)
            if (pos[sq] == Piece.BlackMan) errors.Add($"Black man on square {sq} should be a king (crowning row).");
        return errors;
    }

    /// <summary>Parse and validate; throws <see cref="PdnException"/> with all problems found.</summary>
    public static Position ParseAndValidate(string? text)
    {
        var pos = ParseFen(text);
        var errors = Validate(pos);
        if (errors.Count > 0) throw new PdnException(string.Join(" ", errors));
        return pos;
    }
}

/// <summary>Turns move text such as "11-15", "22x15", "9x18x27" or "9x27" into a legal move.</summary>
public static class MoveNotation
{
    public enum Outcome { Legal, Illegal, Ambiguous, Malformed }

    public sealed record Result(Outcome Outcome, Move? Move, string? Message)
    {
        public bool IsLegal => Outcome == Outcome.Legal;
    }

    /// <summary>
    /// Resolve a move string against the legal moves of a position. The listed squares must start at the
    /// moving piece, end on its final square, and appear in order along the move's path (so both full and
    /// short capture notation work). With <paramref name="firstIfAmbiguous"/>, ambiguous short notation
    /// picks the first matching move instead of failing.
    /// </summary>
    public static Result Resolve(Position pos, string? text, bool firstIfAmbiguous = false) =>
        Resolve(pos, MoveGenerator.Generate(pos), text, firstIfAmbiguous);

    public static Result Resolve(Position pos, IReadOnlyList<Move> legal, string? text, bool firstIfAmbiguous = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(Outcome.Malformed, null, "Move is empty.");

        var squares = new List<int>();
        var cur = new StringBuilder();
        foreach (char ch in text.Trim())
        {
            if (char.IsDigit(ch)) { cur.Append(ch); continue; }
            if (ch is '-' or 'x' or 'X' or ':' or ' ')
            {
                if (cur.Length > 0) { squares.Add(int.Parse(cur.ToString())); cur.Clear(); }
                continue;
            }
            return new(Outcome.Malformed, null, $"Unexpected character '{ch}' in move '{text}'.");
        }
        if (cur.Length > 0) squares.Add(int.Parse(cur.ToString()));
        if (squares.Count < 2) return new(Outcome.Malformed, null, $"Move '{text}' needs at least two squares.");
        if (squares.Any(s => s < 1 || s > 32)) return new(Outcome.Malformed, null, $"Move '{text}' has a square outside 1..32.");

        var candidates = legal.Where(m => Matches(m, squares)).ToList();
        if (candidates.Count == 0) return new(Outcome.Illegal, null, $"{text} is not a legal move in this position.");
        if (candidates.Count == 1) return new(Outcome.Legal, candidates[0], null);

        // Several paths: fine if they all lead to the same position (e.g. a king loop), or if the caller accepts the first.
        var first = pos.Apply(candidates[0]);
        if (firstIfAmbiguous || candidates.Skip(1).All(m => pos.Apply(m).Equals(first)))
            return new(Outcome.Legal, candidates[0], null);
        return new(Outcome.Ambiguous, null,
            $"{text} is ambiguous; write the full path: {string.Join(" or ", candidates.Select(m => m.Notation))}.");
    }

    static bool Matches(Move m, List<int> squares)
    {
        if (squares[0] != m.From || squares[^1] != m.To) return false;
        int i = 0;
        foreach (int s in m.Squares)
            if (i < squares.Count && s == squares[i]) i++;
        return i == squares.Count;
    }
}
