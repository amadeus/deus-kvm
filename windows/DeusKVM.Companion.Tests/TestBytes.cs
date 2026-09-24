namespace DeusKVM.Companion.Tests;

internal static class TestBytes
{
    internal static void CreateSymbolicLink(string link, string target)
    {
#if NETFRAMEWORK
        if (!CreateSymbolicLinkW(link, target, 2))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
#else
        File.CreateSymbolicLink(link, target);
#endif
    }
#if NETFRAMEWORK
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.U1)]
    private static extern bool CreateSymbolicLinkW(string link, string target, int flags);
#endif
    internal static byte[] FromHex(string value) => Enumerable.Range(0, value.Length / 2)
        .Select(i => Convert.ToByte(value.Substring(i * 2, 2), 16)).ToArray();
}
