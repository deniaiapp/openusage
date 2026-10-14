using System.Runtime.InteropServices;
using System.Text;

namespace OpenUsage.Core.Support;

/// <summary>
/// Reads generic Windows Credential Manager items. Claude Code, Codex, Cursor, and gh
/// store tokens here on Windows the way they use the Keychain on macOS.
/// </summary>
public static class WindowsCredentialStore
{
    public static string? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE.GENERIC, 0, out var ptr))
            return null;
        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public static string? ReadAny(params string[] targets)
    {
        foreach (var target in targets)
        {
            var value = Read(target);
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    public static string? UnwrapGoKeyring(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        const string prefix = "go-keyring-base64:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text[prefix.Length..]));
                return string.IsNullOrWhiteSpace(decoded) ? null : decoded.Trim();
            }
            catch
            {
                return null;
            }
        }
        return text;
    }

    private const int CRED_MAX_CREDENTIAL_BLOB_SIZE = 5 * 512;

    private enum CRED_TYPE : uint { GENERIC = 1 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CRED_TYPE Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, CRED_TYPE type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    // Silence unused-field warning for the blob size constant kept as documentation.
    static WindowsCredentialStore() => _ = CRED_MAX_CREDENTIAL_BLOB_SIZE;
}
