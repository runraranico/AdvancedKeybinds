using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Patches
{
    /// <summary>Only the audited Spell HUD key draw; icons, effects and native bindings stay original.</summary>
    public sealed class BakaKeyLabelLayout : ModSystem
    {
        private static ILHook _hook;
        private static readonly FieldInfo RegisteredKeys = typeof(KeybindLoader).GetField("modKeybinds", BindingFlags.Static | BindingFlags.NonPublic);
        private delegate Vector2 DrawLabel(SpriteBatch batch, string text, Vector2 position, Color color,
            float scale, float anchorX, float anchorY, int maxCharacters, int slot);

        public override void PostSetupContent()
        {
            if (!ModLoader.TryGetMod("BakaMod", out Mod mod)
                || mod.Code?.ManifestModule.ModuleVersionId != new Guid("ecd80ddb-8156-49c8-8387-21c18502da1f")) return;
            var type = mod.Code.GetType("BakaMod.Common.Systems.SpellBookUISystem");
            var method = type?.GetMethod("DrawSpellIcon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method?.GetParameters().Length != 7 || method.GetParameters()[4].ParameterType != typeof(int)) return;
            try
            {
                _hook = new ILHook(method, il =>
                {
                    var calls = il.Body.Instructions.Where(i => i.MatchCall(typeof(Utils), nameof(Utils.DrawBorderString))).ToArray();
                    if (calls.Length != 1 || calls[0].Next?.OpCode != OpCodes.Pop)
                        throw new InvalidOperationException("Audited Spell HUD key draw changed");
                    var cursor = new ILCursor(il);
                    cursor.Goto(calls[0], MoveType.Before);
                    cursor.Remove();
                    cursor.Emit(OpCodes.Ldarg, il.Method.Parameters[4]);
                    cursor.EmitDelegate<DrawLabel>(DrawFittedLabel);
                });
            }
            catch (Exception ex) { AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds: Spell HUD label layout not applied: " + ex.Message); }
        }

        internal static bool HasAdvancedBinding(int slot)
        {
            if (slot < 0 || slot > 3) return false;
            try
            {
                return RegisteredKeys?.GetValue(null) is IDictionary<string, ModKeybind> map
                    && map.TryGetValue("BakaMod/Spell " + (slot + 1), out ModKeybind key)
                    && BindingDisplayText.NeedsOverrideFromAssignedKeys(key.GetAssignedKeys(InputMode.Keyboard));
            }
            catch (Exception) { return false; }
        }

        private static Vector2 DrawFittedLabel(SpriteBatch batch, string text, Vector2 position, Color color,
            float scale, float anchorX, float anchorY, int maxCharacters, int slot)
        {
            if (!HasAdvancedBinding(slot))
                return Utils.DrawBorderString(batch, text, position, color, scale, anchorX, anchorY, maxCharacters);
            // Remain readable even if another optional hook prevented GetKeyName's adapter.
            if (RegisteredKeys?.GetValue(null) is IDictionary<string, ModKeybind> map
                && map.TryGetValue("BakaMod/Spell " + (slot + 1), out ModKeybind key))
                text = string.Join(" / ", ExternalKeyLabelDisplaySystem.ReadAssignedForDisplay(key, InputMode.Keyboard));
            var font = FontAssets.MouseText.Value;
            var fit = KeyLabelFitPolicy.Fit(text, 52f, s => font.MeasureString(s).X, scale, Math.Min(.65f, scale));
            Vector2 size = font.MeasureString(fit.Text) * fit.Scale;
            if (fit.NeedsHover)
                KeyLabelHoverSystem.Queue(new object(), new Rectangle((int)(position.X - size.X * anchorX),
                    (int)(position.Y - size.Y * anchorY), (int)Math.Ceiling(size.X), (int)Math.Ceiling(size.Y)), text);
            return Utils.DrawBorderString(batch, fit.Text, position, color, fit.Scale, anchorX, anchorY, -1);
        }

        public override void Unload()
        {
            try { _hook?.Dispose(); }
            catch (Exception) { /* Best effort during MOD teardown. */ }
            finally { _hook = null; }
        }
    }
}
