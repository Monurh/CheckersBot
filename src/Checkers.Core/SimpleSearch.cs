using System.Diagnostics;

namespace Checkers.Core;

/// <summary>Result of <see cref="SimpleSearch"/>.</summary>
public sealed record SimpleSearchResult(Move? BestMove, IReadOnlyList<Move> Pv, int Score, int Depth, long Nodes, bool Stopped);

/// <summary>
/// A small built-in engine (iterative-deepening alpha-beta, material and advancement evaluation, capture
/// extension). It is the "stub" engine: it lets the service run and be tested without KingsRow. It is far
/// weaker than KingsRow and has no endgame databases.
/// </summary>
public sealed class SimpleSearch
{
    public const int WinScore = 10000;
    const int ManValue = 100, KingValue = 150;

    long _nodes;
    long _deadline;
    CancellationToken _ct;
    bool _stopped;
    readonly Stopwatch _clock = new();

    public SimpleSearchResult Search(Position root, int maxDepth, int timeMs, CancellationToken ct = default)
    {
        _nodes = 0;
        _stopped = false;
        _ct = ct;
        _clock.Restart();
        _deadline = timeMs > 0 ? timeMs : long.MaxValue;

        var rootMoves = MoveGenerator.Generate(root);
        if (rootMoves.Count == 0) return new(null, Array.Empty<Move>(), -WinScore, 0, 0, false);
        if (rootMoves.Count == 1)
            return new(rootMoves[0], new[] { rootMoves[0] }, Evaluate(root.Apply(rootMoves[0])) * -1, 1, 1, false);

        Move best = rootMoves[0];
        List<Move> bestPv = new() { best };
        int bestScore = 0, completedDepth = 0;

        for (int depth = 1; depth <= Math.Max(1, maxDepth); depth++)
        {
            // Search the previous best move first.
            rootMoves.Remove(best);
            rootMoves.Insert(0, best);

            int alpha = -WinScore - 1, beta = WinScore + 1;
            Move? iterBest = null;
            List<Move>? iterPv = null;
            foreach (var m in rootMoves)
            {
                var childPv = new List<Move>();
                int score = -Negamax(root.Apply(m), depth - 1, 1, -beta, -alpha, childPv);
                if (_stopped) break;
                if (iterBest == null || score > alpha)
                {
                    alpha = score;
                    iterBest = m;
                    iterPv = new List<Move> { m };
                    iterPv.AddRange(childPv);
                }
            }
            if (_stopped && completedDepth > 0) break; // keep the last completed iteration
            if (iterBest != null)
            {
                best = iterBest;
                bestPv = iterPv!;
                bestScore = alpha;
                completedDepth = depth;
            }
            if (_stopped || Math.Abs(bestScore) >= WinScore - 100) break;
        }
        return new(best, bestPv, bestScore, completedDepth, _nodes, _stopped);
    }

    int Negamax(Position pos, int depth, int ply, int alpha, int beta, List<Move> pv)
    {
        _nodes++;
        if ((_nodes & 1023) == 0 && (_clock.ElapsedMilliseconds >= _deadline || _ct.IsCancellationRequested))
            _stopped = true;
        if (_stopped) return 0;

        var moves = MoveGenerator.Generate(pos);
        if (moves.Count == 0) return -WinScore + ply;

        bool capture = moves[0].IsCapture;
        if (depth <= 0 && (!capture || ply > 40)) return Evaluate(pos);

        foreach (var m in moves)
        {
            var childPv = new List<Move>();
            int score = -Negamax(pos.Apply(m), depth - 1, ply + 1, -beta, -alpha, childPv);
            if (_stopped) return 0;
            if (score > alpha)
            {
                alpha = score;
                pv.Clear();
                pv.Add(m);
                pv.AddRange(childPv);
                if (alpha >= beta) break;
            }
        }
        return alpha;
    }

    /// <summary>Static evaluation from the side to move's point of view.</summary>
    public static int Evaluate(Position pos)
    {
        int score = 0;
        for (int sq = 1; sq <= 32; sq++)
        {
            byte p = pos[sq];
            if (p == Piece.Empty) continue;
            int v;
            if (Piece.IsKing(p)) v = KingValue;
            else
            {
                int row = (sq - 1) / 4;
                int advance = Piece.Is(p, Side.Black) ? row : 7 - row;
                v = ManValue + 2 * advance;
            }
            score += Piece.Is(p, Side.White) ? v : -v;
        }
        return pos.SideToMove == Side.White ? score : -score;
    }
}
