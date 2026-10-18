using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AudioStreamer.IPC.Transport;

internal static class WindowsPipeFactory
{
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    public static NamedPipeServerStream CreateServer(string pipeName, int maximumInstances, int bufferSize)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The AudioStreamer IPC server requires Windows Named Pipes.");
        var security = CreatePipeSecurity();
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptorMemory = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, descriptorMemory, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptorMemory,
                InheritHandle = false
            };
            using var handle = CreateNamedPipe(
                @"\\.\pipe\" + pipeName,
                PipeAccessDuplex | FileFlagOverlapped,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                maximumInstances,
                bufferSize,
                bufferSize,
                0,
                ref attributes);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create secure local IPC pipe.");

            var ownedHandle = new SafePipeHandle(handle.DangerousGetHandle(), ownsHandle: true);
            handle.SetHandleAsInvalid();
            try { return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, ownedHandle); }
            catch { ownedHandle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(descriptorMemory); }
    }

    public static IpcClientIdentity AuthenticateClient(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        IpcClientIdentity? result = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var sid = identity.User?.Value;
            var authenticated = identity.IsAuthenticated && !identity.IsAnonymous && !identity.IsGuest &&
                identity.Groups?.OfType<SecurityIdentifier>().Any(group => group.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)) == true;
            if (!authenticated || string.IsNullOrWhiteSpace(sid))
                throw new UnauthorizedAccessException("Named Pipe client is not an authenticated local user.");

            var isAdministrator = identity.Groups?.OfType<SecurityIdentifier>()
                .Any(group => group.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) == true;
            result = new IpcClientIdentity(sid, identity.Name ?? "Authenticated user", isAdministrator);
        });
        return result ?? throw new UnauthorizedAccessException("Named Pipe client identity could not be read.");
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        using var serviceIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var serviceSid = serviceIdentity.User ?? throw new UnauthorizedAccessException("Service SID is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(serviceSid);
        security.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, int maximumInstances,
        int outputBufferSize, int inputBufferSize, uint defaultTimeout, ref SecurityAttributes securityAttributes);
}

public sealed record IpcClientIdentity(string Sid, string DisplayName, bool IsAdministrator);
