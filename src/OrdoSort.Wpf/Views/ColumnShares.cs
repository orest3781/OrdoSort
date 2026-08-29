namespace OrdoSort.Wpf.Views;

/// <summary>The arithmetic behind <see cref="DataGridColumnCap"/>'s autofit:
/// how much of the width a grid has left each of its text columns gets.
/// Pure, so it is provable without a DataGrid — the WPF half of the rule
/// lives in DataGridColumnCap, this is the part a person can check with a
/// calculator.</summary>
internal static class ColumnShares
{
    /// <summary>One share per column, in the order given.
    ///
    /// When every column's wanted width — its natural (content) width, or
    /// its floor if that is larger — fits in <paramref name="available"/>,
    /// each column gets exactly that: content-sized, nothing wraps. When
    /// they don't fit, the width is split in proportion to wanted width,
    /// and a column whose proportional share would fall under its floor is
    /// held AT the floor with the rest re-split among the others — so a
    /// long message wraps rather than squeezing a file name below the
    /// floor its window declared. Floors are honoured even when they alone
    /// exceed the width; that overflow is WPF's to resolve (a horizontal
    /// scrollbar), and every window's own MinWidth is what makes it
    /// unreachable in practice.</summary>
    /// <param name="available">Width the columns may use between them.</param>
    /// <param name="natural">Each column's content width.</param>
    /// <param name="floors">Each column's MinWidth; same length as <paramref name="natural"/>.</param>
    /// <exception cref="ArgumentException">The two lists differ in length.</exception>
    public static double[] Compute(double available, IReadOnlyList<double> natural, IReadOnlyList<double> floors)
    {
        if (natural.Count != floors.Count)
            throw new ArgumentException(
                $"{natural.Count} natural widths against {floors.Count} floors", nameof(floors));

        var count = natural.Count;
        var wanted = new double[count];
        for (var i = 0; i < count; i++) wanted[i] = Math.Max(natural[i], floors[i]);

        var shares = new double[count];
        if (wanted.Sum() <= available)
        {
            Array.Copy(wanted, shares, count);
            return shares;
        }

        // Proportional split with floors. Holding a column at its floor
        // changes what is left for the others, which can push another
        // column under ITS floor, so this repeats until a pass holds nobody
        // new — at most one pass per column.
        var held = new bool[count];
        while (true)
        {
            var remaining = available;
            var pool = 0.0;
            for (var i = 0; i < count; i++)
            {
                if (held[i]) remaining -= floors[i];
                else pool += wanted[i];
            }

            var newlyHeld = false;
            for (var i = 0; i < count; i++)
            {
                if (held[i])
                {
                    shares[i] = floors[i];
                    continue;
                }
                shares[i] = pool > 0 ? remaining * wanted[i] / pool : floors[i];
                if (shares[i] < floors[i])
                {
                    held[i] = true;
                    newlyHeld = true;
                }
            }
            if (!newlyHeld) return shares;
        }
    }
}
