using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed class SecureSessionStore(string path)
{
    public static SecureSessionStore ForApplicationDirectory() =>
        new(Path.Combine(ApplicationDirectories.Data, "session.dat"));

    public void Save(AccountSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session));
        var encrypted = Protect(plaintext);
        File.WriteAllBytes(path, encrypted);
        Array.Clear(plaintext);
    }

    public AccountSession? Load()
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AccountSession>(Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(path)))); }
        catch { Clear(); return null; }
    }

    public void Clear() { if (File.Exists(path)) File.Delete(path); }

    private static byte[] Protect(byte[] value) => Crypt(value, true);
    private static byte[] Unprotect(byte[] value) => Crypt(value, false);

    private static byte[] Crypt(byte[] value, bool protect)
    {
        var input = new DataBlob(value);
        try
        {
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!ok) throw new InvalidOperationException("Windows 无法加密登录凭证。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { input.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob : IDisposable
    {
        public int Length;
        public IntPtr Data;
        public DataBlob(byte[] bytes) { Length = bytes.Length; Data = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, Data, bytes.Length); }
        public void Dispose() { if (Data != IntPtr.Zero) { Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; } }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
