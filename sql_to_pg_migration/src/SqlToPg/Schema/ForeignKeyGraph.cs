using SqlToPg.Safety;

namespace SqlToPg.Schema;

/// <summary>The fill order and the clear order of a set of tables, worked out by <see cref="ForeignKeyGraph.Arrange"/>.</summary>
/// <param name="Fill">Parents before children; the knotted tables come last.</param>
/// <param name="Knotted">Tables caught in (or downstream of) a foreign-key cycle: no safe order exists, so they run last, one at a time.</param>
public sealed record TableOrder(IReadOnlyList<QualifiedName> Fill, IReadOnlyList<QualifiedName> Knotted)
{
    /// <summary>Children before parents, so a delete never strands a referencing row.</summary>
    public IReadOnlyList<QualifiedName> Clear => [.. Fill.Reverse()];
}

/// <summary>
/// The target's foreign keys as a directed graph (child → parent). The order comes from the target's
/// catalogue rather than the mapping, because the database is what will enforce it — a key added by a
/// later migration must be honoured even though no mapping line mentions it.
/// </summary>
public sealed class ForeignKeyGraph
{
    private readonly Dictionary<QualifiedName, HashSet<QualifiedName>> _parents = [];

    public ForeignKeyGraph(IEnumerable<(QualifiedName Child, QualifiedName Parent)> edges)
    {
        foreach (var (child, parent) in edges)
        {
            if (child == parent)
            {
                continue; // a self-reference never orders two tables
            }

            if (!_parents.TryGetValue(child, out var set))
            {
                _parents[child] = set = [];
            }

            set.Add(parent);
        }
    }

    /// <summary>The tables <paramref name="table"/> holds a foreign key into.</summary>
    public IReadOnlyCollection<QualifiedName> ParentsOf(QualifiedName table) =>
        _parents.TryGetValue(table, out var set) ? set : [];

    /// <summary>
    /// Kahn's algorithm restricted to <paramref name="tables"/>: a parent outside the set does not hold
    /// anything back. Ties are broken by name so a run is reproducible.
    /// </summary>
    public TableOrder Arrange(IEnumerable<QualifiedName> tables)
    {
        var members = tables.ToHashSet();
        var waiting = members.ToDictionary(t => t, t => ParentsOf(t).Count(p => members.Contains(p)));
        var children = members.ToDictionary(t => t, _ => new List<QualifiedName>());
        foreach (var t in members)
        {
            foreach (var p in ParentsOf(t).Where(members.Contains))
            {
                children[p].Add(t);
            }
        }

        var ready = new SortedSet<QualifiedName>(waiting.Where(kv => kv.Value == 0).Select(kv => kv.Key), NameOrder.Instance);
        var fill = new List<QualifiedName>(members.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            fill.Add(next);
            foreach (var child in children[next])
            {
                if (--waiting[child] == 0)
                {
                    ready.Add(child);
                }
            }
        }

        var knotted = members.Except(fill).OrderBy(t => t, NameOrder.Instance).ToList();
        fill.AddRange(knotted);
        return new TableOrder(fill, knotted);
    }

    private sealed class NameOrder : IComparer<QualifiedName>
    {
        public static readonly NameOrder Instance = new();

        public int Compare(QualifiedName x, QualifiedName y) => string.CompareOrdinal(x.ToString(), y.ToString());
    }
}
