using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
    /// <summary>
    /// Audited display call sites only. Never hooks the general input API or changes its live lists.
    /// A different MOD binary is deliberately left alone until its consumers are audited.
    /// </summary>
    public sealed class ExternalKeyLabelDisplaySystem : ModSystem
    {
        private static readonly List<IDisposable> Hooks = new();
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly Guid CojMvid = new("63dd972e-c445-495f-adcb-4a826b5e3336");
        private delegate string GetUpOrig();

        public override void PostSetupContent()
        {
            Install("BakaMod", "ecd80ddb-8156-49c8-8387-21c18502da1f", "BakaMod.Common.Systems.SpellBookUISystem", "GetKeyName", typeof(int));
            Install("GitServerSystem", "a66933df-c242-4a6b-b2e2-64b7b9693b47", "GitServerSystem.UI.F22.Hud", "DrawSelf", typeof(SpriteBatch));
            Install("RecipeBrowser", "b2830ebb-661c-41e5-9d94-ea81f475f2af", "RecipeBrowser.RecipeBrowserUI", "UpdateFavoritedPanel");
            Install("ImproveGame", "1b002923-5b66-4599-bf29-3cbbff42f040", "ImproveGame.Helpers.MyUtils", "TryGetKeybindString", typeof(ModKeybind), typeof(string).MakeByRefType());
            if (ModLoader.TryGetMod("ContinentOfJourney", out Mod coj) && coj.Code?.ManifestModule.ModuleVersionId == CojMvid)
            {
                var method = coj.Code.GetType("ContinentOfJourney.Items.Extra.Dispenser.ItemDispenser")
                    ?.GetMethod("GetUpKeyName", Flags, null, Type.EmptyTypes, null);
                if (method?.IsStatic == true && method.ReturnType == typeof(string))
                    TryAddHook(() => new Hook(method, (Func<GetUpOrig, string>)GetUpDisplay));
            }
        }

        private static void Install(string modName, string mvid, string type, string method, params Type[] parameters)
        {
            if (ModLoader.TryGetMod(modName, out Mod mod) && mod.Code != null)
                TryInstallReadAdapter(mod.Code, type, method, new Guid(mvid), parameters);
        }

        private static bool TryInstallReadAdapter(Assembly assembly, string typeName, string methodName, Guid expectedMvid, Type[] parameters)
        {
            if (assembly.ManifestModule.ModuleVersionId != expectedMvid)
                return false;
            MethodInfo method = assembly.GetType(typeName)?.GetMethod(methodName, Flags, null, parameters, null);
            if (method == null || method.GetMethodBody() == null)
                return false;
            return TryAddHook(() => new ILHook(method, RewriteDisplayRead));
        }

        private static void RewriteDisplayRead(ILContext il)
        {
            var reads = il.Body.Instructions.Where(i => i.MatchCallvirt<ModKeybind>(nameof(ModKeybind.GetAssignedKeys))).ToArray();
            if (reads.Length != 1 || reads[0].Previous?.OpCode != OpCodes.Ldc_I4_0)
                throw new InvalidOperationException("Audited Keyboard display read shape changed");
            var cursor = new ILCursor(il);
            cursor.Goto(reads[0], MoveType.Before);
            cursor.Remove();
            cursor.EmitDelegate<Func<ModKeybind, InputMode, List<string>>>(ReadAssignedForDisplay);
        }

        private static bool TryAddHook(Func<IDisposable> create)
        {
            try { Hooks.Add(create()); return true; }
            catch (Exception ex)
            {
                AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds: an optional key-label display adapter was not applied: " + ex.Message);
                return false;
            }
        }

        internal static List<string> ReadAssignedForDisplay(ModKeybind keybind, InputMode mode)
        {
            List<string> assigned = keybind.GetAssignedKeys(mode); // Original API semantics/exceptions preserved.
            if (mode != InputMode.Keyboard || !BindingDisplayText.NeedsOverrideFromAssignedKeys(assigned))
                return assigned;
            try
            {
                string id = ModKeybindReflection.GetActionId(keybind);
                if (BindingDisplayResolver.TryGetBindingDisplayText(id, assigned, out string label) && IsReadable(label))
                    return new List<string> { label };
                return ReadFallback(assigned);
            }
            catch (Exception) { return ReadFallback(assigned); }
        }

        private static List<string> ReadFallback(List<string> assigned)
            // Without storage provenance do not guess AND semantics, but never lose later keys
            // merely because the audited consumer only reads element zero.
            => new() { string.Join(" / ", assigned.ConvertAll(ReadToken)) };

        private static string GetUpDisplay(GetUpOrig orig)
        {
            string original = orig();
            try
            {
                var assigned = VanillaRebindHelper.GetAssignedKeysForInputMode("Up", InputMode.Keyboard);
                if (BindingDisplayResolver.TryGetBindingDisplayText("Up", assigned, out string label) && IsReadable(label))
                    return label;
                return BindingDisplayText.ReplaceOwnershipMarkersInDisplayText(original, ReadToken);
            }
            catch (Exception) { return original; }
        }

        private static string ReadToken(string token)
        {
            try { string label = BindingDisplayResolver.ResolveDisplayName(token); return IsReadable(label) ? label : "?"; }
            catch (Exception) { return "?"; }
        }

        private static bool IsReadable(string label)
            => !string.IsNullOrEmpty(label) && label.IndexOf("AKAdv:", StringComparison.OrdinalIgnoreCase) < 0
                && label.IndexOf("SC:", StringComparison.OrdinalIgnoreCase) < 0 && label.IndexOf("VK:", StringComparison.OrdinalIgnoreCase) < 0;

        public override void Unload()
        {
            for (int i = Hooks.Count - 1; i >= 0; i--)
                try { Hooks[i].Dispose(); } catch (Exception) { /* Best effort; keep releasing others. */ }
            Hooks.Clear();
        }
    }

    public sealed class CojConfiguredKeyHint : GlobalItem
    {
        private static readonly FieldInfo RegisteredKeys = typeof(KeybindLoader).GetField("modKeybinds", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly HashSet<string> Items = new(StringComparer.Ordinal)
        {
            "BattalionsBackup", "BoxOfMystery", "BuffBanner", "Concheror", "GrayRook", "OneGiantLeap", "SurvivalCrisis",
            "FrameWooden", "FrameIron", "FrameBone", "FrameYin", "FramePlant", "FrameHeroic", "FrameSolar", "FramePrism"
        };
        public override void ModifyTooltips(Item item, List<TooltipLine> tooltips)
        {
            try
            {
                ModItem content = item?.ModItem;
                if (content?.Mod?.Name != "ContinentOfJourney" || !Items.Contains(content.Name)
                    || content.Mod.Code?.ManifestModule.ModuleVersionId != new Guid("63dd972e-c445-495f-adcb-4a826b5e3336")
                    || RegisteredKeys?.GetValue(null) is not IDictionary<string, ModKeybind> keys
                    || !keys.TryGetValue("ContinentOfJourney/CoJActivate", out ModKeybind key))
                    return;
                var display = ExternalKeyLabelDisplaySystem.ReadAssignedForDisplay(key, InputMode.Keyboard);
                if (display.Count > 0)
                    tooltips.Add(new TooltipLine(Mod, "AKConfiguredActivation", "AK: " + string.Join(" / ", display)));
            }
            catch (Exception) { /* An optional hint must not break the original item tooltip. */ }
        }
    }
}
