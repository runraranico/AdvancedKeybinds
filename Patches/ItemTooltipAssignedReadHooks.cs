using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using Terraria.ModLoader;
using ReflectionOpCode = System.Reflection.Emit.OpCode;
using OperandType = System.Reflection.Emit.OperandType;

namespace AdvancedKeybinds.Patches
{
    // Only proven TooltipLine.Text/constructor sinks are adapted. Raw getter values,
    // comparisons, parsing, list identity and input ownership stay unchanged.
    internal static class ItemTooltipAssignedReadHooks
    {
        private static readonly Dictionary<short, ReflectionOpCode> Opcodes = typeof(System.Reflection.Emit.OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(ReflectionOpCode))
            .Select(f => (ReflectionOpCode)f.GetValue(null)).ToDictionary(op => op.Value);
        private static readonly List<IDisposable> Hooks = new();
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.Instance;

        internal static bool InstallLoadedTooltips()
        {
            try { return InstallTooltips(ModLoader.Mods); }
            catch (Exception) { return false; }
        }

        private static bool InstallTooltips(IEnumerable<Mod> mods)
        {
            try
            {
                var assemblies = new HashSet<Assembly>();
                var roots = new HashSet<MethodInfo>();
                foreach (Mod mod in mods)
                {
                    if (!IsModAssembly(mod.Code))
                        continue;
                    assemblies.Add(mod.Code);
                    foreach (ModItem item in mod.GetContent<ModItem>())
                        roots.Add(item.GetType().GetMethod(nameof(ModItem.ModifyTooltips), InstanceFlags,
                            null, new[] { typeof(List<TooltipLine>) }, null));
                    foreach (GlobalItem item in mod.GetContent<GlobalItem>())
                        roots.Add(item.GetType().GetMethod(nameof(GlobalItem.ModifyTooltips), InstanceFlags,
                            null, new[] { typeof(Terraria.Item), typeof(List<TooltipLine>) }, null));
                }
                return Install(roots, assemblies);
            }
            catch (Exception) { return false; }
        }

        internal static bool Install(IEnumerable<MethodInfo> roots, ISet<Assembly> assemblies)
        {
            if (Hooks.Count != 0) return false;
            try
            {
                var rootSet = new HashSet<MethodInfo>(roots.Where(m => CanAdapt(m, assemblies)));
                var callers = new Dictionary<MethodInfo, HashSet<MethodInfo>>();
                var children = new Dictionary<MethodInfo, HashSet<MethodInfo>>();
                var direct = new HashSet<MethodInfo>();
                var seen = new HashSet<MethodInfo>();
                var pending = new Queue<MethodInfo>(rootSet);
                while (pending.Count != 0)
                {
                    MethodInfo method = pending.Dequeue();
                    if (!CanAdapt(method, assemblies) || !seen.Add(method))
                        continue;
                    if (seen.Count > 8192) return false; // Bounded load-time work, not input monitoring.
                    MethodInfo[] callees;
                    try
                    {
                        // Read-only preflight: never create a detour to discover that it is a no-op.
                        using var body = new DynamicMethodDefinition(method);
                        using var context = new ILContext(body.Definition);
                        if (DisplaySinks(context).Any()) direct.Add(method);
                        callees = ReadCalledMethods(method).Where(m => CanAdapt(m, assemblies)).ToArray();
                    }
                    catch (Exception) { continue; } // Changed/unsupported MOD IL only loses display adaptation.
                    foreach (MethodInfo callee in callees)
                    {
                        if (!callers.TryGetValue(callee, out var parents))
                            callers[callee] = parents = new HashSet<MethodInfo>();
                        parents.Add(method);
                        if (!children.TryGetValue(method, out var descendants))
                            children[method] = descendants = new HashSet<MethodInfo>();
                        descendants.Add(callee);
                        pending.Enqueue(callee);
                    }
                }
                var affected = new HashSet<MethodInfo>(direct);
                pending = new Queue<MethodInfo>(direct);
                while (pending.Count != 0)
                    if (callers.TryGetValue(pending.Dequeue(), out var parents))
                        foreach (MethodInfo parent in parents)
                            if (affected.Add(parent)) pending.Enqueue(parent);

                var done = new HashSet<MethodInfo>();
                var active = new HashSet<MethodInfo>();
                void Attach(MethodInfo method)
                {
                    if (!CanAdapt(method, assemblies) || done.Contains(method) || !active.Add(method)) return;
                    if (children.TryGetValue(method, out var descendants))
                        foreach (MethodInfo child in descendants)
                            if (affected.Contains(child)) Attach(child);
                    active.Remove(method);
                    Hooks.Add(new ILHook(method, RewriteDisplaySinks));
                    done.Add(method);
                }
                foreach (MethodInfo method in affected) Attach(method);
                // Every helper is now adapted. Refresh outer entry bodies even in a call cycle,
                // so an older inlined helper body cannot survive the load-time installation.
                foreach (MethodInfo root in rootSet)
                    if (CanAdapt(root, assemblies) && affected.Contains(root)) Hooks.Add(new ILHook(root, static _ => { }));
                return true;
            }
            catch (Exception)
            {
                DisposeAll();
                return false;
            }
        }

        private static bool IsModAssembly(Assembly assembly)
            => assembly != null && !assembly.IsDynamic
                && assembly != typeof(Mod).Assembly && assembly != typeof(Terraria.Main).Assembly
                && assembly != typeof(string).Assembly && assembly != typeof(ItemTooltipAssignedReadHooks).Assembly;

        private static bool CanAdapt(MethodInfo method, ISet<Assembly> assemblies)
        {
            try
            {
                if (method?.DeclaringType == null || !IsModAssembly(method.DeclaringType.Assembly)
                    || !assemblies.Contains(method.DeclaringType.Assembly) || method.IsAbstract
                    || method.IsGenericMethod || method.ContainsGenericParameters)
                    return false;
                // Closed generic owners share runtime code too; ContainsGenericParameters alone is insufficient.
                for (Type type = method.DeclaringType; type != null; type = type.DeclaringType)
                    if (type.IsGenericType || type.ContainsGenericParameters) return false;
                return method.GetMethodBody() != null;
            }
            catch (Exception) { return false; }
        }

        private static bool IsTooltipSink(Instruction instruction)
        {
            Instruction next = instruction.Next;
            while (next != null && next.OpCode == Mono.Cecil.Cil.OpCodes.Nop) next = next.Next;
            return next?.OpCode == Mono.Cecil.Cil.OpCodes.Stfld && next.Operand is FieldReference field
                && field.DeclaringType.FullName == typeof(TooltipLine).FullName && field.Name == nameof(TooltipLine.Text)
                || next?.OpCode == Mono.Cecil.Cil.OpCodes.Newobj && next.Operand is MethodReference ctor
                && ctor.DeclaringType.FullName == typeof(TooltipLine).FullName && ctor.Parameters.Count == 3;
        }

        private static IEnumerable<(Instruction Instruction, Delegate Replacement)> DisplaySinks(ILContext il)
        {
            bool scalarSafe = !il.Body.Instructions.Any(i => i.Operand is MethodReference m
                && (m.DeclaringType.FullName == "System.Linq.Enumerable"
                    || m.DeclaringType.FullName == "System.String" && m.Name == "Join"));
            foreach (Instruction instruction in il.Body.Instructions.ToArray())
            {
                if (instruction.OpCode != Mono.Cecil.Cil.OpCodes.Call && instruction.OpCode != Mono.Cecil.Cil.OpCodes.Callvirt
                    || instruction.Operand is not MethodReference method || method.DeclaringType.FullName != "System.String"
                    || method.ReturnType.FullName != "System.String" || !IsTooltipSink(instruction)) continue;
                string[] parameters = method.Parameters.Select(p => p.ParameterType.FullName).ToArray();
                Delegate replacement = null;
                if (scalarSafe && method.Name == "Replace" && method.HasThis
                    && parameters.SequenceEqual(new[] { "System.String", "System.String" }))
                    replacement = (Func<string, string, string, string>)Replace;
                if (scalarSafe && method.Name == "Concat" && !method.HasThis && parameters.All(p => p == "System.String"))
                    replacement = parameters.Length switch {
                        2 => (Func<string, string, string>)Concat2,
                        3 => (Func<string, string, string, string>)Concat3,
                        4 => (Func<string, string, string, string, string>)Concat4,
                        _ => null
                    };
                if (scalarSafe && method.Name == "Format" && !method.HasThis && parameters.Length >= 2
                    && parameters[0] == "System.String" && parameters.Skip(1).All(p => p == "System.Object"))
                    replacement = parameters.Length switch {
                        2 => (Func<string, object, string>)Format1,
                        3 => (Func<string, object, object, string>)Format2,
                        4 => (Func<string, object, object, object, string>)Format3,
                        _ => null
                    };
                if (method.Name == "Join" && !method.HasThis && parameters.SequenceEqual(new[] {
                    "System.String", "System.Collections.Generic.IEnumerable`1<System.String>" }))
                    replacement = (Func<string, IEnumerable<string>, string>)Join;
                if (replacement != null) yield return (instruction, replacement);
            }
        }

        private static void RewriteDisplaySinks(ILContext il)
        {
            foreach (var (instruction, replacement) in DisplaySinks(il).ToArray())
            {
                var cursor = new ILCursor(il);
                cursor.Goto(instruction, MoveType.Before);
                cursor.Remove();
                cursor.EmitDelegate(replacement);
            }
        }

        private static string Resolve(string value, string[] text)
            => AkAdvItemTooltipDisplaySystem.ResolveTooltipKeyReference(value, text);
        private static object ResolveObject(object value, string[] text) => value is string s ? Resolve(s, text) : value;
        private static string Replace(string text, string oldValue, string newValue)
        {
            if (text == null) throw new NullReferenceException();
            return text.Replace(oldValue, Resolve(newValue, new[] { text }));
        }
        private static string Concat2(string a, string b) { var t = new[] { a, b }; return string.Concat(Resolve(a, t), Resolve(b, t)); }
        private static string Concat3(string a, string b, string c) { var t = new[] { a, b, c }; return string.Concat(Resolve(a, t), Resolve(b, t), Resolve(c, t)); }
        private static string Concat4(string a, string b, string c, string d) { var t = new[] { a, b, c, d }; return string.Concat(Resolve(a, t), Resolve(b, t), Resolve(c, t), Resolve(d, t)); }
        private static string Format1(string format, object a) => string.Format(format, ResolveObject(a, new[] { a as string }));
        private static string Format2(string format, object a, object b) { var t = new[] { a as string, b as string }; return string.Format(format, ResolveObject(a, t), ResolveObject(b, t)); }
        private static string Format3(string format, object a, object b, object c) { var t = new[] { a as string, b as string, c as string }; return string.Format(format, ResolveObject(a, t), ResolveObject(b, t), ResolveObject(c, t)); }
        private static string Join(string separator, IEnumerable<string> values)
            => AkAdvItemTooltipDisplaySystem.ResolveTooltipJoinedList(values, string.Join(separator, values));

        private static IEnumerable<MethodInfo> ReadCalledMethods(MethodInfo method)
        {
            byte[] bytes = method.GetMethodBody().GetILAsByteArray();
            for (int at = 0; at < bytes.Length;)
            {
                short code = bytes[at++];
                if (code == 0xfe) code = (short)(0xfe00 | bytes[at++]);
                ReflectionOpCode opcode = Opcodes[code];
                if (opcode.OperandType == OperandType.InlineMethod)
                {
                    MethodBase target = method.Module.ResolveMethod(BitConverter.ToInt32(bytes, at),
                        method.DeclaringType.GetGenericArguments(), method.GetGenericArguments());
                    if (target is MethodInfo called) yield return called;
                }
                at += opcode.OperandType switch {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, at),
                    _ => 4
                };
            }
        }

        internal static bool DisposeAll()
        {
            for (int i = Hooks.Count - 1; i >= 0; i--)
            {
                try { Hooks[i].Dispose(); Hooks.RemoveAt(i); }
                catch (Exception) { /* Retain the failed handle so failure is visible and retryable. */ }
            }
            return Hooks.Count == 0;
        }
    }
}
