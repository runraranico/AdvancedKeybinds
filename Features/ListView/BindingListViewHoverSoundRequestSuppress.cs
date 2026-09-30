using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// Runtime MonoMod apply for DrawInventory QuickStack/Sort hover PlaySound requests,
	/// and separately for ChestUI.UpdateHover shared ButtonHovered-enter PlaySound.
	/// Inventory Status/Outcome stay independent of Chest failure (frozen QS/Sort behavior).
	/// Orig delegate is validated inside Apply try before any hook install.
	/// </summary>
	internal static class BindingListViewHoverSoundRequestSuppress
	{
		private static bool _inventoryHookRegistered;
		private static bool _chestHookRegistered;
		private static bool _warnedInventory;
		private static bool _warnedChest;

		private static BindingListViewHoverSoundRequestSuppressStatus _status;
		private static BindingListViewHoverSoundRequestSuppressStatus _chestStatus;
		private static BindingListViewHoverSoundRequestMethodOutcome _outcome;
		private static BindingListViewHoverSoundRequestMethodOutcome _chestOutcome;
		private static MethodInfo _wrapperPlaySoundInt6;

		/// <summary>DrawInventory QS/Sort transform status (unchanged contract).</summary>
		public static BindingListViewHoverSoundRequestSuppressStatus Status => _status;

		/// <summary>DrawInventory QS/Sort transform outcome (unchanged contract).</summary>
		public static BindingListViewHoverSoundRequestMethodOutcome Outcome => _outcome;

		/// <summary>ChestUI.UpdateHover transform status — independent of inventory.</summary>
		public static BindingListViewHoverSoundRequestSuppressStatus ChestStatus => _chestStatus;

		/// <summary>ChestUI.UpdateHover transform outcome — independent of inventory.</summary>
		public static BindingListViewHoverSoundRequestMethodOutcome ChestOutcome => _chestOutcome;

		public static void Apply()
		{
			if (_inventoryHookRegistered && _chestHookRegistered)
				return;

			try
			{
				// Force type init without relying on a throwing static field initializer.
				RuntimeHelpers.RunClassConstructor(
					typeof(BindingListViewHoverSoundRequestSuppressWrappers).TypeHandle);

				if (!BindingListViewHoverSoundRequestSuppressWrappers.TryInitializeOrigDelegate(out string bindError))
				{
					if (!_inventoryHookRegistered)
					{
						_status = BindingListViewHoverSoundRequestSuppressStatus.UnsupportedMissingMethod;
						WarnOnceInventory("orig PlaySoundInt6 bind failed: " + bindError);
					}
					if (!_chestHookRegistered)
					{
						_chestStatus = BindingListViewHoverSoundRequestSuppressStatus.UnsupportedMissingMethod;
						WarnOnceChest("orig PlaySoundInt6 bind failed: " + bindError);
					}
					return;
				}

				_wrapperPlaySoundInt6 = typeof(BindingListViewHoverSoundRequestSuppressWrappers).GetMethod(
					nameof(BindingListViewHoverSoundRequestSuppressWrappers.ConditionalPlaySoundInt6),
					BindingFlags.Public | BindingFlags.Static);
				if (_wrapperPlaySoundInt6 == null)
				{
					if (!_inventoryHookRegistered)
					{
						_status = BindingListViewHoverSoundRequestSuppressStatus.UnsupportedMissingMethod;
						WarnOnceInventory("wrapper MethodInfo resolve failed");
					}
					if (!_chestHookRegistered)
					{
						_chestStatus = BindingListViewHoverSoundRequestSuppressStatus.UnsupportedMissingMethod;
						WarnOnceChest("wrapper MethodInfo resolve failed");
					}
					return;
				}

				TryApplyDrawInventory();
				TryApplyChestUpdateHover();
			}
			catch (Exception ex)
			{
				// Outer catch must not clear a successfully registered inventory hook.
				if (!_inventoryHookRegistered)
				{
					_status = BindingListViewHoverSoundRequestSuppressStatus.UnsupportedTransformFailed;
					WarnOnceInventory("hover-sound transform failed: " + ex.GetType().Name + ": " + ex.Message);
				}
				if (!_chestHookRegistered)
				{
					_chestStatus = BindingListViewHoverSoundRequestSuppressStatus.UnsupportedTransformFailed;
					WarnOnceChest("hover-sound transform failed: " + ex.GetType().Name + ": " + ex.Message);
				}
			}
		}

		public static void UnapplyNote()
		{
			// tML Unload runs MonoModHooks.RemoveAll for this mod's owned manipulators.
			_inventoryHookRegistered = false;
			_chestHookRegistered = false;
			_warnedInventory = false;
			_warnedChest = false;
			_status = BindingListViewHoverSoundRequestSuppressStatus.NotApplied;
			_chestStatus = BindingListViewHoverSoundRequestSuppressStatus.NotApplied;
			_outcome = BindingListViewHoverSoundRequestMethodOutcome.NotRun;
			_chestOutcome = BindingListViewHoverSoundRequestMethodOutcome.NotRun;
			_wrapperPlaySoundInt6 = null;
		}

		private static void TryApplyDrawInventory()
		{
			if (_inventoryHookRegistered)
				return;

			MethodInfo drawInventory = Resolve(typeof(Main), "DrawInventory", Type.EmptyTypes);
			MonoModHooks.Modify(drawInventory, il =>
			{
				_outcome = BindingListViewHoverSoundRequestSuppressEmitter.TryReplaceDrawInventoryHoverSoundCalls(
					il.Instrs,
					il.Import(_wrapperPlaySoundInt6),
					BindingListViewHoverSoundRequestCallsitePlanner.ExpectedDrawInventoryHoverSoundCalls);
				_status = BindingListViewHoverSoundRequestSuppressEmitter.StatusFromOutcome(_outcome);
				if (_outcome != BindingListViewHoverSoundRequestMethodOutcome.Applied)
					WarnOnceInventory("DrawInventory hover-sound transform: " + _outcome);
			});

			_inventoryHookRegistered = true;
			_status = BindingListViewHoverSoundRequestSuppressEmitter.StatusFromOutcome(_outcome);
		}

		private static void TryApplyChestUpdateHover()
		{
			if (_chestHookRegistered)
				return;

			try
			{
				MethodInfo updateHover = Resolve(
					typeof(ChestUI),
					"UpdateHover",
					new[] { typeof(int), typeof(bool) });
				MonoModHooks.Modify(updateHover, il =>
				{
					_chestOutcome = BindingListViewHoverSoundRequestSuppressEmitter.TryReplaceChestUpdateHoverSoundCalls(
						il.Instrs,
						il.Import(_wrapperPlaySoundInt6),
						BindingListViewHoverSoundRequestCallsitePlanner.ExpectedChestUpdateHoverSoundCalls);
					_chestStatus = BindingListViewHoverSoundRequestSuppressEmitter.StatusFromOutcome(_chestOutcome);
					if (_chestOutcome != BindingListViewHoverSoundRequestMethodOutcome.Applied)
						WarnOnceChest("ChestUI.UpdateHover hover-sound transform: " + _chestOutcome);
				});

				_chestHookRegistered = true;
				_chestStatus = BindingListViewHoverSoundRequestSuppressEmitter.StatusFromOutcome(_chestOutcome);
			}
			catch (Exception ex)
			{
				_chestHookRegistered = false;
				_chestStatus = BindingListViewHoverSoundRequestSuppressStatus.UnsupportedTransformFailed;
				_chestOutcome = BindingListViewHoverSoundRequestMethodOutcome.NotRun;
				WarnOnceChest("ChestUI.UpdateHover transform failed: " + ex.GetType().Name + ": " + ex.Message);
				// Inventory Status/Outcome intentionally untouched.
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

		private static void WarnOnceInventory(string detail)
		{
			if (_warnedInventory)
				return;
			_warnedInventory = true;
			Warn("inventory: " + detail);
		}

		private static void WarnOnceChest(string detail)
		{
			if (_warnedChest)
				return;
			_warnedChest = true;
			Warn("chest: " + detail);
		}

		private static void Warn(string detail)
		{
			try
			{
				if (ModLoader.TryGetMod("AdvancedKeybinds", out Mod ak))
					ak.Logger.Warn("[ListView HoverSoundRequestSuppress] unsupported: " + detail);
			}
			catch
			{
				// ignore
			}
		}
	}
}
