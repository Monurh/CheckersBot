namespace Checkers.Core;

/// <summary>A legal move: a step, or a capture that may jump several times.</summary>
public sealed class Move
{
    public Move(int from, int[] path, int[] captured, bool promotes)
    {
        From = from;
        Path = path;
        Captured = captured;
        Promotes = promotes;
    }

    public int From { get; }

    /// <summary>Landing squares in order; for a step it is just the destination.</summary>
    public int[] Path { get; }

    /// <summary>Squares of the captured pieces, in jump order. Empty for a step.</summary>
    public int[] Captured { get; }

    public bool Promotes { get; }

    public int To => Path[^1];

    public bool IsCapture => Captured.Length > 0;

    /// <summary>All squares visited: from, then every landing square.</summary>
    public IEnumerable<int> Squares
    {
        get
        {
            yield return From;
            foreach (int s in Path) yield return s;
        }
    }

    /// <summary>Full PDN notation: 11-15, 22x15, 9x18x27.</summary>
    public string Notation => IsCapture ? From + "x" + string.Join("x", Path) : $"{From}-{To}";

    /// <summary>Short PDN notation: 11-15, 9x27.</summary>
    public string ShortNotation => IsCapture ? $"{From}x{To}" : $"{From}-{To}";

    public override string ToString() => Notation;
}

/// <summary>
/// English (American) checkers move generation: men move and capture forward only, kings one square in any
/// diagonal direction, captures are mandatory, a capture must continue while further jumps exist (any capture
/// sequence may be chosen), and a man that reaches the last row is crowned and its move ends.
/// </summary>
public static class MoveGenerator
{
    public static List<Move> Generate(Position pos)
    {
        var moves = new List<Move>(16);
        var board = pos.ToArray();
        Side side = pos.SideToMove;

        GenerateCaptures(board, side, moves);
        if (moves.Count > 0) return moves;

        for (int sq = 1; sq <= 32; sq++)
        {
            byte p = board[sq];
            if (!Piece.Is(p, side)) continue;
            bool king = Piece.IsKing(p);
            foreach (int d in king ? Geometry.KingDirs : Geometry.ManDirs(side))
            {
                int to = Geometry.Neighbor[sq, d];
                if (to == 0 || board[to] != Piece.Empty) continue;
                bool promotes = !king && Geometry.IsPromotionSquare(to, side);
                moves.Add(new Move(sq, new[] { to }, Array.Empty<int>(), promotes));
            }
        }
        return moves;
    }

    public static bool HasCapture(Position pos)
    {
        var board = pos.ToArray();
        Side side = pos.SideToMove;
        Side opp = side.Opponent();
        for (int sq = 1; sq <= 32; sq++)
        {
            byte p = board[sq];
            if (!Piece.Is(p, side)) continue;
            foreach (int d in Piece.IsKing(p) ? Geometry.KingDirs : Geometry.ManDirs(side))
            {
                int over = Geometry.Neighbor[sq, d], land = Geometry.Landing[sq, d];
                if (over != 0 && land != 0 && Piece.Is(board[over], opp) && board[land] == Piece.Empty) return true;
            }
        }
        return false;
    }

    static void GenerateCaptures(byte[] board, Side side, List<Move> moves)
    {
        var taken = new bool[33];
        var path = new List<int>(12);
        var caps = new List<int>(12);
        for (int sq = 1; sq <= 32; sq++)
        {
            byte p = board[sq];
            if (!Piece.Is(p, side)) continue;
            bool king = Piece.IsKing(p);
            board[sq] = Piece.Empty; // the moving piece leaves its square (a king may land on it again)
            Jump(board, side, sq, sq, king, taken, path, caps, moves);
            board[sq] = p;
        }
    }

    static void Jump(byte[] board, Side side, int origin, int at, bool king, bool[] taken,
        List<int> path, List<int> caps, List<Move> moves)
    {
        Side opp = side.Opponent();
        bool jumped = false;
        foreach (int d in king ? Geometry.KingDirs : Geometry.ManDirs(side))
        {
            int over = Geometry.Neighbor[at, d], land = Geometry.Landing[at, d];
            if (over == 0 || land == 0 || taken[over]) continue;
            if (!Piece.Is(board[over], opp) || board[land] != Piece.Empty) continue;

            jumped = true;
            taken[over] = true;
            path.Add(land);
            caps.Add(over);

            if (!king && Geometry.IsPromotionSquare(land, side))
                moves.Add(new Move(origin, path.ToArray(), caps.ToArray(), true)); // crowning ends the move
            else
                Jump(board, side, origin, land, king, taken, path, caps, moves);

            path.RemoveAt(path.Count - 1);
            caps.RemoveAt(caps.Count - 1);
            taken[over] = false;
        }

        if (!jumped && path.Count > 0)
            moves.Add(new Move(origin, path.ToArray(), caps.ToArray(), false));
    }

    /// <summary>Count leaf nodes of the legal move tree (standard perft).</summary>
    public static long Perft(Position pos, int depth)
    {
        var moves = Generate(pos);
        if (depth <= 1) return depth == 1 ? moves.Count : 1;
        long n = 0;
        foreach (var m in moves) n += Perft(pos.Apply(m), depth - 1);
        return n;
    }

    /// <summary>The legal move that turns <paramref name="before"/> into <paramref name="after"/>, if any.</summary>
    public static Move? FindMoveByResult(Position before, Position after)
    {
        foreach (var m in Generate(before))
            if (before.Apply(m).Equals(after)) return m;
        return null;
    }
}
