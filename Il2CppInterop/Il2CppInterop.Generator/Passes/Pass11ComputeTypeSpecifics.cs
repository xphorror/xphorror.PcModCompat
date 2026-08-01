using AsmResolver.DotNet.Signatures;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Generator.Passes;

public static class Pass11ComputeTypeSpecifics
{
    public static void DoPass(RewriteGlobalContext context)
    {
        foreach (var assemblyContext in context.Assemblies)
            foreach (var typeContext in assemblyContext.Types)
                try
                {
                    ComputeSpecifics(typeContext);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Failed to compute type specifics for {assemblyContext.OriginalAssembly.Name}::{typeContext.OriginalType?.FullName ?? typeContext.NewType.FullName}",
                        exception);
                }
    }

    private static void ComputeSpecifics(TypeRewriteContext typeContext)
    {
        if (typeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.NotComputed) return;
        typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.Computing;

        foreach (var originalField in typeContext.OriginalType.Fields)
        {
            if (originalField.Signature is null)
            {
                Logger.Instance.LogWarning(
                    "Skipping malformed field without a signature while computing blittability: {Assembly}::{Type}.{Field}",
                    typeContext.OriginalType.DeclaringModule?.Name,
                    typeContext.OriginalType.FullName,
                    originalField.Name);
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }

            // Sometimes il2cpp metadata has invalid field offsets for some reason (https://github.com/SamboyCoding/Cpp2IL/issues/167)
            if ((originalField.TryExtractFieldOffset(out var injectedOffset) &&
                 injectedOffset >= 0x8000000) ||
                (originalField.FieldOffset is { } metadataOffset &&
                 metadataOffset >= 0x8000000))
            {
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }

            if (originalField.IsStatic) continue;

            var fieldType = originalField.Signature.FieldType;
            if (fieldType.IsPrimitive() || fieldType is PointerTypeSignature)
                continue;
            if (fieldType.FullName == "System.String" || fieldType.FullName == "System.Object"
                || fieldType is ArrayBaseTypeSignature or ByReferenceTypeSignature or GenericParameterSignature or GenericInstanceTypeSignature)
            {
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }

            var resolvedFieldType = fieldType.Resolve();
            if (resolvedFieldType is null)
            {
                Logger.Instance.LogWarning(
                    "Treating field with an unresolved value type as non-blittable: {Assembly}::{Type}.{Field} ({FieldType})",
                    typeContext.OriginalType.DeclaringModule?.Name,
                    typeContext.OriginalType.FullName,
                    originalField.Name,
                    fieldType.FullName);
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }

            var fieldTypeContext = typeContext.AssemblyContext.GlobalContext.GetNewTypeForOriginal(resolvedFieldType);
            ComputeSpecifics(fieldTypeContext);
            if (fieldTypeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.BlittableStruct)
            {
                typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.NonBlittableStruct;
                return;
            }
        }

        typeContext.ComputedTypeSpecifics = TypeRewriteContext.TypeSpecifics.BlittableStruct;
    }
}
