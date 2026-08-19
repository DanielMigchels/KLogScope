using System.IO;
using System.Diagnostics;
using System.Globalization;
using k8s;
using k8s.KubeConfigModels;
using k8s.Models;

namespace KubeLogViewer;

/// <summary>Represents a single context entry shown in the cluster ComboBox.</summary>
public record ClusterItem(string DisplayName, string KubeconfigPath, string ContextName)
{
    public override string ToString() => DisplayName;
}

public record NodeTopMetric(
    string Name,
    string CpuCores,
    double? CpuPercent,
    string MemoryBytes,
    double? MemoryPercent)
{
    public string CpuPercentDisplay => CpuPercent.HasValue ? $"{CpuPercent.Value:0.#}%" : "n/a";
    public string MemoryPercentDisplay => MemoryPercent.HasValue ? $"{MemoryPercent.Value:0.#}%" : "n/a";
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

    public async Task<List<NodeTopMetric>> GetTopNodesAsync(
        string kubeconfigPath,
        string contextName,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "kubectl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("--kubeconfig");
        psi.ArgumentList.Add(kubeconfigPath);
        psi.ArgumentList.Add("--context");
        psi.ArgumentList.Add(contextName);
        psi.ArgumentList.Add("top");
        psi.ArgumentList.Add("nodes");
        psi.ArgumentList.Add("--no-headers");

        using var process = new Process { StartInfo = psi };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start kubectl process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
                ? $"kubectl exited with code {process.ExitCode}."
                : reason.Trim());
        }

        return ParseTopNodes(stdout);
    }

    private static List<NodeTopMetric> ParseTopNodes(string stdout)
    {
        var results = new List<NodeTopMetric>();
        if (string.IsNullOrWhiteSpace(stdout))
            return results;

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Replace("\r", string.Empty);
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                continue;

            results.Add(new NodeTopMetric(
                parts[0],
                parts[1],
                ParsePercent(parts[2]),
                parts[3],
                ParsePercent(parts[4])));
        }

        return results.OrderBy(n => n.Name).ToList();
    }

    private static double? ParsePercent(string raw)
    {
        var value = raw.Trim().TrimEnd('%');
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private void EnsureConnected()
    {
        if (_client is null)
            throw new InvalidOperationException("Not connected. Call Connect() first.");
    }
}
