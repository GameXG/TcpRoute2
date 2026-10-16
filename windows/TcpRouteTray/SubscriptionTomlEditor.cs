using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TcpRouteTray;

internal sealed record SubscriptionIdentity(string SourceId, string NodeId, string NodeName = "", bool Delete = false, bool Remove = false);
internal sealed record SubscriptionTomlResult(string Content, SubscriptionIdentity[] Selections);
internal sealed record SubscriptionTomlChange(string Path, string Before, string After);
internal sealed record SubscriptionOwnership(string SourceId, string Prefix, Dictionary<string, string> Nodes, string NameIdPrefix = "name:", bool PoolKnown = false, string[]? AvailableNodes = null);

internal static class SubscriptionTomlEditor
{
    public static SubscriptionOwnership[] Ownership(IEnumerable<SubscriptionSource> sources, SubscriptionManager? manager = null) =>
        sources.SelectMany(source => {
            var cache = manager?.Load(source) ?? new SubscriptionCache();
            var nodes = cache.Nodes.ToDictionary(node => node.Id, node => node.Name, StringComparer.Ordinal);
            return cache.ManagedPrefixes.Append(source.ManagedPrefix).Distinct(StringComparer.Ordinal).Select(prefix => new SubscriptionOwnership(source.Id, prefix, nodes, PoolKnown: cache.UpdatedAt != default || cache.Nodes.Count != 0, AvailableNodes: cache.Nodes.Where(node => !node.Removed).Select(node => node.Id).ToArray()));
        }).ToArray();

    public static async Task<SubscriptionTomlResult> ProtectAsync(string executable, TcpRouteConfig profile, IEnumerable<SubscriptionSource> sources, SubscriptionManager manager)
    {
        var configured = sources.ToArray();
        var owners = Ownership(configured, manager);
        var snapshot = await EditAsync(executable, profile.Content, [], [], owners, includeRemoved: true).ConfigureAwait(false);
        var removals = new List<SubscriptionIdentity>();
        foreach (var source in configured)
        {
            var nodes = manager.Load(source).Nodes.Where(node => !node.Removed).ToArray();
            var exclusions = source.ExcludedNodesByProfile.GetValueOrDefault(profile.Id, []).ToHashSet(StringComparer.Ordinal);
            var excluded = nodes.Where(node => exclusions.Contains(node.Id) || exclusions.Contains(node.Name)).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            removals.AddRange(snapshot.Selections.Where(node => node.SourceId == source.Id && excluded.Contains(node.NodeId)).Select(node => node with { Delete = true }));
        }
        return await EditAsync(executable, profile.Content, [], removals.ToArray(), owners, protectMissing: true).ConfigureAwait(false);
    }

    public static async Task<SubscriptionTomlChange[]> PrepareSyncAsync(string executable, SubscriptionSource source, string activeProfile, IEnumerable<TcpRouteConfig> profiles, List<SubscriptionNode> nodes, List<SubscriptionNode>? previousNodes = null, SubscriptionOwnership[]? sources = null)
    {
        sources ??= Ownership([source]);
        var names = (previousNodes ?? []).Concat(nodes).GroupBy(node => node.Name, StringComparer.Ordinal).Select(group => group.First()).ToDictionary(node => node.Id, node => node.Name, StringComparer.Ordinal);
        sources = sources.Select(owner => owner.SourceId == source.Id ? owner with { Nodes = owner.Nodes.Concat(names.Where(pair => !owner.Nodes.ContainsKey(pair.Key))).ToDictionary(pair => pair.Key, pair => pair.Value) } : owner).ToArray();
        var changes = new List<SubscriptionTomlChange>();
        foreach (var profile in profiles)
        {
            var snapshot = await EditAsync(executable, profile.Content, [], [], sources, includeRemoved: true).ConfigureAwait(false);
            var ownership = snapshot.Selections.Select(node => node.SourceId == source.Id && node.NodeName == "" && previousNodes?.FirstOrDefault(old => old.Id == node.NodeId) is { } oldNode
                ? node with { NodeName = oldNode.Name } : node).ToArray();
            SubscriptionManager.NormalizeExclusions(source, ownership.Where(node => node.SourceId == source.Id && node.NodeName != "").Select(node => new SubscriptionNode { Id = node.NodeId, Name = node.NodeName }).Concat(previousNodes ?? []).Concat(nodes), profile.Id);
            if (profile.Id != activeProfile && !ownership.Any(node => node.SourceId == source.Id)) continue;
            var plan = SubscriptionManager.PlanSync(source, profile.Id, nodes, ownership);
            var next = await EditAsync(executable, profile.Content, plan.Imports, plan.Removals, sources, migrate: true).ConfigureAwait(false);
            if (next.Content != profile.Content) changes.Add(new(profile.Path, profile.Content, next.Content));
        }
        return changes.ToArray();
    }
    public static async Task<SubscriptionTomlChange[]> PrepareSourceRemovalAsync(string executable, IEnumerable<TcpRouteConfig> profiles, HashSet<string> sourceIds, SubscriptionOwnership[] sources)
    {
        if (sourceIds.Count == 0) return [];
        var changes = new List<SubscriptionTomlChange>();
        foreach (var profile in profiles)
        {
            var snapshot = await EditAsync(executable, profile.Content, [], [], sources).ConfigureAwait(false);
            var removals = snapshot.Selections.Where(node => sourceIds.Contains(node.SourceId)).ToArray();
            if (removals.Length == 0) continue;
            var next = await EditAsync(executable, profile.Content, [], removals, sources).ConfigureAwait(false);
            changes.Add(new(profile.Path, profile.Content, next.Content));
        }
        return changes.ToArray();
    }

    public static void ApplyChanges(IReadOnlyList<SubscriptionTomlChange> changes)
    {
        var applied = new List<SubscriptionTomlChange>();
        try {
            foreach (var change in changes) { WriteIfUnchanged(change.Path, change.Before, change.After); applied.Add(change); }
        }
        catch { RollbackChanges(applied); throw; }
    }

    public static void RollbackChanges(IEnumerable<SubscriptionTomlChange> changes)
    {
        foreach (var change in changes.Reverse())
            if (File.Exists(change.Path) && File.ReadAllText(change.Path) == change.After) WriteIfUnchanged(change.Path, change.After, change.Before);
    }

    public static async Task<SubscriptionTomlResult> EditAsync(string executable, string content, object[] imports, SubscriptionIdentity[] removals, SubscriptionOwnership[]? sources = null, bool includeRemoved = false, bool migrate = false, bool protectMissing = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(executable) {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--subscription-toml");
        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Content = content, Imports = imports, Removals = removals, Sources = sources ?? [], IncludeRemoved = includeRemoved, Migrate = migrate, ProtectMissing = protectMissing }).AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            var detail = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException("Subscription TOML edit failed: " + detail.Trim());
            try { return JsonSerializer.Deserialize<SubscriptionTomlResult>(text) ?? throw new InvalidDataException("Core returned no subscription edit result."); }
            catch (JsonException) { throw new InvalidDataException("The selected core does not support subscription TOML editing. Use the matching TcpRoute2 core."); }
        }
        catch
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); }
            throw;
        }
    }

    public static void WriteIfUnchanged(string path, string expected, string content)
    {
        Encoding encoding;
        using (var reader = new StreamReader(path, new UTF8Encoding(false), true))
        {
            if (reader.ReadToEnd() != expected) throw new IOException("The TOML file changed during subscription editing; retry the operation.");
            encoding = reader.CurrentEncoding;
        }
        var temporary = path + ".subscription-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, encoding);
            if (File.ReadAllText(path) != expected) throw new IOException("The TOML file changed during subscription editing; retry the operation.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
