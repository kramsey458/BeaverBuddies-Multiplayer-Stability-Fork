using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Compiles a tiny assembly in memory, the way another mod ships its own ReplayEvent subclasses. The bytes are loaded
// with Assembly.Load(byte[]), as MixedStorage loads its multiplayer bridge, so the types live outside BeaverBuddies
// and outside any directory the loader probes.
internal static class StubAssembly
{
    /// <summary>A public field. Kind is "string", "int", the name of a class in the same assembly, or "List:Name".</summary>
    public record Field(string Name, string Kind);

    /// <summary>A public class with a public parameterless constructor. An event derives from ReplayEvent.</summary>
    public record Class(string Namespace, string Name, bool IsEvent, params Field[] Fields);

    public static byte[] Build(string assemblyName, Assembly mod, params Class[] classes)
    {
        var metadata = new MetadataBuilder();
        var code = new BlobBuilder();
        var bodies = new MethodBodyStreamEncoder(code);
        StringHandle S(string text) => metadata.GetOrAddString(text);

        metadata.AddAssembly(S(assemblyName), new Version(1, 0, 0, 0), default, default, default, AssemblyHashAlgorithm.None);
        metadata.AddModule(0, S(assemblyName + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);

        AssemblyReferenceHandle Reference(AssemblyName name)
        {
            byte[] token = name.GetPublicKeyToken() ?? Array.Empty<byte>();
            return metadata.AddAssemblyReference(S(name.Name!), name.Version ?? new Version(0, 0, 0, 0), default,
                token.Length == 0 ? default : metadata.GetOrAddBlob(token), default, default);
        }
        var core = Reference(typeof(object).Assembly.GetName());
        var beaverBuddies = Reference(mod.GetName());
        var objectType = metadata.AddTypeReference(core, S("System"), S("Object"));
        var listType = metadata.AddTypeReference(core, S("System.Collections.Generic"), S("List`1"));
        var eventType = metadata.AddTypeReference(beaverBuddies, S("BeaverBuddies.Events"), S("ReplayEvent"));
        var contextType = metadata.AddTypeReference(beaverBuddies, S("BeaverBuddies.Events"), S("IReplayContext"));

        BlobHandle Signature(Action<BlobEncoder> write)
        {
            var blob = new BlobBuilder();
            write(new BlobEncoder(blob));
            return metadata.GetOrAddBlob(blob);
        }
        var constructorSignature = Signature(e => e.MethodSignature(isInstanceMethod: true).Parameters(0, r => r.Void(), p => { }));
        var replaySignature = Signature(e => e.MethodSignature(isInstanceMethod: true)
            .Parameters(1, r => r.Void(), p => p.AddParameter().Type().Type(contextType, false)));
        var objectConstructor = metadata.AddMemberReference(objectType, S(".ctor"), constructorSignature);
        var eventConstructor = metadata.AddMemberReference(eventType, S(".ctor"), constructorSignature);

        int Body(Action<InstructionEncoder> emit)
        {
            var il = new InstructionEncoder(new BlobBuilder());
            emit(il);
            return bodies.AddMethodBody(il);
        }

        // Row 1 is <Module>; the classes follow in order, so a field can name a class defined after it.
        TypeDefinitionHandle Defined(string name) =>
            MetadataTokens.TypeDefinitionHandle(2 + Array.FindIndex(classes, c => c.Name == name));
        void FieldType(SignatureTypeEncoder type, string kind)
        {
            if (kind == "string") type.String();
            else if (kind == "int") type.Int32();
            else if (kind.StartsWith("List:")) type.GenericInstantiation(listType, 1, false).AddArgument().Type(Defined(kind.Substring(5)), false);
            else type.Type(Defined(kind), false);
        }

        metadata.AddTypeDefinition(default, default, S("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        foreach (Class type in classes)
        {
            var firstField = MetadataTokens.FieldDefinitionHandle(metadata.GetRowCount(TableIndex.Field) + 1);
            foreach (Field field in type.Fields)
                metadata.AddFieldDefinition(FieldAttributes.Public, S(field.Name), Signature(e => FieldType(e.FieldSignature(), field.Kind)));

            var firstMethod = MetadataTokens.MethodDefinitionHandle(metadata.GetRowCount(TableIndex.MethodDef) + 1);
            var noParameters = MetadataTokens.ParameterHandle(metadata.GetRowCount(TableIndex.Param) + 1);
            metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                MethodImplAttributes.IL, S(".ctor"), constructorSignature,
                Body(il => { il.LoadArgument(0); il.Call(type.IsEvent ? eventConstructor : objectConstructor); il.OpCode(ILOpCode.Ret); }),
                noParameters);
            if (type.IsEvent)
            {
                // public override void Replay(IReplayContext context) { }
                metadata.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    MethodImplAttributes.IL, S("Replay"), replaySignature, Body(il => il.OpCode(ILOpCode.Ret)), noParameters);
            }
            metadata.AddTypeDefinition(
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit | (type.IsEvent ? TypeAttributes.Sealed : 0),
                S(type.Namespace), S(type.Name), type.IsEvent ? eventType : objectType, firstField, firstMethod);
        }

        var image = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage),
            new MetadataRootBuilder(metadata), code, flags: CorFlags.ILOnly);
        var bytes = new BlobBuilder();
        image.Serialize(bytes);
        return bytes.ToArray();
    }
}
