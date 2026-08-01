namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>由 native managed-object 指针支持的 MOD stub 基类。</summary>
public abstract class UnmanagedObject
{
    public nint Ptr { get; }

    protected UnmanagedObject(nint ptr) => Ptr = ptr;

    public T Field<T>(string name) where T : unmanaged
        => new RuntimeObject(Ptr).GetField<T>(name);

    protected static T? Wrap<T>(nint ptr) where T : UnmanagedObject
        => ptr != 0 ? (T?)Activator.CreateInstance(typeof(T), ptr) : null;
}
