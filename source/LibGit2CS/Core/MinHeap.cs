// Copyright (C) the libgit2 contributors. All rights reserved.
// C# adaptation Copyright (c) 2026 LibGit2CS contributors.
// Licensed under GNU GPL v2 only with the libgit2 linking exception.
// See LICENSE and THIRD-PARTY-NOTICES.txt at the repository root.
// Adapted from libgit2 v1.9.4 (f7164261c9bc0a7e0ebf767c584e5192810a8b24).

using System.Diagnostics;

namespace LibGit2CS.Core;

/// <summary>
/// Binary min-heap with optional fixed-size mode, indexable access, and array
/// reversal. Managed port of libgit2's <c>src/util/pqueue.c</c>
/// (<c>git_pqueue</c>).
/// </summary>
/// <remarks>
/// <para>
/// libgit2's <c>git_pqueue</c> is a <c>git_vector</c> with heap operations layered
/// on top. When a comparator is provided, <see cref="Insert"/> bubbles up and
/// <see cref="Pop"/> removes the minimum. When the comparator is <c>null</c>, it
/// degrades to a simple vector: <see cref="Insert"/> appends, <see cref="Pop"/>
/// removes from the end (LIFO).
/// </para>
/// <para>
/// <b>Why not use BCL <c>PriorityQueue&lt;TElement,TPriority&gt;</c>?</b> The BCL
/// type lacks <see cref="Reverse"/> (used by revwalk's non-time topo sort to
/// flip FIFO to LIFO) and indexable access via <see cref="this[int]"/> (used by
/// <c>graph.c</c>'s <c>interesting()</c> to scan all elements without popping).
/// </para>
/// <para>
/// Consumers: <c>revwalk.c</c> (topological sort queue, time-sort queue),
/// <c>graph.c</c> (ahead-behind computation), <c>merge.c</c> (merge base
/// computation).
/// </para>
/// </remarks>
internal sealed class MinHeap<T> where T : class
{
    private readonly Comparison<T>? _comparison;
    private readonly bool _fixedSize;
    private readonly int _maxSize;
    private T?[] _items;
    private int _count;

    /// <summary>
    /// Creates a min-heap with the given comparison.
    /// </summary>
    /// <param name="comparison">Comparison delegate. When <c>null</c>, the heap
    /// degrades to a LIFO list (insert appends, pop takes from end).</param>
    /// <param name="initialCapacity">Initial backing array capacity.</param>
    /// <param name="fixedSize">When <c>true</c>, keeps only the <paramref name="initialCapacity"/> smallest items.</param>
    public MinHeap(Comparison<T>? comparison, int initialCapacity = 8, bool fixedSize = false)
    {
        _comparison = comparison;
        _fixedSize = fixedSize;
        _maxSize = initialCapacity;
        _items = new T?[Math.Max(initialCapacity, 4)];
        _count = 0;
    }

    /// <summary>The number of items in the heap.</summary>
    public int Count => _count;

    /// <summary>
    /// Index-based access to the underlying array. Used by <c>graph.c</c> to
    /// scan elements without popping. The heap property is not maintained by
    /// this accessor — it exposes raw storage.
    /// </summary>
    public T? this[int index] => (uint)index < (uint)_count ? _items[index] : null;

    /// <summary>
    /// Inserts an item into the heap. With a comparator, bubbles up to maintain
    /// the min-heap property. Without a comparator, appends to the end.
    /// </summary>
    public void Insert(T item)
    {
        if (_comparison is { } comparison && _fixedSize && _count >= _maxSize)
        {
            // Fixed-size mode: skip if item is not better than the current min.
            T? currentMin = _items[0];
            Debug.Assert(currentMin is not null, "Fixed-size heap with _count >= _maxSize must have a min at index 0.");
            if (comparison(item, currentMin) <= 0)
            {
                return;
            }

            _ = Pop();
        }

        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[_count] = item;

        if (_comparison is { } cmp)
        {
            SiftUp(_count, cmp);
        }

        _count++;
    }

    /// <summary>
    /// Removes and returns the minimum element (or the last element when no
    /// comparator is set). Returns <c>null</c> if the heap is empty.
    /// </summary>
    public T? Pop()
    {
        if (_count == 0)
        {
            return null;
        }

        if (_comparison is null)
        {
            // No comparator: take from the end (LIFO behavior).
            T? last = _items[--_count];
            _items[_count] = null;
            Debug.Assert(last is not null, "Pop on a non-empty heap must yield a non-null element.");
            return last;
        }
        else
        {
            // Min-heap: take from the top (index 0).
            T? top = _items[0];
            Debug.Assert(top is not null, "Pop on a non-empty heap must yield a non-null element.");

            if (_count > 1)
            {
                _items[0] = _items[--_count];
                _items[_count] = null;
                SiftDown(0, _comparison);
            }
            else
            {
                _count = 0;
            }

            return top;
        }
    }

    /// <summary>
    /// Reverses the underlying array. Used by revwalk when not time-sorting to
    /// flip the insertion order from FIFO to LIFO. After reversal, the heap
    /// property is intentionally violated — callers must use <see cref="Pop"/>
    /// in no-comparator mode (which takes from the end).
    /// </summary>
    public void Reverse()
    {
        Array.Reverse(_items, 0, _count);
    }

    /// <summary>Removes all items from the heap.</summary>
    public void Clear()
    {
        Array.Clear(_items, 0, _count);
        _count = 0;
    }

    private void SiftUp(int index, Comparison<T> comparison)
    {
        T? kid = _items[index];
        Debug.Assert(kid is not null, "SiftUp index must reference a populated slot.");
        while (index > 0)
        {
            int parentIndex = (index - 1) >> 1;
            T? parent = _items[parentIndex];
            Debug.Assert(parent is not null, "SiftUp parent index must reference a populated slot.");

            if (comparison(parent, kid) <= 0)
            {
                break;
            }

            _items[index] = parent;
            index = parentIndex;
        }

        _items[index] = kid;
    }

    private void SiftDown(int index, Comparison<T> comparison)
    {
        T? parent = _items[index];
        Debug.Assert(parent is not null, "SiftDown index must reference a populated slot.");
        while (true)
        {
            int kidIndex = (index << 1) + 1;
            if (kidIndex >= _count)
            {
                break;
            }

            T? kid = _items[kidIndex];
            Debug.Assert(kid is not null, "SiftDown kid index must reference a populated slot.");
            int rightIndex = kidIndex + 1;
            if (rightIndex < _count)
            {
                T? right = _items[rightIndex];
                Debug.Assert(right is not null, "SiftDown right index must reference a populated slot.");
                if (comparison(kid, right) > 0)
                {
                    kid = right;
                    kidIndex = rightIndex;
                }
            }

            if (comparison(parent, kid) <= 0)
            {
                break;
            }

            _items[index] = kid;
            index = kidIndex;
        }

        _items[index] = parent;
    }
}
