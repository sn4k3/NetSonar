using System;
using ObservableCollections;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NetSonar.Avalonia.Extensions;

namespace NetSonar.Avalonia.Settings;

public abstract class RootCollectionFile<T, TO> : RootSettingsFile<T>, IList<TO> where T : RootCollectionFile<T, TO>, new()
{
    /// <summary>
    /// Gets the collection of items contained in the list.
    /// </summary>
    /// <remarks>The returned collection is observable and not null. Changes to the collection, such as adding
    /// or removing items, will raise notifications to observers.</remarks>
    public ObservableList<TO> Items { get; } = [];

    /// <summary>
    /// Gets a value indicating whether to trim the collection when it exceeds a certain size.<br/>
    /// The collection is only trimmed when saving the file.
    /// </summary>
    [JsonIgnore]
    public virtual (int, CollectionSide)? TrimCollectionWhenExceeding => null;

    protected RootCollectionFile()
    {
        Items.CollectionChanged += Items_CollectionChanged;
    }

    protected override void BeforeSave()
    {
        base.BeforeSave();
        if (TrimCollectionWhenExceeding is not null)
        {
            var (maxSize, side) = TrimCollectionWhenExceeding.Value;
            Items.RemoveExceedingAt(maxSize, side);
        }
    }

    private void Items_CollectionChanged(in NotifyCollectionChangedEventArgs<TO> e)
    {
        DebouncedSave(DefaultDebounceSaveMilliseconds);
    }

    public IEnumerator<TO> GetEnumerator()
    {
        return Items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return ((IEnumerable)Items).GetEnumerator();
    }

    public void Add(TO item)
    {
        Items.Add(item);
    }

    public void Clear()
    {
        Items.Clear();
        DebouncedSave();
    }

    public bool Contains(TO item)
    {
        return Items.Contains(item);
    }

    public void CopyTo(TO[] array, int arrayIndex)
    {
        Items.CopyTo(array, arrayIndex);
    }

    public bool Remove(TO item)
    {
        var result = Items.Remove(item);
        DebouncedSave();
        return result;
    }

    public int Count => Items.Count;

    public bool IsReadOnly => false;

    public int IndexOf(TO item)
    {
        return Items.IndexOf(item);
    }

    public void Insert(int index, TO item)
    {
        Items.Insert(index, item);
        DebouncedSave();
    }

    public void RemoveAt(int index)
    {
        Items.RemoveAt(index);
        DebouncedSave();
    }

    public TO this[int index]
    {
        get => Items[index];
        set => Items[index] = value;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        Items.CollectionChanged -= Items_CollectionChanged;
        base.Dispose();
    }
}