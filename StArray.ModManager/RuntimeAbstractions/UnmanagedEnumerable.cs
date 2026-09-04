using System.Collections;
using StArray.ModManager.Il2Cpp;
using StArray.ModManager.Mono;

namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>把 managed IEnumerable 包装为 RuntimeObject 序列。</summary>
public unsafe class UnmanagedEnumerable : IEnumerable<RuntimeObject>
{
    private readonly nint _ptr;

    public nint Ptr => _ptr;
    public bool IsValid => _ptr != 0;

    public UnmanagedEnumerable(nint ptr) => _ptr = ptr;
    public UnmanagedEnumerable(RuntimeObject obj) => _ptr = obj.Ptr;

    public static implicit operator UnmanagedEnumerable(nint ptr) => new(ptr);
    public static implicit operator UnmanagedEnumerable(RuntimeObject obj) => new(obj);
    public static implicit operator nint(UnmanagedEnumerable enumerable) => enumerable._ptr;

    public int Count => Unbox<int>(new RuntimeObject(_ptr).Invoke("get_Count", 0));

    public Enumerator GetEnumerator() => new(_ptr);
    IEnumerator<RuntimeObject> IEnumerable<RuntimeObject>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public RuntimeObject?[] ToArray()
    {
        var result = new List<RuntimeObject?>();
        foreach (var item in this)
            result.Add(item.IsValid ? item : null);
        return result.ToArray();
    }

    private static TValue Unbox<TValue>(nint boxed) where TValue : unmanaged
    {
        if (boxed == 0)
            return default;
        var value = RuntimeManager.Backend switch
        {
            RuntimeBackend.Il2Cpp => Il2CppRuntimeApi.Current.ObjectUnbox(boxed),
            RuntimeBackend.Mono => MonoFunctions.MonoObjectUnbox(boxed),
            _ => 0
        };
        return value != 0 ? *(TValue*)value : default;
    }

    public sealed class Enumerator : IEnumerator<RuntimeObject>
    {
        private readonly RuntimeObject _collection;
        private RuntimeObject _enumerator;
        private RuntimeObject _current;
        private bool _started;

        internal Enumerator(nint ptr) => _collection = new RuntimeObject(ptr);

        public RuntimeObject Current => _current;
        object IEnumerator.Current => _current;

        public bool MoveNext() => _started ? MoveNextInner() : MoveFirst();

        private bool MoveFirst()
        {
            _enumerator = _collection.InvokeObject("GetEnumerator", 0) ?? default;
            if (!_enumerator.IsValid)
                return false;
            _started = true;
            return MoveNextInner();
        }

        private bool MoveNextInner()
        {
            if (!Unbox<bool>(_enumerator.Invoke("MoveNext", 0)))
            {
                _current = default;
                return false;
            }

            _current = _enumerator.InvokeObject("get_Current", 0) ?? default;
            return true;
        }

        public void Reset()
        {
            _enumerator = default;
            _current = default;
            _started = false;
        }

        public void Dispose()
        {
            _enumerator = default;
            _current = default;
        }
    }
}

/// <summary>把 managed IEnumerable 包装为 MOD定义的 stub 类型序列。</summary>
public unsafe class UnmanagedEnumerable<T> : IEnumerable<T> where T : UnmanagedObject
{
    private readonly nint _ptr;

    public nint Ptr => _ptr;
    public bool IsValid => _ptr != 0;

    public UnmanagedEnumerable(nint ptr) => _ptr = ptr;
    public UnmanagedEnumerable(RuntimeObject obj) => _ptr = obj.Ptr;
    public UnmanagedEnumerable(UnmanagedEnumerable other) => _ptr = other.Ptr;

    public static implicit operator UnmanagedEnumerable<T>(nint ptr) => new(ptr);
    public static implicit operator UnmanagedEnumerable<T>(RuntimeObject obj) => new(obj);
    public static implicit operator UnmanagedEnumerable<T>(UnmanagedEnumerable other) => new(other);
    public static implicit operator nint(UnmanagedEnumerable<T> enumerable) => enumerable._ptr;
    public static implicit operator UnmanagedEnumerable(UnmanagedEnumerable<T> enumerable)
        => new(enumerable._ptr);

    public int Count => new UnmanagedEnumerable(_ptr).Count;

    public Enumerator GetEnumerator() => new(_ptr);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public T?[] ToArray()
    {
        var result = new List<T?>();
        foreach (var item in new UnmanagedEnumerable(_ptr))
            result.Add(item.IsValid ? (T?)Activator.CreateInstance(typeof(T), item.Ptr) : null);
        return result.ToArray();
    }

    public sealed class Enumerator : IEnumerator<T>
    {
        private readonly UnmanagedEnumerable.Enumerator _inner;

        internal Enumerator(nint ptr) => _inner = new UnmanagedEnumerable(ptr).GetEnumerator();

        public T Current => (T)Activator.CreateInstance(typeof(T), _inner.Current.Ptr)!;
        object IEnumerator.Current => Current;
        public bool MoveNext() => _inner.MoveNext();
        public void Reset() => _inner.Reset();
        public void Dispose() => _inner.Dispose();
    }
}
