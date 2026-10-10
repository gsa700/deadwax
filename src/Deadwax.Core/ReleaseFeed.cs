using System.Text.Json;

namespace Deadwax.Core;

/// The two release channels (spec: release model, 2026-10-10). Every build is
/// published as a GitHub pre-release: that is Edge. A build that has run on Edge
/// without trouble is promoted, the same binary with its pre-release flag
/// cleared: that is Stable. GitHub's /releases/latest never returns a
/// pre-release, so Stable reads that; Edge reads the release list and takes the
/// newest version in it, pre-release or not.
public static class ReleaseFeed
{
    /// The newest release in a /releases list by version (VersionOrder), never a
    /// draft; null when the list holds none.
    public static JsonElement? Newest(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;
        JsonElement? best = null;
        string? bestTag = null;
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("tag_name", out var t) || t.GetString() is not { Length: > 0 } tag) continue;
            if (VersionOrder.Compare(tag, tag) is null) continue;   // not a version at all
            if (bestTag is null || VersionOrder.IsNewer(tag, bestTag)) { best = r; bestTag = tag; }
        }
        return best;
    }
}
