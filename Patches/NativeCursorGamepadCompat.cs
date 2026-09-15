using System;
using System.Collections.Generic;
using System.Reflection;
using AdvancedKeybinds.Binding;
using AdvancedKeybinds.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.OS;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;
using Terraria.UI.Gamepad;

namespace AdvancedKeybinds.Patches
{
	/// <summary>
	/// Soft dependency (Windows verified): when Native Cursor suppresses vanilla DrawCursor via IL Ret,
	/// restore only GamePad cursors 13 / 15 / 17 using vanilla formulas and the live
	/// PlayerInput.smartSelectPointer (no AK DistUniform / UpdateSize detour).
	/// Removable as a unit — see ROLLBACK_NATIVE_CURSOR_COMPAT.md.
	/// </summary>
	internal static class NativeCursorGamepadCompat
	{
		public const string NativeCursorModName = "NativeCursor";
		public const string LayerName = "AdvancedKeybinds: NativeCursorGamepadCompat";

		private const string FutureSupportTypeName = "NativeCursor.NativeCursorGamepadSupport";

		private static readonly NativeCursorGamepadCompatPolicy.SessionState State = new();
		private static readonly NativeCursorGamepadCompatLayout.DrawItem[] PlanBuffer = new NativeCursorGamepadCompatLayout.DrawItem[2];

		private static bool _loggedInitSkipOnce;

		/// <summary>
		/// Returns true only when Applied was set. On failure Applied stays false.
		/// </summary>
		public static bool Apply()
		{
			if (State.Applied)
				return true;
			if (State.Disabled)
				return false;

			bool modPresent = ModLoader.HasMod(NativeCursorModName);
			bool declaresSupport = false;
			bool suppresses = false;

			try
			{
				if (modPresent)
				{
					if (!ModLoader.TryGetMod(NativeCursorModName, out Mod nc) || nc?.Code == null)
						return DisableCompat("nativecursor-code-null", null);

					declaresSupport = DetectFutureGamepadSupport(nc.Code);
					suppresses = EvaluateNcSuppressesVanillaAtInit();
				}

				var outcome = NativeCursorGamepadCompatPolicy.DecideInit(
					alreadyApplied: State.Applied,
					disabled: State.Disabled,
					modPresent: modPresent,
					ncRuntimeWouldSuppressVanilla: suppresses,
					ncDeclaresGamepadSupport: declaresSupport);

				State.NativeCursorDeclaresGamepadSupport = declaresSupport;
				State.NativeCursorSuppressesVanillaDraw = suppresses;

				if (outcome != NativeCursorGamepadCompatPolicy.InitOutcome.ApplyOk)
				{
					LogInitSkipOnce(outcome);
					State.Applied = false;
					return false;
				}

				State.Applied = true;
				State.Disabled = false;
				return true;
			}
			catch (Exception ex)
			{
				return DisableCompat("apply-failed", ex);
			}
		}

		public static void ResetAppliedFlag()
		{
			State.ResetForReload();
			_loggedInitSkipOnce = false;
		}

		public static void TryInsertLayer(List<GameInterfaceLayer> layers)
		{
			if (layers == null || !State.Applied || State.Disabled)
				return;

			for (int i = 0; i < layers.Count; i++)
			{
				if (layers[i] != null && layers[i].Name == LayerName)
					return;
			}

			int cursorIndex = layers.FindIndex(layer => layer != null && layer.Name == "Vanilla: Cursor");
			if (cursorIndex < 0)
				return;

			layers.Insert(cursorIndex + 1, new LegacyGameInterfaceLayer(
				LayerName,
				delegate
				{
					TryDrawGamepadCursors();
					return true;
				},
				InterfaceScaleType.UI));
		}

		private static void TryDrawGamepadCursors()
		{
			if (!State.Applied || State.Disabled)
				return;

			try
			{
				bool dead = Main.LocalPlayer != null && Main.LocalPlayer.dead && !Main.LocalPlayer.ghost && !Main.gameMenu;
				bool invisible = PlayerInput.InvisibleGamepadInMenus;
				bool suppressDead = dead || invisible;

				SmartSelectGamepadPointer smartPtr = PlayerInput.smartSelectPointer;
				bool pointerAvailable = smartPtr != null;

				var gate = NativeCursorGamepadCompatPolicy.DecideFrame(
					applied: State.Applied,
					disabled: State.Disabled,
					ncSuppressesVanillaCached: State.NativeCursorSuppressesVanillaDraw,
					ncDeclaresGamepadSupportCached: State.NativeCursorDeclaresGamepadSupport,
					showGamepadCursor: PlayerInput.SettingsForUI.ShowGamepadCursor,
					gameMenu: Main.gameMenu,
					cursorOverrideActive: Main.cursorOverride != -1,
					suppressDeadOrInvisible: suppressDead,
					vanillaDrawCursorBodyRuns: false,
					pointerAvailable: pointerAvailable);

				if (gate != NativeCursorGamepadCompatPolicy.FrameGate.Draw)
					return;

				bool smartUsed = Main.SmartCursorIsUsed;
				bool uiLinkBlocks = UILinkPointNavigator.Available && !PlayerInput.InBuildingMode;
				Vector2 bonus = Main.ThickMouse ? new Vector2(2f, 2f) : Vector2.Zero;

				float pointerX = 0f;
				float pointerY = 0f;
				bool pointerValid = false;

				if (smartUsed)
				{
					// Exact vanilla DrawCursor sequence.
					smartPtr.UpdateCenter(Main.ScreenSize.ToVector2() / 2f);
					Vector2 pointer = smartPtr.GetPointerPosition();
					pointerX = pointer.X;
					pointerY = pointer.Y;
					pointerValid = true;
				}

				int count = NativeCursorGamepadCompatLayout.BuildDrawPlan(
					showGamepadCursor: true,
					smartCursorIsUsed: smartUsed,
					smartDrawArg: smartUsed,
					uiLinkBlocksSmartCrosshair: uiLinkBlocks,
					suppressDeadOrInvisible: false,
					mouseX: Main.mouseX,
					mouseY: Main.mouseY,
					pointerX: pointerX,
					pointerY: pointerY,
					pointerValid: pointerValid || !smartUsed,
					gamepadCursorAlpha: Main.GamepadCursorAlpha,
					globalTimeWrappedHourly: Main.GlobalTimeWrappedHourly,
					bonusX: bonus.X,
					bonusY: bonus.Y,
					dest: PlanBuffer);

				SpriteBatch sb = Main.spriteBatch;
				if (sb == null || count <= 0)
					return;

				for (int i = 0; i < count; i++)
					DrawOne(sb, in PlanBuffer[i]);
			}
			catch (Exception ex)
			{
				DisableCompat("draw-failed", ex);
			}
		}

		private static void DrawOne(SpriteBatch sb, in NativeCursorGamepadCompatLayout.DrawItem item)
		{
			if (TextureAssets.Cursors == null
			    || item.CursorIndex < 0
			    || item.CursorIndex >= TextureAssets.Cursors.Length)
				return;

			var asset = TextureAssets.Cursors[item.CursorIndex];
			if (asset == null || !asset.IsLoaded)
				return;

			Texture2D tex = asset.Value;
			if (tex == null)
				return;

			Vector2 pos = new Vector2(item.PosX, item.PosY);
			Color color = Color.White * item.ColorMul;
			Rectangle? source = null;
			Vector2 origin;

			if (item.UseSmartFrame)
			{
				Rectangle frame = tex.Frame(2, 1, 0);
				source = frame;
				origin = frame.Size() / 2f;
			}
			else if (item.CursorIndex == NativeCursorGamepadCompatLayout.CursorSmartAlt)
			{
				Rectangle frame = tex.Frame(1, 1, 0);
				source = frame;
				origin = frame.Size() / 2f;
			}
			else
			{
				origin = tex.Size() / 2f;
			}

			sb.Draw(
				tex,
				pos,
				source,
				color,
				item.Rotation,
				origin,
				Main.cursorScale,
				SpriteEffects.None,
				0f);
		}

		private static bool DetectFutureGamepadSupport(Assembly asm)
		{
			try
			{
				if (asm.GetType(FutureSupportTypeName) != null)
					return true;

				Type t = asm.GetType("NativeCursor") ?? asm.GetType("NativeCursor.NativeCursor");
				if (t == null)
					return false;

				foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Static))
				{
					string n = m.Name;
					if (n == "SupportsGamepadCursor"
					    || n == "GamepadCursorRestored"
					    || n == "RestoresGamepadDrawCursor")
						return true;
				}
			}
			catch
			{
				return false;
			}

			return false;
		}

		/// <summary>Init-only: mirrors !NativeCursor.disabled(). Result cached on State.</summary>
		private static bool EvaluateNcSuppressesVanillaAtInit()
		{
			if (Main.dedServ)
				return false;
			if (Main.instance == null)
				return false;
			if (Platform.IsOSX)
				return false;
			return true;
		}

		private static bool DisableCompat(string reason, Exception ex)
		{
			State.Disabled = true;
			State.Applied = false;
			State.LastDisableReason = reason;

			if (!NativeCursorGamepadCompatPolicy.ShouldLogDisable(State))
				return false;

			try
			{
				string msg = "[NativeCursorGamepadCompat] Disabled (" + reason + "). "
					+ "AK and Terraria continue; GamePad cursor restore will not run. "
					+ (ex != null ? ex.GetType().Name + ": " + ex.Message : "");
				AdvancedKeybinds.Instance?.Logger?.Warn(msg);
			}
			catch
			{
				// ignore
			}

			return false;
		}

		private static void LogInitSkipOnce(NativeCursorGamepadCompatPolicy.InitOutcome outcome)
		{
			if (_loggedInitSkipOnce || outcome == NativeCursorGamepadCompatPolicy.InitOutcome.SkipModAbsent)
				return;
			_loggedInitSkipOnce = true;
			try
			{
				if (AkDiag.Enabled)
					AkDiag.Log("[NativeCursorGamepadCompat] Init skip: " + outcome);
			}
			catch
			{
				// ignore
			}
		}
	}

	/// <summary>
	/// Inserts the Native Cursor GamePad cursor restore layer after Vanilla: Cursor.
	/// </summary>
	public sealed class NativeCursorGamepadCompatSystem : ModSystem
	{
		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			NativeCursorGamepadCompat.TryInsertLayer(layers);
		}
	}
}
