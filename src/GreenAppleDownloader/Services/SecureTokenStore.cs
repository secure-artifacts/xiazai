using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GreenAppleDownloader.Models;

namespace GreenAppleDownloader.Services;

public sealed class SecureTokenStore(string appDataDirectory)
{
    private readonly string _tokenPath = Path.Combine(appDataDirectory, "google-token.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GreenAppleDownloader-v1");

    public void Save(GoogleToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(token));
        var encrypted = Protect(plain, Entropy);
        File.WriteAllBytes(_tokenPath, encrypted);
    }

    public GoogleToken? Load()
    {
        try
        {
            if (!File.Exists(_tokenPath))
            {
                return null;
            }

            var encrypted = File.ReadAllBytes(_tokenPath);
            var plain = Unprotect(encrypted, Entropy);
            return JsonSerializer.Deserialize<GoogleToken>(Encoding.UTF8.GetString(plain));
        }
        catch
        {
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_tokenPath))
        {
            File.Delete(_tokenPath);
        }
    }

    private static byte[] Protect(byte[] data, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("安全令牌存储仅支持 Windows。");
        }

        return CryptProtect(data, entropy, protect: true);
    }

    private static byte[] Unprotect(byte[] data, byte[] entropy) => CryptProtect(data, entropy, protect: false);

    private static byte[] CryptProtect(byte[] data, byte[] entropy, bool protect)
    {
        var input = ToBlob(data);
        var optionalEntropy = ToBlob(entropy);
        var output = new DataBlob();
        try
        {
            var ok = protect
                ? CryptProtectData(ref input, null, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 0x1, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref optionalEntropy, IntPtr.Zero, IntPtr.Zero, 0x1, ref output);
            if (!ok)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(optionalEntropy);
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    private static DataBlob ToBlob(byte[] value)
    {
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob { Length = value.Length, Data = pointer };
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
