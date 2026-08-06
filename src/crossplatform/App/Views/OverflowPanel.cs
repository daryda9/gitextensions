using Avalonia;
using Avalonia.Controls;

namespace GitExtensions.Avalonia.Views;


/// <summary>
///  A single-line toolbar strip that lays its items out left to right and,
///  when they do not all fit, keeps as many as will fit and parks the rest
///  off-screen, pinning an overflow button at the right edge instead — the
///  behaviour of the original Windows toolbar's "»" chevron.
///
///  Items are never hidden through <c>IsVisible</c> (mutating visibility from
///  a measure pass re-invalidates layout); they are arranged outside the
///  panel's clip rectangle, which is cheap and cannot loop.
/// </summary>
internal sealed class OverflowPanel : Panel
{
    internal const string SeparatorTag = "toolbar-separator";

    private readonly Control _overflowButton;

    // Insertion rank per item, so an item removed by SetItemPresent can be put
    // back at its original place on the strip.
    private readonly Dictionary<Control, int> _order = new();
    private int _visibleCount;

    public OverflowPanel(Control overflowButton)
    {
        _overflowButton = overflowButton;
        ClipToBounds = true;
        Children.Add(overflowButton);
    }

    /// <summary>Gap between adjacent items, matching the old StackPanel spacing.</summary>
    public double Spacing { get; set; }

    /// <summary>True while some items are parked in the overflow menu.</summary>
    public bool IsOverflowing { get; private set; }

    /// <summary>The toolbar items, in order, excluding the overflow button.</summary>
    public IEnumerable<Control> Items
        => Children.Where(c => !ReferenceEquals(c, _overflowButton));

    /// <summary>The items the last layout pass could not fit, in order.</summary>
    public IEnumerable<Control> HiddenItems => Items.Skip(_visibleCount);

    /// <summary>Appends a toolbar item, keeping the overflow button last.</summary>
    public void AddItem(Control item)
    {
        _order[item] = _order.Count;
        Children.Insert(Children.Count - 1, item);
    }

    /// <summary>True while <paramref name="item"/> is on the strip.</summary>
    public bool Contains(Control item) => Children.Contains(item);

    /// <summary>
    ///  Takes an item off the strip. Its original position is remembered, so
    ///  <see cref="RestoreItem"/> puts it back where it belongs instead of at the
    ///  end. Removal (rather than <c>IsVisible = false</c>) is deliberate: a
    ///  collapsed-but-present child would still be measured, would still consume
    ///  overflow budget, and would still be listed in the overflow menu.
    /// </summary>
    public void RemoveItem(Control item) => Children.Remove(item);

    /// <summary>Puts a previously removed item back at its original index.</summary>
    public void RestoreItem(Control item)
    {
        if (Children.Contains(item) || !_order.TryGetValue(item, out int rank))
        {
            return;
        }

        // Insert before the first present item that was added after this one; the
        // overflow button is always last, so the fallback lands just before it.
        int at = Children.Count - 1;
        for (int i = 0; i < Children.Count - 1; i++)
        {
            if (Children[i] is Control sibling
                && _order.TryGetValue(sibling, out int siblingRank)
                && siblingRank > rank)
            {
                at = i;
                break;
            }
        }

        Children.Insert(at, item);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (Control child in Children)
        {
            child.Measure(Size.Infinity);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        List<Control> items = Items.ToList();
        double total = 0;
        for (int i = 0; i < items.Count; i++)
        {
            total += items[i].DesiredSize.Width + (i > 0 ? Spacing : 0);
        }

        double available = availableSize.Width;
        if (double.IsInfinity(available) || double.IsNaN(available) || total <= available)
        {
            _visibleCount = items.Count;
            IsOverflowing = false;
            return new Size(total, height);
        }

        // Reserve room for the "»" button, then keep items from the left while
        // they fit; the remainder goes to the overflow menu.
        double budget = Math.Max(0, available - _overflowButton.DesiredSize.Width - Spacing);
        double used = 0;
        int fitting = 0;
        for (int i = 0; i < items.Count; i++)
        {
            double step = items[i].DesiredSize.Width + (i > 0 ? Spacing : 0);
            if (used + step > budget)
            {
                break;
            }

            used += step;
            fitting++;
        }

        // Never end the visible run on a group rule.
        while (fitting > 0 && IsSeparator(items[fitting - 1]))
        {
            fitting--;
        }

        _visibleCount = fitting;
        IsOverflowing = true;
        return new Size(available, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Parked items go far off to the left; the panel clips to its bounds,
        // so they are neither drawn nor hit-testable.
        const double Parked = -10000;

        double x = 0;
        int index = 0;
        foreach (Control item in Items)
        {
            Size desired = item.DesiredSize;
            if (index < _visibleCount)
            {
                item.Arrange(new Rect(x, Center(finalSize.Height, desired.Height), desired.Width, desired.Height));
                x += desired.Width + Spacing;
            }
            else
            {
                item.Arrange(new Rect(Parked, 0, desired.Width, desired.Height));
            }

            index++;
        }

        Size overflowSize = _overflowButton.DesiredSize;
        if (IsOverflowing)
        {
            double ox = Math.Max(x, finalSize.Width - overflowSize.Width);
            _overflowButton.Arrange(new Rect(
                ox, Center(finalSize.Height, overflowSize.Height), overflowSize.Width, overflowSize.Height));
        }
        else
        {
            _overflowButton.Arrange(new Rect(Parked, 0, overflowSize.Width, overflowSize.Height));
        }

        return finalSize;
    }

    private static double Center(double outer, double inner) => Math.Max(0, (outer - inner) / 2);

    private static bool IsSeparator(Control item)
        => item.Tag as string == SeparatorTag;
}
