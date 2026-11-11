using System.Text;

namespace Sounding.Agent.Sampling;

// Reads the small files of /proc, /sys and the cgroup into one reused buffer: a read costs one string
// instead of a stream, a reader and their buffers. Throws like File.ReadAllText.
public static class KernelFile
{
    [ThreadStatic]
    private static byte[]? _buffer;

    public static string ReadAllText(string path)
    {
        using var handle = File.OpenHandle(path);
        var buffer = _buffer ??= new byte[16 * 1024];
        var length = 0;
        int read;
        while ((read = RandomAccess.Read(handle, buffer.AsSpan(length), length)) > 0)
        {
            length += read;
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
                _buffer = buffer;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    public static string[] ReadAllLines(string path)
    {
        var text = ReadAllText(path);
        return (text.EndsWith('\n') ? text[..^1] : text) is { Length: > 0 } body ? body.Split('\n') : [];
    }

    // Null when the file can't be read: a process that exited, another user's, a file this kernel doesn't have.
    public static string? TryReadAllText(string path)
    {
        try
        {
            return ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Null when the file can't be read.
    public static string[]? TryReadAllLines(string path)
    {
        try
        {
            return ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
