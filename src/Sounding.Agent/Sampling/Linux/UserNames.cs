namespace Sounding.Agent.Sampling;

// The container's user names by uid, from its /etc/passwd, read once; a uid it doesn't list is shown as it is.
public static class UserNames
{
    private static Dictionary<string, string>? _users;

    public static string Of(string uid)
    {
        _users ??= (KernelFile.TryReadAllLines("/etc/passwd") ?? [])
            .Select(l => l.Split(':'))
            .Where(f => f.Length > 2)
            .GroupBy(f => f[2])
            .ToDictionary(g => g.Key, g => g.First()[0]);
        return _users.GetValueOrDefault(uid, uid);
    }
}
