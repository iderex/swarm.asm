using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Swarm.Tests;

/// <summary>
/// Reads <c>.github/dependabot.yml</c> as YAML rather than as lines.
///
/// This exists because indentation is not scope in YAML and a line scanner
/// cannot establish that a line is a mapping key. Three generations of a
/// hand-rolled scanner (PR #140) each read a decoy inside a block scalar as
/// configuration, read a duplicated key as first-wins, and rejected valid
/// spellings of the correct policy. A loader decides all three by
/// construction, so this type is a thin projection over one and holds no
/// parsing logic of its own.
///
/// Absent is reported as absent. <see cref="Cooldown"/> has no defaults in it:
/// a key the file does not carry is missing from the dictionary rather than
/// answering with the value the updater would have used.
/// </summary>
public sealed class DependabotConfig
{
    /// <summary>One <c>updates:</c> entry, projected to what the policy asserts about it.</summary>
    public sealed class Entry
    {
        public required string Ecosystem { get; init; }
        public required string Directory { get; init; }

        /// <summary>Absent when the entry carries no <c>cooldown:</c> block at all.</summary>
        public IReadOnlyDictionary<string, string>? Cooldown { get; init; }

        public override string ToString() => $"{Ecosystem} {Directory}";
    }

    public required IReadOnlyList<Entry> Updates { get; init; }

    /// <summary>
    /// Parses <paramref name="yaml"/>. Returns null when the document is not
    /// well-formed YAML or is not shaped like a dependabot config, with the
    /// reason in <paramref name="error"/>. A duplicated mapping key is one of
    /// those cases: the loader refuses the document instead of reading
    /// first-wins.
    /// </summary>
    public static DependabotConfig? TryParse(string yaml, out string error)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException)
        {
            error = $"not well-formed YAML: {ex.Message}";
            return null;
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            error = "expected exactly one YAML document with a mapping at its root";
            return null;
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("updates"), out var updatesNode)
            || updatesNode is not YamlSequenceNode updates)
        {
            error = "no `updates:` sequence at the root";
            return null;
        }

        var entries = new List<Entry>();
        foreach (var item in updates)
        {
            if (item is not YamlMappingNode entry)
            {
                error = "an `updates:` item is not a mapping";
                return null;
            }

            var ecosystem = Scalar(entry, "package-ecosystem");
            var directory = Scalar(entry, "directory");
            if (ecosystem is null || directory is null)
            {
                error = "an `updates:` item is missing `package-ecosystem:` or `directory:`";
                return null;
            }

            IReadOnlyDictionary<string, string>? cooldown = null;
            if (entry.Children.TryGetValue(new YamlScalarNode("cooldown"), out var cooldownNode))
            {
                if (cooldownNode is not YamlMappingNode cooldownMap)
                {
                    error = $"the `cooldown:` of {ecosystem} {directory} is not a mapping";
                    return null;
                }

                var kv = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in cooldownMap.Children)
                {
                    if (key is not YamlScalarNode { Value: { } name })
                    {
                        error = $"a `cooldown:` key of {ecosystem} {directory} is not a scalar";
                        return null;
                    }

                    kv[name] = value is YamlScalarNode { Value: { } v } ? v : "<not a scalar>";
                }

                cooldown = kv;
            }

            entries.Add(new Entry { Ecosystem = ecosystem, Directory = directory, Cooldown = cooldown });
        }

        error = "";
        return new DependabotConfig { Updates = entries };
    }

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node)
        && node is YamlScalarNode { Value: { } value }
            ? value
            : null;
}
