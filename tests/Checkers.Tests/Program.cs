using Checkers.Core;
using Checkers.Tests;

// Usage:
//   dotnet run --project tests/Checkers.Tests                 all tests
//   dotnet run --project tests/Checkers.Tests -- core         only core tests
//   dotnet run --project tests/Checkers.Tests -- api          only API tests (starts the API with the stub engine)
//   dotnet run --project tests/Checkers.Tests -- --random-fens 40   print random positions (to build perft reference data)

if (args.Length >= 1 && args[0] == "--random-fens")
{
    int n = args.Length > 1 ? int.Parse(args[1]) : 40;
    foreach (var fen in RandomPositions.Generate(n, seed: 2026)) Console.WriteLine(fen);
    return 0;
}

CoreTests.Register();
ApiTests.Register();
int exitCode = await TestRunner.RunAsync(args);
await ApiTests.ShutdownAsync();
return exitCode;

static class RandomPositions
{
    /// <summary>Positions from random games (many with kings) plus random king-heavy setups.</summary>
    public static IEnumerable<string> Generate(int count, int seed)
    {
        var rng = new Random(seed);
        var seen = new HashSet<string>();
        while (seen.Count < count)
        {
            Position pos;
            if (seen.Count % 3 == 2)
            {
                // Random setup with kings.
                var b = new byte[33];
                int pieces = rng.Next(4, 13);
                for (int i = 0; i < pieces; i++)
                {
                    int sq = rng.Next(1, 33);
                    if (b[sq] != 0) continue;
                    bool white = i % 2 == 0;
                    bool king = rng.Next(3) == 0;
                    if (!king && white && sq <= 4) king = true;
                    if (!king && !white && sq >= 29) king = true;
                    b[sq] = (byte)((white ? Piece.White : Piece.Black) | (king ? Piece.King : Piece.Man));
                }
                pos = new Position(b, rng.Next(2) == 0 ? Side.White : Side.Black);
            }
            else
            {
                pos = Position.Start;
                int plies = rng.Next(10, 90);
                for (int i = 0; i < plies; i++)
                {
                    var moves = MoveGenerator.Generate(pos);
                    if (moves.Count == 0) break;
                    pos = pos.Apply(moves[rng.Next(moves.Count)]);
                }
            }
            if (Pdn.Validate(pos).Count > 0 || MoveGenerator.Generate(pos).Count == 0) continue;
            seen.Add(pos.ToFen());
        }
        return seen;
    }
}
