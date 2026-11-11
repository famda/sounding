using System.Runtime.InteropServices;

namespace Sounding.Agent.Sampling;

public static class StatVfs
{
    public static (long TotalBytes, long FreeBytes, long InodesTotal, long InodesFree)? Read(string path)
    {
        try
        {
            if (Statvfs(path, out var result) != 0)
            {
                return null;
            }

            var block = (long)(result.FragmentSize != 0 ? result.FragmentSize : result.BlockSize);
            var inodesKnown = result.Files > 0 && result.FilesFree <= result.Files;
            return ((long)result.Blocks * block, (long)result.BlocksAvailable * block,
                inodesKnown ? (long)result.Files : 0, inodesKnown ? (long)result.FilesFree : 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct StatvfsResult
    {
        public ulong BlockSize;
        public ulong FragmentSize;
        public ulong Blocks;
        public ulong BlocksFree;
        public ulong BlocksAvailable;
        public ulong Files;
        public ulong FilesFree;
        public ulong FilesAvailable;
    }

    [DllImport("libc", EntryPoint = "statvfs")]
    private static extern int Statvfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out StatvfsResult result);
}
