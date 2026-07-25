// netstandard2.1 does not ship System.Collections.Generic.PriorityQueue<TElement,TPriority>
// (added in .NET 6). k-PERIL's breadth-first search relies on it, so this is a minimal
// binary-min-heap implementation with the same surface used by the algorithm
// (parameterless ctor, Enqueue(element, priority), TryDequeue(out element, out priority)).
// Placed in System.Collections.Generic so the vendored k-PERIL source compiles unchanged.
#if NETSTANDARD2_1
// Enables C# 9+ `init`-only setters on netstandard2.1 (the type is otherwise missing).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace System.Collections.Generic
{
    public class PriorityQueue<TElement, TPriority>
    {
        private readonly List<(TElement Element, TPriority Priority)> _heap = new List<(TElement, TPriority)>();
        private readonly IComparer<TPriority> _comparer = Comparer<TPriority>.Default;

        public int Count => _heap.Count;

        public void Enqueue(TElement element, TPriority priority)
        {
            _heap.Add((element, priority));
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_comparer.Compare(_heap[i].Priority, _heap[parent].Priority) >= 0) break;
                (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
                i = parent;
            }
        }

        public bool TryDequeue(out TElement element, out TPriority priority)
        {
            if (_heap.Count == 0)
            {
                element = default;
                priority = default;
                return false;
            }

            (element, priority) = _heap[0];
            int last = _heap.Count - 1;
            _heap[0] = _heap[last];
            _heap.RemoveAt(last);

            int i = 0;
            int n = _heap.Count;
            while (true)
            {
                int left = 2 * i + 1;
                int right = 2 * i + 2;
                int smallest = i;
                if (left < n && _comparer.Compare(_heap[left].Priority, _heap[smallest].Priority) < 0) smallest = left;
                if (right < n && _comparer.Compare(_heap[right].Priority, _heap[smallest].Priority) < 0) smallest = right;
                if (smallest == i) break;
                (_heap[i], _heap[smallest]) = (_heap[smallest], _heap[i]);
                i = smallest;
            }
            return true;
        }
    }
}
#endif
