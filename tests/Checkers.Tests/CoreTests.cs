using Checkers.Core;

namespace Checkers.Tests;

public static class CoreTests
{
    const string SpecFen = "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16";

    public static void Register()
    {
        const string G = "core";

        TestRunner.Add(G, "geometry: neighbours of corner and edge squares", () =>
        {
            Assert.Equal(5, Geometry.Neighbor[1, 2], "1 down-left");
            Assert.Equal(6, Geometry.Neighbor[1, 3], "1 down-right");
            Assert.Equal(0, Geometry.Neighbor[5, 2], "5 down-left (edge)");
            Assert.Equal(9, Geometry.Neighbor[5, 3], "5 down-right");
            Assert.Equal(8, Geometry.Neighbor[4, 2], "4 down-left");
            Assert.Equal(0, Geometry.Neighbor[4, 3], "4 down-right (edge)");
            Assert.Equal(32, Geometry.Landing[23, 3], "23 jump over 27 lands on 32");
            Assert.Equal(23, Geometry.Landing[14, 3], "14 jump over 18 lands on 23");
            Assert.Equal(18, Geometry.Neighbor[14, 3], "14 down-right is 18");
        });

        TestRunner.Add(G, "perft from the start position, depths 1-9 (CheckerBoard reference)", () =>
        {
            long[] expected = { 7, 49, 302, 1469, 7361, 36768, 179740, 845931, 3963680 };
            for (int d = 1; d <= expected.Length; d++)
                Assert.Equal(expected[d - 1], MoveGenerator.Perft(Position.Start, d), $"perft({d})");
        });

        TestRunner.Add(G, "perft of positions with kings and captures (CheckerBoard reference)", () =>
        {
            string file = Path.Combine(AppContext.BaseDirectory, "perft-reference.txt");
            int checkedCount = 0;
            foreach (string line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                string[] f = line.Split(';');
                var pos = Pdn.ParseFen(f[0]);
                int depth = int.Parse(f[1]);
                long nodes = long.Parse(f[2]);
                Assert.Equal(nodes, MoveGenerator.Perft(pos, depth), $"perft({depth}) of {f[0]}");
                checkedCount++;
            }
            Assert.True(checkedCount >= 20, $"expected at least 20 reference positions, found {checkedCount}");
        });

        TestRunner.Add(G, "spec position: Black must capture 14x23 or 16x23", () =>
        {
            var pos = Pdn.ParseFen(SpecFen);
            Assert.SetEqual(new[] { "14x23", "16x23" }, MoveGenerator.Generate(pos).Select(m => m.Notation), "legal moves");
        });

        TestRunner.Add(G, "multi-jump, crowning ends the move", () =>
        {
            // Black man on 5 jumps 9 and 18: 5x14x23.
            var pos = Pdn.ParseFen("B:W9,18:B5");
            Assert.SetEqual(new[] { "5x14x23" }, MoveGenerator.Generate(pos).Select(m => m.Notation), "double jump");

            // Black man on 22 jumps 26 and lands on 31, the crowning row: the move ends there
            // even though the new king could jump 27 next.
            var crown = Pdn.ParseFen("B:W26,27:B22");
            var moves = MoveGenerator.Generate(crown);
            Assert.SetEqual(new[] { "22x31" }, moves.Select(m => m.Notation), "jump onto the crowning row");
            Assert.True(moves[0].Promotes, "the man is crowned");
            var after = crown.Apply(moves[0]);
            Assert.Equal(Piece.BlackKing, after[31], "square 31 holds a black king");
            Assert.Equal(Piece.WhiteMan, after[27], "27 is not captured in the same move");
        });

        TestRunner.Add(G, "king captures backwards and forwards", () =>
        {
            var pos = Pdn.ParseFen("W:WK14:B9,10,18,17");
            var notations = MoveGenerator.Generate(pos).Select(m => m.Notation).ToList();
            Assert.True(notations.All(n => n.Contains('x')), "only captures are legal");
            Assert.True(notations.Count > 0, "the king has captures");
        });

        TestRunner.Add(G, "FEN: parse variants and canonical output", () =>
        {
            Assert.Equal("B:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,12",
                Pdn.ParseFen("B:W21-32:B1-12").ToFen(), "ranges");
            Assert.Equal("W:WK19,K23,30:B1,5,K12", Pdn.ParseFen("[FEN \"w:wk19,k23,30:b1,5,k12.\"]").ToFen(), "tag, lower case, period");
            Assert.Equal("B:W18:B1", Pdn.ParseFen(" B : W18 : B1 ").ToFen(), "spaces");
            Assert.Equal(Position.Start, Pdn.ParseFen("B:W21-32:B1-12"), "start position equality");
        });

        TestRunner.Add(G, "FEN: errors are reported", () =>
        {
            Assert.Throws<PdnException>(() => Pdn.ParseFen(""), "empty");
            Assert.Throws<PdnException>(() => Pdn.ParseFen("X:W1:B2"), "bad side");
            Assert.Throws<PdnException>(() => Pdn.ParseFen("B:W33:B1"), "square 33");
            Assert.Throws<PdnException>(() => Pdn.ParseFen("B:W1,2:B2"), "duplicate");
            Assert.Throws<PdnException>(() => Pdn.ParseFen("B:Wabc:B1"), "letters");
            Assert.Throws<PdnException>(() => Pdn.ParseAndValidate("B:W1:B5"), "white man on its crowning row");
            Assert.Throws<PdnException>(() => Pdn.ParseAndValidate("B:W30:B29"), "black man on its crowning row");
            Assert.Throws<PdnException>(() => Pdn.ParseAndValidate("B:W17-32:B1-4"), "16 white pieces");
            Assert.Throws<PdnException>(() => Pdn.ParseAndValidate("B:W:B1"), "no white pieces");
            Pdn.ParseAndValidate("B:WK1:BK32"); // kings on the far rows are fine
        });

        TestRunner.Add(G, "move notation: full, short, lenient and wrong moves", () =>
        {
            var start = Position.Start;
            Assert.True(MoveNotation.Resolve(start, "11-15").IsLegal, "11-15");
            Assert.True(MoveNotation.Resolve(start, " 9 - 13 ").IsLegal, "spaces");
            Assert.Equal(MoveNotation.Outcome.Illegal, MoveNotation.Resolve(start, "12-17").Outcome, "12-17");
            Assert.Equal(MoveNotation.Outcome.Malformed, MoveNotation.Resolve(start, "e2-e4").Outcome, "chess move");
            Assert.Equal(MoveNotation.Outcome.Malformed, MoveNotation.Resolve(start, "11").Outcome, "one square");

            var spec = Pdn.ParseFen(SpecFen);
            Assert.Equal(MoveNotation.Outcome.Illegal, MoveNotation.Resolve(spec, "22-18").Outcome, "white move with Black to move");
            Assert.Equal(MoveNotation.Outcome.Illegal, MoveNotation.Resolve(spec, "10-15").Outcome, "capture is mandatory");
            Assert.Equal("14x23", MoveNotation.Resolve(spec, "14x23").Move!.Notation, "14x23");

            var dj = Pdn.ParseFen("B:W9,18:B5");
            Assert.Equal("5x14x23", MoveNotation.Resolve(dj, "5x23").Move!.Notation, "short multi-jump");
            Assert.Equal("5x14x23", MoveNotation.Resolve(dj, "5x14x23").Move!.Notation, "full multi-jump");
            Assert.Equal(MoveNotation.Outcome.Illegal, MoveNotation.Resolve(dj, "5x14").Outcome, "stopping half way");
        });

        TestRunner.Add(G, "find the move from the resulting position", () =>
        {
            var spec = Pdn.ParseFen(SpecFen);
            var after = Pdn.ParseFen("W:W19,22,25,27,28,30,32:B1,5,6,7,10,12,16,23");
            Assert.Equal("14x23", MoveGenerator.FindMoveByResult(spec, after)?.Notation, "KingsRow's move in the spec position");
            Assert.Equal(null, MoveGenerator.FindMoveByResult(spec, spec)?.Notation, "unchanged board is not a move");
        });

        TestRunner.Add(G, "KingsRow status line parsing", () =>
        {
            var s = KingsRowStatus.Parse("value=302,  depth 26/21.5/38,  1.1s,  11597 kN/s,  pv 14x23 27x18 16x23 25-21 12-16");
            Assert.Equal(302, s.Value, "value");
            Assert.Equal('=', s.Bound, "bound");
            Assert.Equal(26, s.Depth, "depth");
            Assert.Equal(1.1, s.Seconds, "seconds");
            Assert.Equal(11597L, s.KiloNodesPerSecond, "kN/s");
            Assert.SetEqual(new[] { "14x23", "27x18", "16x23", "25-21", "12-16" }, s.Pv, "pv");

            var neg = KingsRowStatus.Parse("value<-2721,  depth 14/10.3/20,  0.0s,  7614 kN/s,  pv 30-25 5-9");
            Assert.Equal(-2721, neg.Value, "negative value");
            Assert.Equal('<', neg.Bound, "upper bound");

            var book = KingsRowStatus.Parse("12-16(-1/37)*  11-15(1/25)*  10-14(-1/39)*");
            Assert.True(book.IsBook, "book line");
            Assert.True(KingsRowStatus.Parse("No moves").NoMoves, "no moves");
            Assert.Equal(8, KingsRowStatus.ParseDbPieces("Kingsrow WLD database found for 8 pieces"), "db pieces from check");
            Assert.Equal(8, KingsRowStatus.ParseDbPieces("Wait for db init; Kingsrow db, 8 pieces, 16384 mb cache, 128 mb hashtable ..."), "db pieces from init");
        });

        TestRunner.Add(G, "LRU cache: capacity, recency and expiry", () =>
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cache = new LruCache<string, int>(2, TimeSpan.FromMinutes(15), () => now);
            cache.Set("a", 1);
            cache.Set("b", 2);
            Assert.True(cache.TryGet("a", out _), "a present");   // a becomes most recent
            cache.Set("c", 3);                                    // evicts b
            Assert.True(!cache.TryGet("b", out _), "b evicted");
            Assert.True(cache.TryGet("a", out int a) && a == 1, "a kept");
            now = now.AddMinutes(16);
            Assert.True(!cache.TryGet("a", out _), "a expired");
            Assert.Equal(1, cache.Count, "expired entry removed on access");
        });

        TestRunner.Add(G, "stub engine: legal move in time, takes a free piece", () =>
        {
            var search = new SimpleSearch();
            var r = search.Search(Position.Start, 30, 200);
            Assert.True(r.BestMove != null, "has a move");
            Assert.True(MoveGenerator.Generate(Position.Start).Any(m => m.Notation == r.BestMove!.Notation), "move is legal");
            Assert.True(r.Depth >= 4, $"reached depth {r.Depth}");

            // White to move can win a man with 23x14... only one capture exists, so it must be played.
            var pos = Pdn.ParseFen("W:W23,30:B18,1");
            var r2 = search.Search(pos, 10, 200);
            Assert.Equal("23x14", r2.BestMove?.Notation, "forced capture");
        });
    }
}
