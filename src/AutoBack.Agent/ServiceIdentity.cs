using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutoBack.Agent;

/// <summary>Obtains the worker identity without granting logon privileges to the setup account.</summary>
internal static class ServiceIdentity
{
    private const string Prefix = "AutoBackAccess-";

    internal static SafeAccessTokenHandle Acquire(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var current = WindowsIdentity.GetCurrent();
        if (IsWorker(current)) return Duplicate(current);
        if (LogonUser("LocalService", "NT AUTHORITY", null, 5, 0, out var token)) return token;
        var logonError = Marshal.GetLastWin32Error();
        token?.Dispose();
        if (!new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
            throw new Win32Exception(logonError, "LocalService verification requires elevated setup. Open Configure AutoBack Agent as administrator.");
        return AcquireFromServiceAsync(cancellation).GetAwaiter().GetResult();
    }

    private static bool IsWorker(WindowsIdentity identity)
        => identity.User?.IsWellKnown(WellKnownSidType.LocalServiceSid) == true;

    private static SafeAccessTokenHandle Duplicate(WindowsIdentity identity)
    {
        if (!DuplicateTokenEx(identity.AccessToken, 0x000e, IntPtr.Zero, 2, 2, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot retain the LocalService verification identity.");
        return token;
    }

    private static async Task<SafeAccessTokenHandle> AcquireFromServiceAsync(CancellationToken cancellation)
    {
        // The helper only supplies its identity over a private local pipe; it never receives paths or backup credentials.
        var name = Prefix + Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalServiceSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
#if NETFRAMEWORK
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 256, security);
#else
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 256, security);
#endif
        var executable = System.IO.Path.ChangeExtension(typeof(ServiceIdentity).Assembly.Location, ".exe");
        if (!File.Exists(executable)) throw new IOException("The AutoBack Agent executable is missing. Run the installed agent configuration tool.");
        using var manager = OpenSCManager(null, null, 0x0003);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open Windows Service Control Manager for LocalService verification.");
        var command = $"\"{executable}\" access-identity-worker {name} {nonce}";
        using var service = CreateService(manager, name, name, 0x10034, 0x10, 3, 1, command, null, IntPtr.Zero, null, @"NT AUTHORITY\LocalService", null);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot register the temporary LocalService verification helper.");
        SafeAccessTokenHandle? result = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            if (!StartService(service, 0, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not start the LocalService verification helper. Check service logon policy.");
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            var expected = Encoding.ASCII.GetBytes(nonce);
            var received = new byte[expected.Length];
            for (var offset = 0; offset < received.Length;)
            {
                var read = await pipe.ReadAsync(received, offset, received.Length - offset, timeout.Token).ConfigureAwait(false);
                if (read == 0) throw new IOException("The LocalService verification helper disconnected before identifying itself.");
                offset += read;
            }
            if (!expected.SequenceEqual(received)) throw new IOException("The LocalService verification helper sent an invalid handshake.");
            if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf(typeof(ServiceStatusProcess)), out _) ||
                !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid != status.ProcessId || clientPid == 0)
                throw new IOException("The verification pipe client did not match the temporary Windows service.");
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
                if (!IsWorker(identity)) throw new IOException("The verification helper did not run as LocalService.");
                result = Duplicate(identity);
            });
            cancellation.ThrowIfCancellationRequested();
            return result ?? throw new IOException("No LocalService verification identity was received.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            result?.Dispose();
            throw new IOException("The LocalService verification helper timed out. Check Windows service startup and logon policy.");
        }
        catch { result?.Dispose(); throw; }
        finally
        {
            // Disconnect releases the helper. Stop/delete only the uniquely named service created by this call.
            pipe.Dispose();
            ControlService(service, 1, out _);
            if (!DeleteService(service))
            {
                var error = Marshal.GetLastWin32Error();
                result?.Dispose();
                throw new Win32Exception(error, "Could not remove temporary verification service " + name);
            }
        }
    }

    internal static int RunWorker(string[] args)
    {
        if (args.Length != 3 || !args[1].StartsWith(Prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1].Substring(Prefix.Length), "N", out _) || !Guid.TryParseExact(args[2], "N", out _))
            throw new ArgumentException("Invalid verification helper arguments.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!IsWorker(identity)) throw new UnauthorizedAccessException("The verification helper must be started as LocalService by Windows Service Control Manager.");
        using var worker = new IdentityWorker(args[1], args[2]);
        ServiceBase.Run(worker);
        return 0;
    }

    private sealed class IdentityWorker : ServiceBase
    {
        private readonly string _nonce;
        private readonly CancellationTokenSource _stop = new();
        internal IdentityWorker(string name, string nonce) { ServiceName = name; _nonce = nonce; AutoLog = false; }
        protected override void OnStart(string[] args)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", ServiceName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                    _stop.CancelAfter(TimeSpan.FromSeconds(20));
                    await pipe.ConnectAsync(15000, _stop.Token).ConfigureAwait(false);
                    var hello = Encoding.ASCII.GetBytes(_nonce);
                    await pipe.WriteAsync(hello, 0, hello.Length, _stop.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(_stop.Token).ConfigureAwait(false);
                    // Keep the process alive until the parent has authenticated the PID and duplicated its token.
                    await pipe.ReadAsync(new byte[1], 0, 1, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { }
                finally { Stop(); }
            });
        }
        protected override void OnStop() => _stop.Cancel();
        protected override void Dispose(bool disposing) { if (disposing) _stop.Dispose(); base.Dispose(disposing); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string domain, string? password, int type, int provider, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle source, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle CreateService(ServiceHandle manager, string name, string displayName, uint access, uint type, uint start, uint errorControl, string command, string? group, IntPtr tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool StartService(ServiceHandle service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
