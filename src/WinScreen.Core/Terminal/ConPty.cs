using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinScreen.Core.Terminal;

/// <summary>
/// Windows ConPTY (Pseudo Console) 래퍼
/// </summary>
public sealed class ConPty : IDisposable
{
    private IntPtr _hPC = IntPtr.Zero;
    private SafeFileHandle? _hPipeIn;
    private SafeFileHandle? _hPipeOut;
    private SafeProcessHandle? _processHandle;
    private bool _disposed;

    public SafeFileHandle? PipeIn => _hPipeIn;
    public SafeFileHandle? PipeOut => _hPipeOut;
    public int ProcessId { get; private set; }
    public bool HasExited => _processHandle?.IsClosed == true || CheckProcessExited();

    private bool CheckProcessExited()
    {
        if (_processHandle == null || _processHandle.IsClosed) return true;
        return NativeMethods.WaitForSingleObject(_processHandle.DangerousGetHandle(), 0) == 0;
    }

    public int GetExitCode()
    {
        if (_processHandle == null || _processHandle.IsClosed) return -1;
        if (NativeMethods.GetExitCodeProcess(_processHandle.DangerousGetHandle(), out var exitCode))
            return (int)exitCode;
        return -1;
    }

    /// <summary>
    /// ConPTY 세션 생성
    /// </summary>
    public static ConPty Create(
        string commandLine,
        string? workingDirectory = null,
        Dictionary<string, string>? environment = null,
        short cols = 120,
        short rows = 30)
    {
        var pty = new ConPty();
        
        try
        {
            // 파이프 생성 (서버 → PTY 입력, PTY → 서버 출력)
            if (!NativeMethods.CreatePipe(out var hPipePtyIn, out pty._hPipeIn, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed");

            if (!NativeMethods.CreatePipe(out pty._hPipeOut, out var hPipePtyOut, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed");

            // Pseudo Console 생성
            var size = new NativeMethods.COORD { X = cols, Y = rows };
            var hr = NativeMethods.CreatePseudoConsole(
                size,
                hPipePtyIn.DangerousGetHandle(),
                hPipePtyOut.DangerousGetHandle(),
                0,
                out pty._hPC);

            if (hr != 0)
                throw new Win32Exception(hr, $"CreatePseudoConsole failed: 0x{hr:X8}");

            // PTY 측 파이프 핸들은 더 이상 필요 없음
            hPipePtyIn.Dispose();
            hPipePtyOut.Dispose();

            // 프로세스 시작
            pty.StartProcess(commandLine, workingDirectory, environment);

            return pty;
        }
        catch
        {
            pty.Dispose();
            throw;
        }
    }

    private void StartProcess(string commandLine, string? workingDirectory, Dictionary<string, string>? environment)
    {
        // 속성 리스트 크기 계산
        var attrSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);

        var attrList = Marshal.AllocHGlobal(attrSize);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

            // Pseudo Console 속성 설정
            if (!NativeMethods.UpdateProcThreadAttribute(
                attrList,
                0,
                NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
            }

            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO
                {
                    cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>()
                },
                lpAttributeList = attrList
            };

            // 환경 변수 블록 생성
            var envBlock = IntPtr.Zero;
            if (environment != null && environment.Count > 0)
            {
                envBlock = CreateEnvironmentBlock(environment);
            }

            try
            {
                var processInfo = new NativeMethods.PROCESS_INFORMATION();
                var creationFlags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT;
                if (envBlock != IntPtr.Zero)
                    creationFlags |= NativeMethods.CREATE_UNICODE_ENVIRONMENT;

                if (!NativeMethods.CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    envBlock,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
                }

                ProcessId = processInfo.dwProcessId;
                _processHandle = new SafeProcessHandle(processInfo.hProcess, true);
                NativeMethods.CloseHandle(processInfo.hThread);
            }
            finally
            {
                if (envBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(envBlock);
            }
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    private static IntPtr CreateEnvironmentBlock(Dictionary<string, string> environment)
    {
        // 현재 환경 변수 가져오기
        var currentEnv = Environment.GetEnvironmentVariables();
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (System.Collections.DictionaryEntry entry in currentEnv)
        {
            merged[entry.Key?.ToString() ?? ""] = entry.Value?.ToString() ?? "";
        }

        foreach (var kvp in environment)
        {
            merged[kvp.Key] = kvp.Value;
        }

        // 환경 블록 문자열 생성 (KEY=VALUE\0KEY=VALUE\0\0)
        var envString = string.Join('\0', merged.Select(kv => $"{kv.Key}={kv.Value}")) + "\0\0";
        var bytes = System.Text.Encoding.Unicode.GetBytes(envString);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    /// <summary>
    /// 터미널 크기 조정
    /// </summary>
    public void Resize(short cols, short rows)
    {
        if (_hPC == IntPtr.Zero) return;

        var size = new NativeMethods.COORD { X = cols, Y = rows };
        var hr = NativeMethods.ResizePseudoConsole(_hPC, size);
        if (hr != 0)
            throw new Win32Exception(hr, $"ResizePseudoConsole failed: 0x{hr:X8}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hPC != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        _hPipeIn?.Dispose();
        _hPipeOut?.Dispose();
        _processHandle?.Dispose();
    }

    private static class NativeMethods
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(
            out SafeFileHandle hReadPipe,
            out SafeFileHandle hWritePipe,
            IntPtr lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(
            COORD size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr Attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    }
}
