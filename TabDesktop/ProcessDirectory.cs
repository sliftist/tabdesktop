using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TabDesktop.Interop;

namespace TabDesktop;

// Per-process path queries. The current directory lives in the target's PEB (RTL_USER_PROCESS_PARAMETERS.CurrentDirectory), so it's read straight out of process memory — these are kernel-side reads that never wait on the target pumping messages, so unlike window queries they're safe on any thread even against hung processes.
//
// The window-owning process is usually the wrong one to ask: a terminal host (WindowsTerminal.exe, conhost) sits in System32 forever while the folder the user cares about is the hosted shell's — a child process. GetTreeDirectories therefore walks the window process's descendants and collects the "interesting" working directories, where boring means under the Windows directory or a process idling in its own install folder (the default for GUI apps launched from the shell).
//
// A single terminal process can own several windows (Windows Terminal hosts every window and tab in one WindowsTerminal.exe), so the pid alone can't tell which shell belongs to which window. ConPTY provides the missing link: every conpty session gets a hidden "PseudoConsoleWindow" owned by the session's root client (the shell), and the terminal reparents it onto the terminal window hosting that tab (ConptyReparentPseudoConsole — kept current across tab tear-out). SnapshotTabShells reads those parent links back, giving each terminal window its exact shells. Those shells must then be treated as tree roots of their own — with the default-terminal handoff (conhost -Embedding) a tab's shell is a child of whatever launched it, not of the terminal process. Title matching (full path, or its leaf folder name, appearing in the window title) then picks among a window's multiple tabs, falling back to shallowest-in-the-tree.
public static class ProcessDirectory
{
    // Win64 layouts: the ProcessParameters pointer inside the PEB, the CurrentDirectory UNICODE_STRING inside RTL_USER_PROCESS_PARAMETERS, and the buffer pointer inside UNICODE_STRING after Length/MaximumLength plus alignment padding. 64-bit offsets only — a WOW64 target keeps its live state in the 32-bit PEB, so its directory may come back stale or missing; callers fall back to the exe directory.
    private const int PebProcessParametersOffset = 0x20;
    private const int ParametersCurrentDirectoryOffset = 0x38;
    private const int UnicodeStringBufferOffset = 8;

    private const int MaxExePathChars = 1024;

    // Backstop against pathological process trees (a terminal hosting a build spawning hundreds of workers); the interesting shell is always near the root.
    private const int MaxTreeCandidates = 64;

    // Leaf folder names shorter than this ("a", "ui") match window titles too easily to be trusted.
    private const int MinLeafMatchChars = 3;

    public static string? GetExecutablePath(uint pid)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var buffer = new StringBuilder(MaxExePathChars);
            int size = buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                return null;
            }
            return buffer.ToString();
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    public static string? GetCurrentDirectory(uint pid)
    {
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, pid);
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var info = new NativeMethods.PROCESS_BASIC_INFORMATION();
            if (NativeMethods.NtQueryInformationProcess(process, NativeMethods.ProcessBasicInformation, ref info, Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _) != 0 || info.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }
            byte[] pointerBytes = new byte[sizeof(long)];
            if (!TryRead(process, info.PebBaseAddress + PebProcessParametersOffset, pointerBytes))
            {
                return null;
            }
            long parameters = BitConverter.ToInt64(pointerBytes);
            if (parameters == 0)
            {
                return null;
            }
            byte[] unicodeString = new byte[UnicodeStringBufferOffset + sizeof(long)];
            if (!TryRead(process, (IntPtr)(parameters + ParametersCurrentDirectoryOffset), unicodeString))
            {
                return null;
            }
            ushort lengthBytes = BitConverter.ToUInt16(unicodeString, 0);
            long stringBuffer = BitConverter.ToInt64(unicodeString, UnicodeStringBufferOffset);
            if (lengthBytes == 0 || stringBuffer == 0)
            {
                return null;
            }
            byte[] chars = new byte[lengthBytes];
            if (!TryRead(process, (IntPtr)stringBuffer, chars))
            {
                return null;
            }
            return Path.TrimEndingDirectorySeparator(Encoding.Unicode.GetString(chars));
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static bool TryRead(IntPtr process, IntPtr address, byte[] buffer)
    {
        return NativeMethods.ReadProcessMemory(process, address, buffer, buffer.Length, out IntPtr read) && read == buffer.Length;
    }

    // One system-wide snapshot shared across all window entries in a refresh pass.
    public static Dictionary<uint, List<uint>> SnapshotChildren()
    {
        var childrenByParent = new Dictionary<uint, List<uint>>();
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE)
        {
            return childrenByParent;
        }
        try
        {
            var entry = new NativeMethods.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>() };
            if (!NativeMethods.Process32First(snapshot, ref entry))
            {
                return childrenByParent;
            }
            do
            {
                if (!childrenByParent.TryGetValue(entry.th32ParentProcessID, out List<uint>? children))
                {
                    children = new List<uint>();
                    childrenByParent[entry.th32ParentProcessID] = children;
                }
                children.Add(entry.th32ProcessID);
            } while (NativeMethods.Process32Next(snapshot, ref entry));
            return childrenByParent;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    public sealed record TreeDirectory(int Depth, string Cwd);

    public static List<TreeDirectory> GetTreeDirectories(uint pid, Dictionary<uint, List<uint>> childrenByParent)
    {
        var candidates = new List<(uint Pid, int Depth)>();
        var visited = new HashSet<uint> { pid };
        var creationTimes = new Dictionary<uint, long?>();
        var queue = new Queue<(uint Pid, int Depth)>();
        queue.Enqueue((pid, 0));
        while (queue.Count > 0 && candidates.Count < MaxTreeCandidates)
        {
            (uint current, int depth) = queue.Dequeue();
            candidates.Add((current, depth));
            if (!childrenByParent.TryGetValue(current, out List<uint>? children))
            {
                continue;
            }
            foreach (uint child in children)
            {
                if (!visited.Add(child))
                {
                    continue;
                }
                // Snapshot parent ids can be recycled pids pointing at an unrelated process; a genuine child was created after its parent, so drop edges that violate that.
                long? parentTime = GetCreationTime(current, creationTimes);
                long? childTime = GetCreationTime(child, creationTimes);
                if (parentTime is long parentCreated && childTime is long childCreated && childCreated < parentCreated)
                {
                    continue;
                }
                queue.Enqueue((child, depth + 1));
            }
        }

        var interesting = new List<TreeDirectory>();
        foreach ((uint candidate, int depth) in candidates)
        {
            string? cwd = GetCurrentDirectory(candidate);
            if (cwd is null)
            {
                continue;
            }
            string? exeDirectory = Path.GetDirectoryName(GetExecutablePath(candidate));
            if (!IsBoring(cwd, exeDirectory))
            {
                interesting.Add(new TreeDirectory(depth, cwd));
            }
        }
        return interesting;
    }

    private const string PseudoConsoleWindowClass = "PseudoConsoleWindow";

    // Maps each terminal window hwnd to the root-shell pids of the conpty sessions it hosts, via the reparented PseudoConsoleWindows (see the class comment). One EnumWindows pass shared across all window entries in a refresh.
    public static Dictionary<IntPtr, HashSet<uint>> SnapshotTabShells()
    {
        var shellsByWindow = new Dictionary<IntPtr, HashSet<uint>>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var className = new StringBuilder(PseudoConsoleWindowClass.Length + 1);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() != PseudoConsoleWindowClass)
            {
                return true;
            }
            IntPtr host = NativeMethods.GetParent(hwnd);
            if (host == IntPtr.Zero)
            {
                host = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
            }
            if (host == IntPtr.Zero)
            {
                return true;
            }
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint shellPid);
            if (!shellsByWindow.TryGetValue(host, out HashSet<uint>? shells))
            {
                shells = new HashSet<uint>();
                shellsByWindow[host] = shells;
            }
            shells.Add(shellPid);
            return true;
        }, IntPtr.Zero);
        return shellsByWindow;
    }

    public static string? ChooseBestDirectory(List<TreeDirectory> interesting, string windowTitle)
    {
        if (interesting.Count == 0)
        {
            return null;
        }
        List<TreeDirectory> pool = interesting;
        List<TreeDirectory> matched = pool.Where(entry => windowTitle.Contains(entry.Cwd, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0)
        {
            matched = pool.Where(entry => LeafMatchesTitle(windowTitle, entry.Cwd)).ToList();
        }
        if (matched.Count > 0)
        {
            pool = matched;
        }
        // Shallowest wins — that's the hosted shell rather than whatever it spawned; among equals, prefer the directory most of the pool agrees on.
        int minDepth = pool.Min(entry => entry.Depth);
        return pool
            .Where(entry => entry.Depth == minDepth)
            .OrderByDescending(entry => pool.Count(other => string.Equals(other.Cwd, entry.Cwd, StringComparison.OrdinalIgnoreCase)))
            .First().Cwd;
    }

    private static bool LeafMatchesTitle(string windowTitle, string cwd)
    {
        string leaf = Path.GetFileName(cwd);
        return leaf.Length >= MinLeafMatchChars && windowTitle.Contains(leaf, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBoring(string directory, string? exeDirectory)
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (directory.StartsWith(windowsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return exeDirectory is not null && string.Equals(directory, Path.TrimEndingDirectorySeparator(exeDirectory), StringComparison.OrdinalIgnoreCase);
    }

    private static long? GetCreationTime(uint pid, Dictionary<uint, long?> cache)
    {
        if (cache.TryGetValue(pid, out long? cached))
        {
            return cached;
        }
        long? result = GetCreationFileTime(pid);
        cache[pid] = result;
        return result;
    }

    public static long? GetCreationFileTime(uint pid)
    {
        long? result = null;
        IntPtr process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                if (NativeMethods.GetProcessTimes(process, out long creation, out _, out _, out _))
                {
                    result = creation;
                }
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }
        return result;
    }

    // Whole seconds by design: same-second processes are expected to collide here and get deduped by pid at the sort site.
    public static double? GetCreationUnixSeconds(uint pid)
    {
        if (GetCreationFileTime(pid) is not long fileTime)
        {
            return null;
        }
        return DateTimeOffset.FromFileTime(fileTime).ToUnixTimeSeconds();
    }
}
