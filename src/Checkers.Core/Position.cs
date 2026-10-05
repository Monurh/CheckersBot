namespace Checkers.Core;

/// <summary>Side to move. Values match the CheckerBoard engine API (WHITE = 1, BLACK = 2).</summary>
public enum Side : byte
{
    White = 1,
    Black = 2,
}

/// <summary>Piece codes, the same bit flags as the CheckerBoard engine API.</summary>
public static class Piece
{
    public const byte Empty = 0;
    public const byte White = 1;
    public const byte Black = 2;
    public const byte Man = 4;
    public const byte King = 8;

    public const byte WhiteMan = White | Man;
    public const byte WhiteKing = White | King;
    public const byte BlackMan = Black | Man;
    public const byte BlackKing = Black | King;

    public static bool Is(byte piece, Side side) => (piece & (byte)side) != 0;
    public static bool IsKing(byte piece) => (piece & King) != 0;
    public static bool IsMan(byte piece) => (piece & Man) != 0;
}

public static class SideExtensions
{
    public static Side Opponent(this Side side) => side == Side.White ? Side.Black : Side.White;
    public static char Letter(this Side side) => side == Side.White ? 'W' : 'B';
}

/// <summary>
/// Board geometry for English checkers in standard notation: Black starts on 1-12 at the top,
/// White on 21-32 at the bottom. Row 0 holds squares 1-4, row 7 holds 29-32.
/// </summary>
public static class Geometry
{
    /// <summary>Directions: 0 = up-left, 1 = up-right (towards row 0), 2 = down-left, 3 = down-right (towards row 7).</summary>
    public static readonly int[] DirRow = { -1, -1, 1, 1 };
    public static readonly int[] DirCol = { -1, 1, -1, 1 };

    /// <summary>Neighbor[square, dir]: adjacent square in that direction, or 0.</summary>
    public static readonly int[,] Neighbor = new int[33, 4];

    /// <summary>Landing[square, dir]: square two steps away in that direction (jump landing), or 0.</summary>
    public static readonly int[,] Landing = new int[33, 4];

    public static readonly int[] WhiteManDirs = { 0, 1 };
    public static readonly int[] BlackManDirs = { 2, 3 };
    public static readonly int[] KingDirs = { 0, 1, 2, 3 };

    static Geometry()
    {
        for (int sq = 1; sq <= 32; sq++)
        {
            var (r, c) = RowCol(sq);
            for (int d = 0; d < 4; d++)
            {
                Neighbor[sq, d] = SquareAt(r + DirRow[d], c + DirCol[d]);
                Landing[sq, d] = SquareAt(r + 2 * DirRow[d], c + 2 * DirCol[d]);
            }
        }
    }

    public static (int row, int col) RowCol(int square)
    {
        int r = (square - 1) / 4;
        int i = (square - 1) % 4;
        return (r, r % 2 == 0 ? 2 * i + 1 : 2 * i);
    }

    /// <summary>Square number at a row/column, or 0 for a light square or off the board.</summary>
    public static int SquareAt(int row, int col)
    {
        if (row < 0 || row > 7 || col < 0 || col > 7 || (row + col) % 2 == 0) return 0;
        return row * 4 + col / 2 + 1;
    }

    public static bool IsPromotionSquare(int square, Side side) =>
        side == Side.White ? square <= 4 : square >= 29;

    public static int[] ManDirs(Side side) => side == Side.White ? WhiteManDirs : BlackManDirs;
}

/// <summary>An immutable English checkers position: 32 squares plus the side to move.</summary>
public sealed class Position : IEquatable<Position>
{
    readonly byte[] _squares; // index 1..32, index 0 unused

    public Side SideToMove { get; }

    public Position(ReadOnlySpan<byte> squares33, Side sideToMove)
    {
        if (squares33.Length != 33) throw new ArgumentException("Expected 33 entries (index 0 unused).");
        _squares = squares33.ToArray();
        _squares[0] = 0;
        SideToMove = sideToMove;
    }

    Position(byte[] owned, Side sideToMove, bool _)
    {
        _squares = owned;
        SideToMove = sideToMove;
    }

    public static Position Start { get; } = CreateStart();

    static Position CreateStart()
    {
        var b = new byte[33];
        for (int sq = 1; sq <= 12; sq++) b[sq] = Piece.BlackMan;
        for (int sq = 21; sq <= 32; sq++) b[sq] = Piece.WhiteMan;
        return new Position(b, Side.Black, true);
    }

    public byte this[int square] => _squares[square];

    /// <summary>A copy of the 33-entry square array.</summary>
    public byte[] ToArray() => (byte[])_squares.Clone();

    public int Count(Side side)
    {
        int n = 0;
        for (int sq = 1; sq <= 32; sq++) if (Piece.Is(_squares[sq], side)) n++;
        return n;
    }

    public int CountKings(Side side)
    {
        int n = 0;
        for (int sq = 1; sq <= 32; sq++) if (Piece.Is(_squares[sq], side) && Piece.IsKing(_squares[sq])) n++;
        return n;
    }

    public int PieceCount => Count(Side.White) + Count(Side.Black);

    /// <summary>Play a move (assumed legal) and return the new position with the other side to move.</summary>
    public Position Apply(Move move)
    {
        var b = (byte[])_squares.Clone();
        byte piece = b[move.From];
        b[move.From] = Piece.Empty;
        foreach (int c in move.Captured) b[c] = Piece.Empty;
        if (move.Promotes) piece = (byte)((piece & (Piece.White | Piece.Black)) | Piece.King);
        b[move.To] = piece;
        return new Position(b, SideToMove.Opponent(), true);
    }

    public Position WithSideToMove(Side side) => side == SideToMove ? this : new Position(_squares, side, true);

    public string ToFen() => Pdn.ToFen(this);

    public override string ToString() => ToFen();

    public bool Equals(Position? other) =>
        other is not null && other.SideToMove == SideToMove && _squares.AsSpan().SequenceEqual(other._squares);

    public override bool Equals(object? obj) => Equals(obj as Position);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(SideToMove);
        h.AddBytes(_squares);
        return h.ToHashCode();
    }

    /// <summary>Text diagram, Black at the top: b/B black man/king, w/W white man/king, . empty.</summary>
    public string ToAscii()
    {
        var sb = new System.Text.StringBuilder();
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                int sq = Geometry.SquareAt(row, col);
                if (sq == 0) { sb.Append("  "); continue; }
                char ch = _squares[sq] switch
                {
                    Piece.BlackMan => 'b',
                    Piece.BlackKing => 'B',
                    Piece.WhiteMan => 'w',
                    Piece.WhiteKing => 'W',
                    _ => '.',
                };
                sb.Append(' ').Append(ch);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
