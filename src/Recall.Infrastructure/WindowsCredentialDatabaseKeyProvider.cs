using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Recall.Infrastructure;

public sealed class WindowsCredentialDatabaseKeyProvider : IRecallDatabaseKeyProvider
{
    private const string TargetName = "RecallVault/DatabaseKey/v1";

    public ValueTask<string> GetOrCreateKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Encrypted Recall Vault storage currently requires Windows Credential Manager.");

        if (NativeMethods.CredRead(TargetName, 1, 0, out var credentialPointer))
        {
            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var key = Encoding.UTF8.GetString(bytes);
                if (!IsValidKey(key)) throw new InvalidOperationException("The Recall Vault database credential is malformed.");
                return ValueTask.FromResult(key);
            }
            finally
            {
                NativeMethods.CredFree(credentialPointer);
            }
        }

        var error = Marshal.GetLastWin32Error();
        if (error != 1168) throw new Win32Exception(error, "Unable to read the Recall Vault database credential.");

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var blob = Encoding.UTF8.GetBytes(generated);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = 1,
                TargetName = TargetName,
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
            CryptographicOperations.ZeroMemory(blob);
            handle.Free();
        }

        return ValueTask.FromResult(generated);
    }

    private static bool IsValidKey(string key) =>
        key.Length == 64 && key.All(character => char.IsAsciiHexDigit(character));

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
