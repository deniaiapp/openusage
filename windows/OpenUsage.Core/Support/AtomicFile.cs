namespace OpenUsage.Core.Support;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents) =>
        Replace(path, temporary => File.WriteAllText(temporary, contents));

    public static void WriteAllBytes(string path, byte[] contents) =>
        Replace(path, temporary => File.WriteAllBytes(temporary, contents));

    private static void Replace(string path, Action<string> write)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            write(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
}
