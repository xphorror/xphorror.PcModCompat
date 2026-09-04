using dnlib.DotNet;
using dnlib.DotNet.Emit;

internal enum ReflectedMemberKind
{
    Field,
    Property,
    Method
}

internal sealed record ReflectedMemberReference(
    Instruction Instruction,
    ITypeDefOrRef DeclaringType,
    string MemberName,
    ReflectedMemberKind Kind);

internal static class ReflectionSurfaceFlowScanner
{
    private abstract record SymbolicValue;
    private sealed record UnknownValue : SymbolicValue;
    private sealed record StringValue(string Value) : SymbolicValue;
    private sealed record TypedInstanceValue(ITypeDefOrRef Type) : SymbolicValue;
    private sealed record TypeHandleValue(ITypeDefOrRef Type) : SymbolicValue;
    private sealed record TypeValue(ITypeDefOrRef Type) : SymbolicValue;

    private static readonly SymbolicValue Unknown = new UnknownValue();

    public static IReadOnlyList<ReflectedMemberReference> Scan(MethodDef method)
    {
        if (!method.HasBody)
            return Array.Empty<ReflectedMemberReference>();

        var references = new List<ReflectedMemberReference>();
        var stack = new List<SymbolicValue>();
        var locals = new Dictionary<Local, SymbolicValue>();
        var flowBoundaries = CollectFlowBoundaries(method);
        var first = true;

        foreach (var instruction in method.Body.Instructions)
        {
            if (!first && flowBoundaries.Contains(instruction))
            {
                stack.Clear();
                locals.Clear();
            }
            first = false;

            switch (instruction.OpCode.Code)
            {
                case Code.Ldstr when instruction.Operand is string value:
                    stack.Add(new StringValue(value));
                    break;
                case Code.Ldtoken when instruction.Operand is ITypeDefOrRef type:
                    stack.Add(new TypeHandleValue(type));
                    break;
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                    stack.Add(TryGetArgumentRuntimeType(method, instruction) is { } argumentType
                        ? new TypedInstanceValue(argumentType)
                        : Unknown);
                    break;
                case Code.Dup:
                    stack.Add(stack.Count == 0 ? Unknown : stack[^1]);
                    break;
                case Code.Pop:
                    Pop(stack);
                    break;
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                    stack.Add(TryGetLocal(method, instruction, out var loadedLocal) &&
                              locals.TryGetValue(loadedLocal, out var localValue)
                        ? localValue
                        : Unknown);
                    break;
                case Code.Stloc:
                case Code.Stloc_S:
                case Code.Stloc_0:
                case Code.Stloc_1:
                case Code.Stloc_2:
                case Code.Stloc_3:
                    if (TryGetLocal(method, instruction, out var storedLocal))
                        locals[storedLocal] = Pop(stack);
                    else
                        Pop(stack);
                    break;
                case Code.Call:
                case Code.Callvirt:
                case Code.Newobj:
                    ProcessCall(instruction, stack, references);
                    break;
                default:
                    ApplyUnknownStackEffect(instruction, stack);
                    break;
            }

            switch (instruction.OpCode.FlowControl)
            {
                case FlowControl.Branch:
                case FlowControl.Return:
                case FlowControl.Throw:
                    stack.Clear();
                    locals.Clear();
                    break;
                case FlowControl.Cond_Branch:
                    // Continue conservatively along the fall-through path. Branch targets
                    // are reset above because their incoming symbolic values may differ.
                    stack.Clear();
                    break;
            }
        }

        return references;
    }

    private static void ProcessCall(
        Instruction instruction,
        List<SymbolicValue> stack,
        ICollection<ReflectedMemberReference> references)
    {
        instruction.CalculateStackUsage(out var pushes, out var pops);
        var arguments = PopArguments(stack, pops);
        if (instruction.Operand is not IMethod target)
        {
            PushUnknown(stack, pushes);
            return;
        }

        if (IsTypeGetTypeFromHandle(target) &&
            arguments.LastOrDefault() is TypeHandleValue typeHandle)
        {
            stack.Add(new TypeValue(typeHandle.Type));
            PushUnknown(stack, pushes - 1);
            return;
        }

        // Many MODs retain desktop-version compatibility by reflecting through a typed Unity
        // object rather than typeof(T). The receiver still gives us a closed compile-time type,
        // so it is safe to retain that exact reflected member in the proxy surface.
        if (IsObjectGetType(target) &&
            arguments.LastOrDefault() is TypedInstanceValue typedInstance)
        {
            stack.Add(new TypeValue(typedInstance.Type));
            PushUnknown(stack, pushes - 1);
            return;
        }

        if (TryClassifyLookup(target, out var kind))
        {
            var declaringType = arguments.OfType<TypeValue>().FirstOrDefault();
            var memberName = arguments.OfType<StringValue>().FirstOrDefault();
            if (declaringType is not null && memberName is not null && memberName.Value.Length != 0)
            {
                references.Add(new ReflectedMemberReference(
                    instruction,
                    declaringType.Type,
                    memberName.Value,
                    kind));
            }
        }

        PushUnknown(stack, pushes);
    }

    private static bool IsTypeGetTypeFromHandle(IMethod method)
        => method.DeclaringType.FullName == "System.Type" &&
           method.Name.String == "GetTypeFromHandle";

    private static bool IsObjectGetType(IMethod method)
        => method.DeclaringType.FullName == "System.Object" &&
           method.Name.String == "GetType" &&
           method.MethodSig is { Params.Count: 0 } signature &&
           signature.RetType.RemovePinnedAndModifiers().FullName == "System.Type";

    private static bool TryClassifyLookup(IMethod method, out ReflectedMemberKind kind)
    {
        kind = default;
        var methodName = method.Name.String;
        var returnType = method.MethodSig?.RetType.RemovePinnedAndModifiers().FullName;
        var declaringType = method.DeclaringType.FullName;

        if (methodName is "Field" or "GetField" or "DeclaredField")
        {
            kind = ReflectedMemberKind.Field;
            return declaringType == "System.Type" || returnType == "System.Reflection.FieldInfo";
        }
        if (methodName is "Property" or "GetProperty" or "DeclaredProperty")
        {
            kind = ReflectedMemberKind.Property;
            return declaringType == "System.Type" || returnType == "System.Reflection.PropertyInfo";
        }
        if (methodName is "Method" or "GetMethod" or "DeclaredMethod")
        {
            kind = ReflectedMemberKind.Method;
            return declaringType == "System.Type" || returnType == "System.Reflection.MethodInfo";
        }

        return false;
    }

    private static SymbolicValue[] PopArguments(List<SymbolicValue> stack, int count)
    {
        if (count <= 0)
            return Array.Empty<SymbolicValue>();

        var values = new SymbolicValue[count];
        for (var index = count - 1; index >= 0; index--)
            values[index] = Pop(stack);
        return values;
    }

    private static SymbolicValue Pop(List<SymbolicValue> stack)
    {
        if (stack.Count == 0)
            return Unknown;
        var value = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    private static void PushUnknown(List<SymbolicValue> stack, int count)
    {
        for (var index = 0; index < count; index++)
            stack.Add(Unknown);
    }

    private static void ApplyUnknownStackEffect(Instruction instruction, List<SymbolicValue> stack)
    {
        instruction.CalculateStackUsage(out var pushes, out var pops);
        if (pops < 0)
            stack.Clear();
        else
            for (var index = 0; index < pops; index++)
                Pop(stack);
        PushUnknown(stack, pushes);
    }

    private static bool TryGetLocal(MethodDef method, Instruction instruction, out Local local)
    {
        if (instruction.Operand is Local operandLocal)
        {
            local = operandLocal;
            return true;
        }

        var index = instruction.OpCode.Code switch
        {
            Code.Ldloc_0 or Code.Stloc_0 => 0,
            Code.Ldloc_1 or Code.Stloc_1 => 1,
            Code.Ldloc_2 or Code.Stloc_2 => 2,
            Code.Ldloc_3 or Code.Stloc_3 => 3,
            _ => -1
        };
        if (index >= 0 && index < method.Body.Variables.Count)
        {
            local = method.Body.Variables[index];
            return true;
        }

        local = null!;
        return false;
    }

    private static ITypeDefOrRef? TryGetArgumentRuntimeType(MethodDef method, Instruction instruction)
    {
        var signature = method.MethodSig;
        var index = instruction.GetParameterIndex();
        if (signature is null || index < 0)
            return null;

        TypeSig? type;
        if (signature.HasThis && index == 0)
        {
            type = method.DeclaringType?.ToTypeSig();
        }
        else
        {
            var parameterIndex = signature.HasThis ? index - 1 : index;
            if (parameterIndex < 0 || parameterIndex >= signature.Params.Count)
                return null;
            type = signature.Params[parameterIndex];
        }

        type = type?.RemovePinnedAndModifiers();
        while (type is ByRefSig byReference)
            type = byReference.Next.RemovePinnedAndModifiers();
        return type is null or GenericVar or GenericMVar ? null : type.ToTypeDefOrRef();
    }

    private static HashSet<Instruction> CollectFlowBoundaries(MethodDef method)
    {
        var boundaries = new HashSet<Instruction>();
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is Instruction target)
                boundaries.Add(target);
            else if (instruction.Operand is IList<Instruction> targets)
                foreach (var branchTarget in targets)
                    boundaries.Add(branchTarget);
        }

        foreach (var handler in method.Body.ExceptionHandlers)
        {
            AddBoundary(boundaries, handler.TryStart);
            AddBoundary(boundaries, handler.HandlerStart);
            AddBoundary(boundaries, handler.FilterStart);
        }
        return boundaries;
    }

    private static void AddBoundary(ISet<Instruction> boundaries, Instruction? instruction)
    {
        if (instruction is not null)
            boundaries.Add(instruction);
    }
}
