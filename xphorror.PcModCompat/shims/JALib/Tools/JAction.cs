using System.Reflection;
using System.Runtime.Serialization;
using JALib.Core;

namespace JALib.Tools;

public class JAction
{
    private readonly JAMod? _mod;
    private readonly Action _action;

    public JAction(JAMod? mod, Action action)
    {
        _mod = mod;
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    internal JAMod? Owner => _mod;
    public MethodInfo Method => _action.Method;
    public object? Target => _action.Target;

    public void Invoke()
    {
        try
        {
            _action();
        }
        catch (Exception exception)
        {
            if (_mod != null)
            {
                _mod.LogReportException(
                    "An error occurred while invoking an action " + _action.Method.Name,
                    exception);
            }
            else
            {
                Console.Error.WriteLine(
                    "[PcModCompat][JALib][JAction] " + exception);
            }
        }
    }

    public IAsyncResult BeginInvoke(AsyncCallback? callback, object? state)
        => _action.BeginInvoke(callback, state);

    public void EndInvoke(IAsyncResult result)
        => _action.EndInvoke(result);

    public object? DynamicInvoke(params object?[]? args)
        => _action.DynamicInvoke(args);

    public Delegate[] GetInvocationList()
        => _action.GetInvocationList();

#pragma warning disable SYSLIB0050
    public void GetObjectData(SerializationInfo info, StreamingContext context)
        => ((ISerializable)_action).GetObjectData(info, context);
#pragma warning restore SYSLIB0050

    public override bool Equals(object? obj)
        => _action.Equals(obj is JAction action ? action._action : obj);

    public override int GetHashCode() => _action.GetHashCode();
    public override string? ToString() => _action.ToString();

    public static bool operator ==(JAction? left, JAction? right)
        => ReferenceEquals(left, right) ||
           left is not null && right is not null && left._action == right._action;

    public static bool operator !=(JAction? left, JAction? right)
        => !(left == right);

    public static implicit operator JAction(Action action)
        => new(null, action);
}
