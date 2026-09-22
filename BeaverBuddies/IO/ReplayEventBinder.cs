using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeaverBuddies.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BeaverBuddies.IO
{
    /// <summary>
    /// Decides which types a multiplayer frame may create. Frames are read with TypeNameHandling.All, so every
    /// "$type" in a frame, at any depth (for example inside AutomationEvent.arguments, an object[]), names a type for
    /// Newtonsoft to create. Without this, the other player could make this game create any type that is loaded.
    ///
    /// A "$type" is created only if the type is one of:
    /// - a ReplayEvent subclass from any loaded assembly. Other mods add their own: MixedStorage's
    ///   StorageAllocationEvent lives in an assembly it loads from bytes. A generic one needs its arguments to pass too.
    /// - a type the serialized members of a ReplayEvent subclass can hold, found by following their field and property
    ///   types. This assembly's subclasses, and those of every loaded assembly that references it, are scanned; an
    ///   assembly loaded later is scanned the first time one of its subclasses is resolved or a type is not allowed.
    ///   Which types pass therefore depends only on which assemblies are loaded, never on what arrived before.
    /// - a List, a one-dimensional array or a Nullable whose element type passes. An element declared as object also
    ///   passes: each element then carries its own "$type", which is checked by itself.
    /// - a primitive, string, Guid, any enum, or UnityEngine's Ray, Vector3 and Vector3Int.
    /// - one of the extra payload types given to the constructor: the place for a payload that no ReplayEvent field
    ///   declares, such as a value that travels in an object slot.
    /// Delegates, reflection types and Unity objects never pass. Anything else throws a
    /// <see cref="RefusedTypeException"/> naming the type and its assembly, before an instance is created.
    ///
    /// Names are resolved by Newtonsoft's own DefaultSerializationBinder, so an assembly that is only found through an
    /// AssemblyResolve hook still resolves, and BindToName is left to it, so the JSON that is sent (and the event hash
    /// taken from it) is exactly what it was without this binder.
    ///
    /// This file depends only on Newtonsoft, ReplayEvent and UnityEngine's value types, so other builds of
    /// BeaverBuddies can share it unchanged apart from the namespace. A build's extra payload types are passed to the
    /// constructor where the binder is created (JsonSettings), not written in here.
    /// </summary>
    public sealed class ReplayEventBinder : ISerializationBinder
    {
        /// <summary>A "$type" in a frame that the binder refused to create. The message names the type and its assembly.</summary>
        public sealed class RefusedTypeException : JsonSerializationException
        {
            public string AssemblyName { get; }
            public string TypeName { get; }

            public RefusedTypeException(string assemblyName, string typeName)
                : base($"Refused to create '{typeName}' from assembly '{assemblyName}': multiplayer only creates actions " +
                       "(ReplayEvent types) and the values they carry.")
            {
                AssemblyName = assemblyName;
                TypeName = typeName;
            }
        }

        private static readonly Type EventType = typeof(ReplayEvent);
        private static readonly Assembly HomeAssembly = EventType.Assembly;
        private static readonly string HomeAssemblyName = HomeAssembly.GetName().Name;

        private readonly DefaultSerializationBinder names = new DefaultSerializationBinder();
        private readonly object gate = new object();
        // Every type reached from the serialized members of a scanned ReplayEvent subclass, plus the extra payload types.
        private readonly HashSet<Type> payloadTypes = new HashSet<Type>();
        private readonly HashSet<Assembly> scannedAssemblies = new HashSet<Assembly>();
        // Types already found to pass. Only a pass is remembered: a type refused now may pass once more assemblies load.
        private readonly HashSet<Type> passed = new HashSet<Type>();
        private int assembliesSeen = -1;

        /// <param name="extraPayloadTypes">
        /// Types to accept although no ReplayEvent member declares them. Their own members are followed like an event's.
        /// </param>
        public ReplayEventBinder(params Type[] extraPayloadTypes)
        {
            lock (gate)
            {
                ScanAssembly(HomeAssembly);
                foreach (Type type in extraPayloadTypes ?? Type.EmptyTypes) Walk(type);
            }
        }

        public Type BindToType(string assemblyName, string typeName)
        {
            // Resolving does not create anything; a name that does not resolve throws here, as it always has.
            Type type = names.BindToType(assemblyName, typeName);
            bool allowed;
            lock (gate) allowed = IsAllowed(type);
            if (!allowed) throw new RefusedTypeException(assemblyName, typeName);
            return type;
        }

        public void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            names.BindToName(serializedType, out assemblyName, out typeName);
        }

        private bool IsAllowed(Type type)
        {
            if (type == null || NeverAllowed(type)) return false;
            if (passed.Contains(type)) return true;
            bool allowed = IsAllowedUncached(type);
            if (!allowed && ScanNewAssemblies()) allowed = IsAllowedUncached(type);
            if (allowed) passed.Add(type);
            return allowed;
        }

        private bool IsAllowedUncached(Type type)
        {
            if (EventType.IsAssignableFrom(type))
            {
                // Another mod's action: what its members can hold passes as well.
                ScanAssembly(type.Assembly);
                return ArgumentsAllowed(type);
            }
            if (IsPlainValue(type)) return true;
            if (type.IsArray) return type.GetArrayRank() == 1 && IsAllowedElement(type.GetElementType());
            if (type.IsGenericType)
            {
                Type definition = type.GetGenericTypeDefinition();
                if (definition == typeof(List<>) || definition == typeof(Nullable<>)) return ArgumentsAllowed(type);
            }
            return payloadTypes.Contains(type) && ArgumentsAllowed(type);
        }

        private bool ArgumentsAllowed(Type type) =>
            !type.IsGenericType || type.GetGenericArguments().All(IsAllowedElement);

        // A declared element type. object only says "each element names its own type", which is then checked.
        private bool IsAllowedElement(Type type) => type == typeof(object) || IsAllowed(type);

        private static bool IsPlainValue(Type type) =>
            (type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr)) ||
            type.IsEnum || type == typeof(string) || type == typeof(Guid) ||
            type == typeof(UnityEngine.Ray) || type == typeof(UnityEngine.Vector3) || type == typeof(UnityEngine.Vector3Int);

        private static bool NeverAllowed(Type type) =>
            type.IsPointer || type.IsByRef || type.ContainsGenericParameters ||
            type == typeof(IntPtr) || type == typeof(UIntPtr) ||
            typeof(Delegate).IsAssignableFrom(type) ||
            typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type) ||
            typeof(UnityEngine.Object).IsAssignableFrom(type);

        /// <summary>Scans the loaded assemblies that could define ReplayEvent subclasses. True if one was new.</summary>
        private bool ScanNewAssemblies()
        {
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            if (loaded.Length == assembliesSeen) return false;
            assembliesSeen = loaded.Length;
            bool scannedAny = false;
            foreach (Assembly assembly in loaded.OrderBy(a => a.FullName, StringComparer.Ordinal))
            {
                if (scannedAssemblies.Contains(assembly) || !ReferencesHome(assembly)) continue;
                ScanAssembly(assembly);
                scannedAny = true;
            }
            return scannedAny;
        }

        private static bool ReferencesHome(Assembly assembly)
        {
            try { return assembly.GetReferencedAssemblies().Any(name => name.Name == HomeAssemblyName); }
            // A dynamic assembly cannot list its references; scanning it costs little.
            catch (Exception) { return true; }
        }

        private void ScanAssembly(Assembly assembly)
        {
            if (!scannedAssemblies.Add(assembly)) return;
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            catch (Exception) { return; }
            foreach (Type type in types.Where(t => EventType.IsAssignableFrom(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                Walk(type);
            }
        }

        /// <summary>Adds a type, and every type its serialized members can hold, to the payload types.</summary>
        private void Walk(Type type)
        {
            if (type == null || type == typeof(object) || NeverAllowed(type)) return;
            if (!payloadTypes.Add(type)) return;
            if (type.IsArray) { Walk(type.GetElementType()); return; }
            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments()) Walk(argument);
            }
            // The framework's own types (List, Guid, ...) matter only through their generic arguments.
            if (type.IsPrimitive || type.IsEnum || IsFrameworkType(type)) return;
            try
            {
                foreach (Type memberType in SerializedMemberTypes(type)) Walk(memberType);
            }
            catch (Exception)
            {
                // A type whose members cannot be loaded here cannot be created from a frame either.
            }
        }

        private static bool IsFrameworkType(Type type) =>
            type.Namespace != null && (type.Namespace == "System" || type.Namespace.StartsWith("System.", StringComparison.Ordinal));

        // What Newtonsoft may write or read for a type: public fields and properties, and non-public ones marked
        // [JsonProperty]. Erring towards more members only allows types the mod's own actions already declare.
        private static IEnumerable<Type> SerializedMemberTypes(Type type)
        {
            const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(Declared).OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    if (IsSerialized(field, field.IsPublic)) yield return field.FieldType;
                }
                foreach (PropertyInfo property in current.GetProperties(Declared).OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (property.GetIndexParameters().Length > 0) continue;
                    bool isPublic = property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true;
                    if (IsSerialized(property, isPublic)) yield return property.PropertyType;
                }
            }
        }

        private static bool IsSerialized(MemberInfo member, bool isPublic) =>
            isPublic ? !member.IsDefined(typeof(JsonIgnoreAttribute), true) : member.IsDefined(typeof(JsonPropertyAttribute), true);
    }
}
