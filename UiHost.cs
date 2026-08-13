using System.Reflection;

namespace ZomboidManager;

/// <summary>
/// Ships the Web UI inside the EXE and materializes it under LocalAppData.
/// That way an update only needs to replace the EXE — no ZIP for end users.
/// </summary>
internal static class UiHost
{
    private const string ResourcePrefix = "ui/wwwroot/";

    private static readonly string UiRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZomboidManager",
        "ui");

    public static string EnsureContentRoot(string appVersion)
    {
#if DEBUG
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string nested = Path.Combine(baseDir, "app");
        if (File.Exists(Path.Combine(nested, "wwwroot", "index.html")))
            return nested;
        if (File.Exists(Path.Combine(baseDir, "wwwroot", "index.html")))
            return baseDir;
#endif

        string version = string.IsNullOrWhiteSpace(appVersion) ? "0" : appVersion.Trim();
        string markerPath = Path.Combine(UiRoot, ".ui-version");
        string indexPath = Path.Combine(UiRoot, "wwwroot", "index.html");

        bool upToDate = File.Exists(indexPath)
            && File.Exists(markerPath)
            && string.Equals(File.ReadAllText(markerPath).Trim(), version, StringComparison.Ordinal);

        if (!upToDate)
            ExtractEmbeddedUi(version);

        if (!File.Exists(indexPath))
            throw new FileNotFoundException("UI could not be prepared from embedded resources.", indexPath);

        return UiRoot;
    }

    private static void ExtractEmbeddedUi(string version)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string[] resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToArray();

        if (resources.Length == 0)
            throw new InvalidOperationException("No embedded UI resources found (prefix '" + ResourcePrefix + "').");

        Directory.CreateDirectory(UiRoot);

        // Remove previous wwwroot so deleted files do not linger across versions.
        string www = Path.Combine(UiRoot, "wwwroot");
        if (Directory.Exists(www))
            Directory.Delete(www, recursive: true);

        foreach (string resourceName in resources)
        {
            string relative = resourceName[ResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(relative))
                continue;

            string destPath = Path.Combine(www, relative);
            string? destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            using Stream? input = assembly.GetManifestResourceStream(resourceName);
            if (input is null)
                continue;

            using FileStream output = File.Create(destPath);
            input.CopyTo(output);
        }

        File.WriteAllText(Path.Combine(UiRoot, ".ui-version"), version);
    }
}
