#nullable enable
using System.Reflection;
using System.Reflection.Emit;

// What a compiled method's instructions call and which fields they read and write, for checks of wiring that
// cannot run outside the game (Unity's random state, Steam, dialogs).
internal static class IlScan
{
    private static readonly Dictionary<ushort, OpCode> opcodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => (ushort)o.Value);

    /// <summary>
    /// One instruction: its offset and opcode, and what it names: the method or field (<see cref="Member"/>), the
    /// string it loads (<see cref="Text"/>), or where it branches to (<see cref="Target"/>).
    /// </summary>
    public sealed record Instruction(int Offset, OpCode Op, MemberInfo? Member, string? Text, int? Target)
    {
        public bool Stores => Op == OpCodes.Stfld || Op == OpCodes.Stsfld;
        public bool Loads => Op == OpCodes.Ldfld || Op == OpCodes.Ldsfld || Op == OpCodes.Ldflda || Op == OpCodes.Ldsflda;
        public bool Calls => Op == OpCodes.Call || Op == OpCodes.Callvirt;
        public bool BranchesIfFalse => Op == OpCodes.Brfalse || Op == OpCodes.Brfalse_S;
        public bool Is(string declaringType, string name) => Member?.DeclaringType?.FullName == declaringType && Member.Name == name;
    }

    /// <summary>A method's instructions, in order.</summary>
    public static List<Instruction> Instructions(MethodBase method)
    {
        byte[] body = method.GetMethodBody()!.GetILAsByteArray()!;
        Type[]? typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
        Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var instructions = new List<Instruction>();
        for (int at = 0; at < body.Length;)
        {
            int offset = at;
            ushort value = body[at++];
            if (value == 0xfe) value = (ushort)(0xfe00 | body[at++]);
            OpCode op = opcodes[value];
            MemberInfo? member = null;
            string? text = null;
            int? target = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: target = at + 1 + (sbyte)body[at]; at += 1; break;
                case OperandType.InlineBrTarget: target = at + 4 + BitConverter.ToInt32(body, at); at += 4; break;
                case OperandType.ShortInlineI: case OperandType.ShortInlineVar: at += 1; break;
                case OperandType.InlineVar: at += 2; break;
                case OperandType.InlineI8: case OperandType.InlineR: at += 8; break;
                case OperandType.InlineSwitch: at += 4 + 4 * BitConverter.ToInt32(body, at); break;
                case OperandType.InlineString: text = method.Module.ResolveString(BitConverter.ToInt32(body, at)); at += 4; break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                    int token = BitConverter.ToInt32(body, at); at += 4;
                    try
                    {
                        member = op.OperandType == OperandType.InlineMethod
                            ? method.Module.ResolveMethod(token, typeArguments, methodArguments)
                            : method.Module.ResolveField(token, typeArguments, methodArguments);
                    }
                    // Something in an assembly this program does not load: not what is looked for here.
                    catch (Exception e) when (e is TypeLoadException || e is FileNotFoundException || e is ArgumentException) { }
                    break;
                default: at += 4; break;
            }
            instructions.Add(new Instruction(offset, op, member, text, target));
        }
        return instructions;
    }

    // The methods and fields a method's instructions name: what it calls, and what fields it reads and writes.
    public static List<MemberInfo> Members(MethodBase method) =>
        Instructions(method).Where(i => i.Member != null).Select(i => i.Member!).ToList();

    /// <summary>Every method (with a body) declared by <paramref name="type"/> and its nested types, lambdas included.</summary>
    public static Dictionary<MethodBase, List<MemberInfo>> Of(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return new[] { type }.Concat(type.GetNestedTypes(all))
            .SelectMany(t => t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            .Where(method => method.GetMethodBody() != null)
            .ToDictionary(method => method, Members);
    }

    public static bool Names(IEnumerable<MemberInfo> members, string declaringType, string name) =>
        members.Any(m => m.DeclaringType?.FullName == declaringType && m.Name == name);
}
