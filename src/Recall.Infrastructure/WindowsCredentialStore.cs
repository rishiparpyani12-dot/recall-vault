using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Recall.Infrastructure;

public sealed class WindowsCredentialStore : IRecallCredentialStore
{
    public bool TryRead(string targetName, out string secret)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Encrypted Recall Vault storage currently requires Windows Credential Manager.");
        if (!NativeMethods.CredRead(targetName, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168)
            {
                secret = string.Empty;
                return false;
            }
            throw new Win32Exception(error, "Unable to read the Recall Vault database credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                secret = Encoding.UTF8.GetString(bytes);
                return true;
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public void Write(string targetName, string secret)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Encrypted Recall Vault storage currently requires Windows Credential Manager.");
        var blob = Encoding.UTF8.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = 1,
                TargetName = targetName,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = 2,
                UserName = Environment.UserName
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to store the Recall Vault database credential.");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(blob);
            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll")]
        internal static extern void CredFree(IntPtr buffer);
    }
}
