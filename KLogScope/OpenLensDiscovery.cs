using System.IO;

namespace KubeLogViewer;

/// <summary>
/// Looks for kubeconfig files in well-known locations:
/// ~/.kube/config, OpenLens/Lens app data directories.
/// </summary>
public static class OpenLensDiscovery
{
    public static List<string> DiscoverKubeconfigPaths()
    {
        var paths = new List<string>();

        // Standard ~/.kube/config
        var standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kube", "config");
        if (File.Exists(standard))
            paths.Add(standard);

        var appData      = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // OpenLens / Lens kubeconfigs directories
        var candidateDirs = new[]
        {
            Path.Combine(appData,      "OpenLens", "kubeconfigs"),
            Path.Combine(appData,      "Lens",     "kubeconfigs"),
            Path.Combine(localAppData, "OpenLens", "kubeconfigs"),
            Path.Combine(localAppData, "Lens",     "kubeconfigs"),
            // Older Lens stores individual per-cluster configs here
            Path.Combine(appData, "Lens", "lens-local-storage"),
        };

        foreach (var dir in candidateDirs)
            TryAddFilesFromDir(paths, dir);

        return paths;
    }

    private static void TryAddFilesFromDir(List<string> paths, string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                if (!paths.Contains(file))
                    paths.Add(file);
            }
        }
        catch { /* Ignore access errors */ }
    }
}
