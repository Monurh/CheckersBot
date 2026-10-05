using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Checkers.Core;

namespace KrWorker;

/// <summary>
/// The KingsRow engine DLL, called through the CheckerBoard engine API:
///   int getmove(int board[8][8], int color, double maxtime, char str[1024], int *playnow, int info, int moreinfo, CBmove *move);
///   int enginecommand(const char *command, char reply[1024]);
/// One instance per process: the DLL keeps global state.
/// </summary>
sealed class KingsRowDll
{
    public const int InfoResetMoves = 1;   // CB_RESET_MOVES: forget game history
    public const int InfoExactTime = 2;    // CB_EXACT_TIME: stop at maxtime instead of using it as an average

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int GetMoveFn(IntPtr board, int color, double maxtime, IntPtr str, IntPtr playnow, int info, int moreinfo, IntPtr move);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int EngineCommandFn(IntPtr command, IntPtr reply);

    const int StrSize = 1024, ReplySize = 1024, CommandSize = 1024, MoveSize = 1024;

    readonly GetMoveFn _getmove;
    readonly EngineCommandFn _enginecommand;
    readonly IntPtr _board = Marshal.AllocHGlobal(64 * sizeof(int));
    readonly IntPtr _str = Marshal.AllocHGlobal(StrSize);
    readonly IntPtr _playnow = Marshal.AllocHGlobal(sizeof(int));
    readonly IntPtr _move = Marshal.AllocHGlobal(MoveSize);
    readonly IntPtr _command = Marshal.AllocHGlobal(CommandSize);
    readonly IntPtr _reply = Marshal.AllocHGlobal(ReplySize);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectoryW(string? lpPathName);

    const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    KingsRowDll(IntPtr module)
    {
        IntPtr gm = GetProcAddress(module, "getmove");
        IntPtr ec = GetProcAddress(module, "enginecommand");
        if (gm == IntPtr.Zero || ec == IntPtr.Zero)
            throw new InvalidOperationException("The DLL does not export getmove/enginecommand: it is not a CheckerBoard engine.");
        _getmove = Marshal.GetDelegateForFunctionPointer<GetMoveFn>(gm);
        _enginecommand = Marshal.GetDelegateForFunctionPointer<EngineCommandFn>(ec);
    }

    /// <param name="dllPath">Full path of Kingsrow64.dll.</param>
    /// <param name="helperDir">Folder searched for helper DLLs such as egdb64.dll.</param>
    public static KingsRowDll Load(string dllPath, string helperDir)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The KingsRow engine needs Windows.");
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Kingsrow64.dll needs a 64-bit process.");
        if (!File.Exists(dllPath)) throw new FileNotFoundException($"Engine DLL not found: {dllPath}");

        SetDllDirectoryW(helperDir);
        IntPtr module = LoadLibraryExW(dllPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (module == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"Cannot load {dllPath}: {new Win32Exception(err).Message} (error {err}). " +
                "Check that egdb64.dll is next to it and that the Microsoft Visual C++ x64 runtime is installed.");
        }
        return new KingsRowDll(module);
    }

    public (int rc, string reply) Command(string command)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(command);
        if (bytes.Length >= CommandSize) throw new ArgumentException("Command too long.");
        Marshal.Copy(bytes, 0, _command, bytes.Length);
        Marshal.WriteByte(_command, bytes.Length, 0);
        Zero(_reply, ReplySize);
        int rc = _enginecommand(_command, _reply);
        return (rc, ReadAnsi(_reply, ReplySize).Trim());
    }

    /// <summary>Ask a running getmove to return now with its best move so far. Safe to call from another thread.</summary>
    public void RequestStop() => Marshal.WriteInt32(_playnow, 1);

    /// <summary>Current contents of the status string (safe to read while a search runs).</summary>
    public string CurrentStatus => ReadAnsi(_str, StrSize);

    /// <summary>Run a search. Blocks until the engine returns. Returns the getmove result code and the board after the move.</summary>
    public (int rc, Position after, string status) GetMove(Position pos, double maxTimeSec, int info, CancellationToken stop = default)
    {
        var board = ToBoard8x8(pos);
        Marshal.Copy(board, 0, _board, 64);
        Zero(_str, StrSize);
        Zero(_move, MoveSize);
        Marshal.WriteInt32(_playnow, 0);

        // Registered after playnow is reset, so a stop that arrived early still takes effect (Register runs it at once).
        using var reg = stop.Register(RequestStop);
        int rc = _getmove(_board, (int)pos.SideToMove, maxTimeSec, _str, _playnow, info, 0, _move);

        Marshal.Copy(_board, board, 0, 64);
        var after = FromBoard8x8(board, pos.SideToMove.Opponent());
        return (rc, after, ReadAnsi(_str, StrSize).Trim());
    }

    /// <summary>
    /// CheckerBoard's Board8x8 is int[8][8] indexed [x][y]. For English checkers square n (1..32) is at
    /// y = (n-1)/4 and x = 2*(3-(n-1)%4), plus 1 on odd rows (same as numbertocoors in CheckerBoard).
    /// </summary>
    static (int x, int y) Coords(int square)
    {
        int n = square - 1;
        int y = n / 4;
        int x = 2 * (3 - n % 4);
        if ((y & 1) != 0) x++;
        return (x, y);
    }

    static int[] ToBoard8x8(Position pos)
    {
        var b = new int[64];
        for (int sq = 1; sq <= 32; sq++)
        {
            var (x, y) = Coords(sq);
            b[x * 8 + y] = pos[sq];
        }
        return b;
    }

    static Position FromBoard8x8(int[] b, Side sideToMove)
    {
        var squares = new byte[33];
        for (int sq = 1; sq <= 32; sq++)
        {
            var (x, y) = Coords(sq);
            squares[sq] = (byte)b[x * 8 + y];
        }
        return new Position(squares, sideToMove);
    }

    static string ReadAnsi(IntPtr p, int max)
    {
        var buf = new byte[max];
        Marshal.Copy(p, buf, 0, max);
        int len = Array.IndexOf(buf, (byte)0);
        return Encoding.Latin1.GetString(buf, 0, len < 0 ? max : len);
    }

    static void Zero(IntPtr p, int size)
    {
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
    }
}
