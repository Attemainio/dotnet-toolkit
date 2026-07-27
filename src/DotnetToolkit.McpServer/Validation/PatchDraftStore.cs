using DotnetToolkit.McpServer.Identity;

using Microsoft.CodeAnalysis.Text;

namespace DotnetToolkit.McpServer.Validation;

/// <summary>
/// A validated-but-unapplied patch, retained so a follow-up call can correct a few of its lines instead
/// of resubmitting the whole edit.
/// </summary>
/// <param name="BaseVersions">The symbolId to contentVersion map the original patch was built against; every amend of this draft inherits it.</param>
/// <param name="Proposed">Absolute file path to the text the patch produced. This is the coordinate space an amend's line spans address.</param>
/// <param name="Baseline">Absolute file path to the workspace text the patch forked from, used to detect that a file moved underneath the draft.</param>
public sealed record PatchDraft(
    IReadOnlyDictionary<string, string> BaseVersions,
    IReadOnlyDictionary<string, SourceText> Proposed,
    IReadOnlyDictionary<string, SourceText> Baseline)
{
    /// <summary>The id this draft is stored under.</summary>
    /// <value>Assigned by <see cref="PatchDraftStore.Put"/>; empty on a draft that has not been stored.</value>
    public string Id { get; init; } = "";

    /// <summary>The instant after which this draft can no longer be amended.</summary>
    /// <value>Assigned by <see cref="PatchDraftStore.Put"/>; the value on an unstored draft is meaningless.</value>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Holds recently validated-but-unapplied patches so a failed <c>validate_patch</c> can be corrected with
/// a small amend rather than a full resubmission.
/// </summary>
/// <remarks>
/// Deliberately in-memory rather than part of the SQLite store: a draft describes a fork of the currently
/// loaded workspace, so it is meaningless once that workspace is gone, and <c>Store/</c>'s tables are
/// append-only by design. Bounded in both count and time so a long session cannot accumulate whole-file
/// copies indefinitely.
/// </remarks>
public sealed class PatchDraftStore
{
    /// <summary>How many drafts are retained before the oldest is evicted.</summary>
    public const int Capacity = 8;

    /// <summary>How long a draft stays amendable after it is stored.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly Dictionary<string, PatchDraft> _drafts = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>Creates a store that measures draft lifetime against <paramref name="clock"/>.</summary>
    /// <param name="clock">Time source; tests substitute a fake one to exercise expiry without waiting.</param>
    public PatchDraftStore(TimeProvider clock) => _clock = clock;

    /// <summary>Stores <paramref name="draft"/> under a freshly minted id, evicting the oldest draft if full.</summary>
    /// <param name="draft">The draft to retain. Its <see cref="PatchDraft.Id"/> and <see cref="PatchDraft.ExpiresAt"/> are assigned here.</param>
    /// <returns>The stored draft, carrying the id an amend call passes back and the deadline it must beat.</returns>
    public PatchDraft Put(PatchDraft draft)
    {
        var stored = draft with { Id = Ids.Draft(), ExpiresAt = _clock.GetUtcNow() + Lifetime };
        lock (_gate)
        {
            PurgeExpired();
            if (_drafts.Count >= Capacity)
            {
                // Lifetime is uniform, so the earliest expiry is also the least recently stored draft.
                var oldest = _drafts.OrderBy(kv => kv.Value.ExpiresAt).First().Key;
                _drafts.Remove(oldest);
            }

            _drafts[stored.Id] = stored;
        }

        return stored;
    }

    /// <summary>Looks up a draft by id.</summary>
    /// <param name="draftId">An id previously returned by <see cref="Put"/>.</param>
    /// <returns>The draft, or null if it never existed, has expired, or was evicted to stay within <see cref="Capacity"/>.</returns>
    public PatchDraft? Get(string draftId)
    {
        lock (_gate)
        {
            PurgeExpired();
            return _drafts.GetValueOrDefault(draftId);
        }
    }

    /// <summary>Drops a draft, used when it is found no longer to match the workspace it forked from.</summary>
    /// <param name="draftId">The id to forget. Unknown ids are ignored.</param>
    public void Remove(string draftId)
    {
        lock (_gate)
        {
            _drafts.Remove(draftId);
        }
    }

    private void PurgeExpired()
    {
        var now = _clock.GetUtcNow();
        var expired = _drafts.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
        foreach (var id in expired)
        {
            _drafts.Remove(id);
        }
    }
}
