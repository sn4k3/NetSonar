using ObservableCollections;
using System.Collections.Generic;
using System.Collections.Specialized;


namespace NetSonar.Avalonia.Extensions;

public class ObservableListExtended<T> : ObservableList<T>
{
    public int MaxItemCount { get; set; }

    public ObservableListExtended() : base()
    {
        CollectionChanged += ObservableListExtended_CollectionChanged;
    }

    private void ObservableListExtended_CollectionChanged(in NotifyCollectionChangedEventArgs<T> e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ConstrainItemCount();
        }
    }

    private void ConstrainItemCount()
    {
        if (MaxItemCount <= 0) return;
        var difference = Count - MaxItemCount;
        if (difference <= 0) return;
        RemoveRange(0, difference);
    }

    public ObservableListExtended(IEnumerable<T> collection) : base(collection)
    {
    }
    public ObservableListExtended(List<T> list) : base(list)
    {
    }

    public void RemoveRange(IEnumerable<T> collection)
    {
        foreach (var item in collection)
        {
            Remove(item);
        }
    }

    public void ReplaceRange(IEnumerable<T> collection)
    {
        Clear();
        AddRange(collection);
    }

    public void ReplaceRange(List<T> list)
    {
        Clear();
        AddRange(list);
    }

    public void ReplaceRange(ObservableListExtended<T> list)
    {
        Clear();
        AddRange(list);
    }
}