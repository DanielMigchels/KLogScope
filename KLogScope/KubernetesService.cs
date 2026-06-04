using System.IO;
using k8s;
using k8s.KubeConfigModels;
using k8s.Models;

namespace KubeLogViewer;

/// <summary>Represents a single context entry shown in the cluster ComboBox.</summary>
public record ClusterItem(string DisplayName, string KubeconfigPath, string ContextName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Thin wrapper around the official KubernetesClient library.
/// All operations are async and accept a CancellationToken.
/// </summary>
public class KubernetesService
{
    private IKubernetes? _client;

    public bool IsConnected => _client != null;

    // ─── Configuration ──────────────────────────────────────────────────────

    /// <summary>Load kubeconfig and connect to the given context.</summary>
    public void Connect(string kubeconfigPath, string contextName)
    {
        var config = KubernetesClientConfiguration.BuildConfigFromConfigFile(
            new FileInfo(kubeconfigPath), contextName);
        _client = new Kubernetes(config);
    }

    /// <summary>Parse a kubeconfig file and return all its named contexts.</summary>
    public static List<ClusterItem> GetContexts(string kubeconfigPath)
    {
        var items    = new List<ClusterItem>();
        var fileName = Path.GetFileName(kubeconfigPath);
        try
        {
            var raw = KubernetesClientConfiguration.LoadKubeConfig(new FileInfo(kubeconfigPath));
            if (raw.Contexts is null) return items;

            foreach (var ctx in raw.Contexts)
            {
                var display = raw.Contexts.Count() > 1
                    ? $"{ctx.Name}  ({fileName})"
                    : ctx.Name;
                items.Add(new ClusterItem(display, kubeconfigPath, ctx.Name));
            }
        }
        catch { /* Invalid/inaccessible kubeconfig – skip */ }
        return items;
    }

    // ─── Core API ────────────────────────────────────────────────────────────

    public async Task<List<string>> GetNamespacesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        var result = await _client!.CoreV1.ListNamespaceAsync(cancellationToken: ct);
        return result.Items.Select(n => n.Metadata.Name).OrderBy(n => n).ToList();
    }

    public async Task<List<string>> GetPodsAsync(string ns, CancellationToken ct = default)
    {
        EnsureConnected();
        var result = await _client!.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct);
        return result.Items
            .Select(p => p.Metadata.Name)
            .OrderBy(n => n)
            .ToList();
    }

    public async Task<List<string>> GetContainersAsync(string ns, string podName, CancellationToken ct = default)
    {
        EnsureConnected();
        var pod = await _client!.CoreV1.ReadNamespacedPodAsync(podName, ns, cancellationToken: ct);
        var containers = pod.Spec.Containers.Select(c => c.Name).ToList();
        if (pod.Spec.InitContainers?.Count > 0)
            containers.AddRange(pod.Spec.InitContainers.Select(c => c.Name));
        return containers;
    }

    /// <summary>
    /// Open a log stream for the given container.
    /// The caller is responsible for disposing the returned Stream.
    /// </summary>
    public async Task<Stream> GetLogStreamAsync(
        string ns,
        string podName,
        string container,
        int?   tailLines,
        bool   follow,
        bool   previous,
        CancellationToken ct)
    {
        EnsureConnected();
        return await _client!.CoreV1.ReadNamespacedPodLogAsync(
            podName,
            ns,
            container:  container,
            follow:     follow,
            tailLines:  tailLines < 0 ? null : tailLines,  // -1 means "All"
            previous:   previous,
            cancellationToken: ct);
    }

    private void EnsureConnected()
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected. Call Connect() first.");
    }
}
