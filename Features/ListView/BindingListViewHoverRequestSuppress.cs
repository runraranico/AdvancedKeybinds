using System;
using System.Reflection;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Runtime MonoMod apply using the shared emitter contract.
	/// Per-method outcomes are preserved; Applied/BankApplied is never set on mismatch.
	/// Hooks register at most once per Apply cycle; VanillaStatus recomputed on each IL callback
	/// (MonoMod may re-run manipulators when later hooks are added).
	/// </summary>
	internal static class BindingListViewHoverRequestSuppress
	{
		private static bool _vanillaHooksRegistered;
		private static bool _bankHookRegistered;
		private static bool _warned;

		private static BindingListViewHoverRequestSuppressStatus _vanillaStatus;
		private static BindingListViewHoverRequestSuppressStatus _bankStatus;

		private static BindingListViewHoverRequestMethodOutcome _inventoryOutcome;
		private static BindingListViewHoverRequestMethodOutcome _emoteOutcome;
		private static BindingListViewHoverRequestMethodOutcome _bestiaryOutcome;
		private static BindingListViewHoverRequestMethodOutcome _bankOutcome;

		private static MethodInfo _wrapperMouseText8;
		private static MethodInfo _wrapperHackZoom2;

		public static BindingListViewHoverRequestSuppressStatus VanillaStatus => _vanillaStatus;
		public static BindingListViewHoverRequestSuppressStatus BankStatus => _bankStatus;

		public static BindingListViewHoverRequestMethodOutcome InventoryOutcome => _inventoryOutcome;
		public static BindingListViewHoverRequestMethodOutcome EmoteOutcome => _emoteOutcome;
		public static BindingListViewHoverRequestMethodOutcome BestiaryOutcome => _bestiaryOutcome;
		public static BindingListViewHoverRequestMethodOutcome BankOutcome => _bankOutcome;

		public static void Apply()
		{
			if (_vanillaHooksRegistered)
				return;

			_wrapperMouseText8 = typeof(BindingListViewHoverRequestSuppressWrappers).GetMethod(
				nameof(BindingListViewHoverRequestSuppressWrappers.ConditionalMouseText8),
				BindingFlags.Public | BindingFlags.Static);
			_wrapperHackZoom2 = typeof(BindingListViewHoverRequestSuppressWrappers).GetMethod(
				nameof(BindingListViewHoverRequestSuppressWrappers.ConditionalMouseTextHackZoom2),
				BindingFlags.Public | BindingFlags.Static);

			if (_wrapperMouseText8 == null || _wrapperHackZoom2 == null)
			{
				_vanillaStatus = BindingListViewHoverRequestSuppressStatus.UnsupportedMissingMethod;
				WarnOnce("wrapper MethodInfo resolve failed");
				return;
			}

			try
			{
				Type mainType = typeof(Main);
				MethodInfo drawInventory = Resolve(mainType, "DrawInventory", Type.EmptyTypes);
				MethodInfo drawEmote = Resolve(mainType, "DrawEmoteBubblesButton", new[] { typeof(int), typeof(int) });
				MethodInfo drawBestiary = Resolve(mainType, "DrawBestiaryIcon", new[] { typeof(int), typeof(int) });

				MonoModHooks.Modify(drawInventory, il =>
				{
					_inventoryOutcome = BindingListViewHoverRequestSuppressEmitter.TryReplaceVanillaUtilityMouseTextCalls(
						il.Instrs,
						il.Import(_wrapperMouseText8),
						BindingListViewHoverRequestCallsitePlanner.ExpectedDrawInventoryUtilityCalls);
					RecomputeVanillaStatus();
					if (_inventoryOutcome != BindingListViewHoverRequestMethodOutcome.Applied)
						WarnOnce("DrawInventory transform: " + _inventoryOutcome);
				});

				MonoModHooks.Modify(drawEmote, il =>
				{
					_emoteOutcome = BindingListViewHoverRequestSuppressEmitter.TryReplaceVanillaUtilityMouseTextCalls(
						il.Instrs,
						il.Import(_wrapperMouseText8),
						BindingListViewHoverRequestCallsitePlanner.ExpectedEmoteCalls);
					RecomputeVanillaStatus();
					if (_emoteOutcome != BindingListViewHoverRequestMethodOutcome.Applied)
						WarnOnce("DrawEmoteBubblesButton transform: " + _emoteOutcome);
				});

				MonoModHooks.Modify(drawBestiary, il =>
				{
					_bestiaryOutcome = BindingListViewHoverRequestSuppressEmitter.TryReplaceVanillaUtilityMouseTextCalls(
						il.Instrs,
						il.Import(_wrapperMouseText8),
						BindingListViewHoverRequestCallsitePlanner.ExpectedBestiaryCalls);
					RecomputeVanillaStatus();
					if (_bestiaryOutcome != BindingListViewHoverRequestMethodOutcome.Applied)
						WarnOnce("DrawBestiaryIcon transform: " + _bestiaryOutcome);
				});

				_vanillaHooksRegistered = true;
				// Initial aggregate after registrations (callbacks usually already ran sync).
				RecomputeVanillaStatus();
			}
			catch (Exception ex)
			{
				_vanillaStatus = BindingListViewHoverRequestSuppressStatus.UnsupportedTransformFailed;
				WarnOnce("vanilla transform failed: " + ex.GetType().Name + ": " + ex.Message);
			}

			TryApplyBankButtons();
		}

		public static void TryApplyBankButtons()
		{
			if (_bankHookRegistered)
				return;

			if (_bankStatus == BindingListViewHoverRequestSuppressStatus.BankApplied
			    || _bankStatus == BindingListViewHoverRequestSuppressStatus.BankUnsupported)
				return;

			if (!ModLoader.HasMod("BankButtons"))
			{
				_bankStatus = BindingListViewHoverRequestSuppressStatus.BankNotInstalled;
				return;
			}

			try
			{
				Mod bankMod = ModLoader.GetMod("BankButtons");
				Type buttonsUi = bankMod?.Code?.GetType("BankButtons.ButtonsUI");
				if (buttonsUi == null)
				{
					_bankStatus = BindingListViewHoverRequestSuppressStatus.BankUnsupported;
					WarnOnce("BankButtons.ButtonsUI type missing");
					return;
				}

				MethodInfo draw = buttonsUi.GetMethod(
					"Draw",
					BindingFlags.Instance | BindingFlags.Public,
					binder: null,
					types: new[] { typeof(SpriteBatch) },
					modifiers: null);
				if (draw == null)
				{
					_bankStatus = BindingListViewHoverRequestSuppressStatus.BankUnsupported;
					WarnOnce("BankButtons.ButtonsUI.Draw(SpriteBatch) missing");
					return;
				}

				if (_wrapperHackZoom2 == null)
				{
					_wrapperHackZoom2 = typeof(BindingListViewHoverRequestSuppressWrappers).GetMethod(
						nameof(BindingListViewHoverRequestSuppressWrappers.ConditionalMouseTextHackZoom2),
						BindingFlags.Public | BindingFlags.Static);
				}

				if (_wrapperHackZoom2 == null)
				{
					_bankStatus = BindingListViewHoverRequestSuppressStatus.BankUnsupported;
					WarnOnce("Bank wrapper MethodInfo resolve failed");
					return;
				}

				MonoModHooks.Modify(draw, il =>
				{
					_bankOutcome = BindingListViewHoverRequestSuppressEmitter.TryReplaceBankGetHoverTextHackZoomCalls(
						il.Instrs,
						il.Import(_wrapperHackZoom2),
						BindingListViewHoverRequestCallsitePlanner.ExpectedBankGetHoverTextHackZoomCalls);
					ApplyBankStatusFromOutcome();
				});

				_bankHookRegistered = true;
				// Initial bank status after registration.
				ApplyBankStatusFromOutcome();
			}
			catch (Exception ex)
			{
				_bankStatus = BindingListViewHoverRequestSuppressStatus.BankUnsupported;
				WarnOnce("BankButtons transform failed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		public static void UnapplyNote()
		{
			// tML Unload runs MonoModHooks.RemoveAll for this mod's owned manipulators.
			// Reset local status/outcomes/flags so a later Apply in-process registers cleanly.
			_vanillaHooksRegistered = false;
			_bankHookRegistered = false;
			_warned = false;
			_vanillaStatus = BindingListViewHoverRequestSuppressStatus.NotApplied;
			_bankStatus = BindingListViewHoverRequestSuppressStatus.NotApplied;
			_inventoryOutcome = BindingListViewHoverRequestMethodOutcome.NotRun;
			_emoteOutcome = BindingListViewHoverRequestMethodOutcome.NotRun;
			_bestiaryOutcome = BindingListViewHoverRequestMethodOutcome.NotRun;
			_bankOutcome = BindingListViewHoverRequestMethodOutcome.NotRun;
			_wrapperMouseText8 = null;
			_wrapperHackZoom2 = null;
		}

		private static void RecomputeVanillaStatus()
		{
			_vanillaStatus = BindingListViewHoverRequestSuppressEmitter.AggregateVanillaStatus(
				_inventoryOutcome, _emoteOutcome, _bestiaryOutcome);
		}

		private static void ApplyBankStatusFromOutcome()
		{
			if (_bankOutcome == BindingListViewHoverRequestMethodOutcome.Applied)
				_bankStatus = BindingListViewHoverRequestSuppressStatus.BankApplied;
			else
			{
				_bankStatus = BindingListViewHoverRequestSuppressStatus.BankUnsupported;
				if (_bankOutcome != BindingListViewHoverRequestMethodOutcome.NotRun)
					WarnOnce("BankButtons Draw transform: " + _bankOutcome);
			}
		}

		private static MethodInfo Resolve(Type type, string name, Type[] args)
		{
			MethodInfo mi = type.GetMethod(
				name,
				BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
				binder: null,
				types: args,
				modifiers: null);
			if (mi == null)
				throw new InvalidOperationException("Missing method " + type.FullName + "." + name);
			return mi;
		}

		private static void WarnOnce(string detail)
		{
			if (_warned)
				return;
			_warned = true;
			try
			{
				if (ModLoader.TryGetMod("AdvancedKeybinds", out Mod ak))
					ak.Logger.Warn("[ListView HoverRequestSuppress] unsupported: " + detail);
			}
			catch
			{
				// ignore
			}
		}
	}
}
