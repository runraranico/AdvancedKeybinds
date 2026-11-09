using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Patches
{
    /// <summary>
    /// Optional display module: explicitly registered AK label rectangles only.
    /// Never consumes input or sets mouseInterface/focus. Removing its presentation call sites
    /// and this file removes hover hints without touching saved bindings or the label fitter.
    /// </summary>
    public sealed class KeyLabelHoverSystem : ModSystem
    {
        private sealed record Label(object Owner, Rectangle Bounds, string FullText)
        {
            public bool TooltipAppended;
            public bool Resolved;
        }
        private static readonly List<Label> Labels = new();
        private delegate void TooltipOrig(object self, ref string text);
        private delegate void TooltipHook(TooltipOrig orig, object self, ref string text);
        private delegate void DrawOrig(object self, SpriteBatch spriteBatch);
        private delegate void DrawHook(DrawOrig orig, object self, SpriteBatch spriteBatch);
        private static FieldInfo _fargoHoverElement;
        private static FieldInfo _fargoHeldElement;

        internal static void AttachFargo(Type type)
        {
            if (type?.Assembly.ManifestModule.ModuleVersionId != new Guid("535d0777-d3df-4436-ac3f-e1d1fd0ad83b")) return;
            var method = type.GetMethod("ModifyTooltip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string).MakeByRefType() }, null);
            var draw = type.GetMethod("DrawSelf", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(SpriteBatch) }, null);
            var menu = type.Assembly.GetType("FargowiltasSouls.Content.UI.ActiveSkillMenu");
            var hovered = menu?.GetField("MouseHoveredElement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var held = menu?.GetField("MouseHeldElement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (method?.ReturnType != typeof(void) || draw?.ReturnType != typeof(void) || hovered == null || held == null) return;
            _fargoHoverElement = hovered;
            _fargoHeldElement = held;
            ModDetours.Apply(method, (TooltipHook)AppendFargoHint);
            ModDetours.Apply(draw, (DrawHook)OnFargoDrawSelf);
        }

        internal static void Queue(object owner, Rectangle bounds, string fullText)
        {
            if (owner == null || bounds.Width <= 0 || bounds.Height <= 0 || string.IsNullOrEmpty(fullText)) return;
            var value = new Label(owner, bounds, fullText);
            for (int i = 0; i < Labels.Count; i++)
                if (ReferenceEquals(Labels[i].Owner, owner)) { Labels[i] = value; return; }
            if (Labels.Count < 64) Labels.Add(value);
        }

        internal static string AppendHint(string existing, string fullText)
            => string.IsNullOrEmpty(fullText) ? existing
                : string.IsNullOrEmpty(existing) ? fullText : existing + "\n" + fullText;

        private static void AppendFargoHint(TooltipOrig orig, object self, ref string text)
        {
            orig(self, ref text);
            foreach (var label in Labels)
                if (ReferenceEquals(label.Owner, self) && label.Bounds.Contains(Main.MouseScreen.ToPoint()))
                { text = AppendHint(text, label.FullText); label.TooltipAppended = true; return; }
        }

        private static void OnFargoDrawSelf(DrawOrig orig, object self, SpriteBatch spriteBatch)
        {
            // Exactly one original call. No geometry, hover-owner, drag or input state is changed.
            orig(self, spriteBatch);
            try
            {
                foreach (var label in Labels)
                {
                    if (!ReferenceEquals(label.Owner, self) || label.Resolved) continue;
                    // A local decision, including rejection, must not reappear in the generic fallback.
                    label.Resolved = true;
                    if (label.TooltipAppended) return;
                    if (Main.gameMenu || !label.Bounds.Contains(Main.MouseScreen.ToPoint())
                        || _fargoHoverElement == null || !ReferenceEquals(_fargoHoverElement.GetValue(null), self)
                        || _fargoHeldElement?.GetValue(null) != null) return;
                    // Fargo owns this visible box. Its empty-effect branch never calls ModifyTooltip.
                    // Underlying inventory hover flags must not suppress this local box's key hint.
                    Main.instance.MouseText(label.FullText);
                    return;
                }
            }
            catch (Exception) { /* Optional presentation must not interrupt Fargo's completed draw. */ }
        }

        public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
        {
            // Rendering continues while world-update counters stop during autopause.
            layers.Insert(0, new LegacyGameInterfaceLayer("AdvancedKeybinds: Begin Key Labels", () =>
            { Labels.Clear(); return true; }, InterfaceScaleType.UI));
            int index = layers.FindIndex(x => x.Name == "Vanilla: Mouse Text");
            if (index >= 0)
                layers.Insert(index, new LegacyGameInterfaceLayer("AdvancedKeybinds: Key Label Hover", DrawHover, InterfaceScaleType.UI));
        }

        private static bool DrawHover()
        {
            try
            {
                // Existing item/other-MOD tooltip wins. The local Fargo hook above appends
                // to its original effect text. Other registered consumers retain this fallback.
                bool blocked = Main.gameMenu || Main.mouseText || !string.IsNullOrEmpty(Main.hoverItemName)
                    || (Main.HoverItem != null && !Main.HoverItem.IsAir);
                string text = ResolveHoverText(blocked, Main.MouseScreen.ToPoint());
                if (text != null) Main.instance.MouseText(text);
                return true;
            }
            finally { Labels.Clear(); }
        }
        internal static string ResolveHoverText(bool blocked, Point uiMouse)
        {
            if (!blocked)
                for (int i = Labels.Count - 1; i >= 0; i--)
                    if (!Labels[i].Resolved && Labels[i].Bounds.Contains(uiMouse)) return Labels[i].FullText;
            return null;
        }
        public override void Unload()
        {
            Labels.Clear();
            _fargoHoverElement = null;
            _fargoHeldElement = null;
        }
    }
}
