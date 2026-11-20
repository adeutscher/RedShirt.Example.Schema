namespace RedShirt.Example.Schema;

/// <summary>
///     Reads a password from a file, for example a mounted Docker or Kubernetes secret. Environment variables are visible
///     to anyone who can run <c>docker inspect</c> on the container or read the process environment, a file's contents
///     are not.
/// </summary>
internal static class PasswordFile
{
    /// <summary>
    ///     Reads the whole file and drops one trailing line ending, which editors and <c>echo</c> usually add. Any other
    ///     whitespace is kept as part of the password.
    /// </summary>
    public static string Read(string path)
    {
        var contents = File.ReadAllText(path);
        if (contents.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return contents[..^2];
        }

        return contents.EndsWith('\n') ? contents[..^1] : contents;
    }
}