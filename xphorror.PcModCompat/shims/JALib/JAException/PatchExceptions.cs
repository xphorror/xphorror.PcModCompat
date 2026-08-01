namespace JALib.JAException;

public class AlreadyWorkedException(string message) : Exception(message);

public class PacketRunningException : Exception
{
    public PacketRunningException(string message)
        : base(message)
    {
    }

    public PacketRunningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class PatchParameterException(string message) : Exception(message);

public class PatchReturnException(Type original, Type current)
    : Exception($"Patch return type mismatch: {original.FullName} -> {current.FullName}");
