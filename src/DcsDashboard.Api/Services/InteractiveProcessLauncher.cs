using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DcsDashboard.Api.Services;

public sealed class InteractiveProcessLauncher
{
    private const uint InvalidSessionId = 0xffffffff;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private static readonly nint CurrentServer = nint.Zero;

    public InteractiveLaunchResult Start(string executablePath, string arguments, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            var process = Process.Start(new ProcessStartInfo(executablePath, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("The operating system did not start the DCS process.");
            return new(process, null);
        }

        var sessionId = FindInteractiveSession();
        if (sessionId is null)
            throw new InvalidOperationException("DCS needs a signed-in Windows desktop session. Sign in to the DCS server PC, then start the server again from Groundcrew.");

        if (!WTSQueryUserToken((uint)sessionId.Value, out var userToken))
            throw WindowsError($"Groundcrew could not access Windows session {sessionId}. Ensure the Groundcrew service is running as Local System");

        nint environment = nint.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, userToken, false))
                throw WindowsError("Groundcrew could not create the signed-in user's environment");

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default",
                ShowWindow = 1
            };
            var commandLine = new StringBuilder($"\"{executablePath}\"{(string.IsNullOrWhiteSpace(arguments) ? "" : $" {arguments.Trim()}")}");
            if (!CreateProcessAsUser(
                    userToken,
                    executablePath,
                    commandLine,
                    nint.Zero,
                    nint.Zero,
                    false,
                    CreateNewProcessGroup | CreateUnicodeEnvironment,
                    environment,
                    workingDirectory,
                    ref startup,
                    out var processInformation))
                throw WindowsError($"Windows could not launch DCS in interactive session {sessionId}");

            try
            {
                return new(Process.GetProcessById((int)processInformation.ProcessId), sessionId);
            }
            finally
            {
                CloseHandle(processInformation.Thread);
                CloseHandle(processInformation.Process);
            }
        }
        finally
        {
            if (environment != nint.Zero) DestroyEnvironmentBlock(environment);
            CloseHandle(userToken);
        }
    }

    private static int? FindInteractiveSession()
    {
        var activeSessions = new List<int>();
        if (WTSEnumerateSessions(CurrentServer, 0, 1, out var sessions, out var count))
        {
            try
            {
                var size = Marshal.SizeOf<WtsSessionInfo>();
                for (var index = 0; index < count; index++)
                {
                    var session = Marshal.PtrToStructure<WtsSessionInfo>(sessions + index * size);
                    if (session.State == WtsConnectState.Active) activeSessions.Add(session.SessionId);
                }
            }
            finally { WTSFreeMemory(sessions); }
        }

        if (activeSessions.Count > 0) return activeSessions[0];
        var consoleSession = WTSGetActiveConsoleSessionId();
        return consoleSession == InvalidSessionId ? null : (int)consoleSession;
    }

    private static InvalidOperationException WindowsError(string message)
    {
        var code = Marshal.GetLastWin32Error();
        return new InvalidOperationException($"{message}: {new Win32Exception(code).Message} (Windows error {code}).");
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(nint server, int reserved, int version, out nint sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out nint token);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        nint token,
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public nint WinStationName;
        public WtsConnectState State;
    }

    private enum WtsConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint ReservedData;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}

public sealed record InteractiveLaunchResult(Process Process, int? SessionId);
