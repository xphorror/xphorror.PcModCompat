using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Generator.Contexts;
using Il2CppInterop.Generator.Extensions;
using Il2CppInterop.Generator.Utils;

namespace Il2CppInterop.Generator.Passes;

public static class Pass21GenerateValueTypeFields
{
    public static void DoPass(RewriteGlobalContext context)
    {
        foreach (var assemblyContext in context.Assemblies)
        {
            var il2CppTypeTypeRewriteContext = assemblyContext.GlobalContext.GetAssemblyByName("mscorlib")
                .GetTypeByName("System.Object");
            var il2CppSystemTypeRef =
                assemblyContext.NewAssembly.ManifestModule!.DefaultImporter.ImportType(il2CppTypeTypeRewriteContext.NewType).ToTypeSignature();

            foreach (var typeContext in assemblyContext.Types)
            {
                if (typeContext.ComputedTypeSpecifics != TypeRewriteContext.TypeSpecifics.BlittableStruct ||
                    typeContext.OriginalType.IsEnum) continue;

                try
                {
                    var newType = typeContext.NewType;
                    var instanceFields = typeContext.Fields
                        .Where(field => !field.OriginalField.IsStatic)
                        .ToArray();
                    var hasInjectedOffsets = instanceFields.Length != 0 && instanceFields.All(field =>
                        field.OriginalField.TryExtractFieldOffset(out _));
                    var hasMetadataOffsets = instanceFields.Length != 0 && instanceFields.All(field =>
                        field.OriginalField.FieldOffset.HasValue);
                    var useExplicitLayout = hasInjectedOffsets ||
                                            (typeContext.OriginalType.IsExplicitLayout && hasMetadataOffsets);

                    if (typeContext.OriginalType.IsExplicitLayout &&
                        instanceFields.Length != 0 &&
                        !useExplicitLayout)
                    {
                        throw new InvalidOperationException(
                            $"Explicit-layout source type has incomplete field offsets: " +
                            $"{typeContext.OriginalType.FullName}");
                    }

                    newType.Attributes = (newType.Attributes & ~TypeAttributes.LayoutMask) |
                                         (useExplicitLayout
                                             ? TypeAttributes.ExplicitLayout
                                             : TypeAttributes.SequentialLayout);
                    if (typeContext.OriginalType.ClassLayout is { } originalLayout)
                    {
                        newType.ClassLayout = new ClassLayout(
                            originalLayout.PackingSize,
                            originalLayout.ClassSize);
                    }

                    ILGeneratorEx.GenerateBoxMethod(assemblyContext.Imports, newType, typeContext.ClassPointerFieldRef,
                        il2CppSystemTypeRef);

                    foreach (var fieldContext in typeContext.Fields)
                    {
                        var field = fieldContext.OriginalField;
                        if (field.IsStatic) continue;

                        var newField = new FieldDefinition(fieldContext.UnmangledName, field.Attributes.ForcePublic(),
                            !field.Signature!.FieldType.IsValueType()
                                ? assemblyContext.Imports.Module.IntPtr()
                                : assemblyContext.RewriteTypeRef(field.Signature.FieldType));

                        if (useExplicitLayout)
                        {
                            newField.FieldOffset = field.TryExtractFieldOffset(out var injectedOffset)
                                ? injectedOffset
                                : checked((int)field.FieldOffset!.Value);
                        }

                        // Special case: bools in Il2Cpp are bytes
                        if (newField.Signature!.FieldType.FullName == "System.Boolean")
                            newField.MarshalDescriptor = new SimpleMarshalDescriptor(NativeType.U1);

                        newType.Fields.Add(newField);
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        $"Failed to generate value type fields for type {typeContext.OriginalType.FullName} in assembly {typeContext.AssemblyContext.OriginalAssembly.Name}",
                        ex);
                }
            }
        }
    }
}
