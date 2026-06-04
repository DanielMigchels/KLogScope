using System.Windows;

namespace KubeLogViewer;

public class AppSettings
{
    public List<string> KubeconfigPaths { get; set; } = new();
    public string? LastKubeconfigPath   { get; set; }
    public string? LastContext          { get; set; }
    public string? LastNamespace        { get; set; }
    public int     LastTailLines        { get; set; } = 500;
    public double  WindowLeft           { get; set; } = 100;
    public double  WindowTop            { get; set; } = 100;
    public double  WindowWidth          { get; set; } = 1300;
    public double  WindowHeight         { get; set; } = 800;
    public WindowState WindowState      { get; set; } = WindowState.Normal;
}
