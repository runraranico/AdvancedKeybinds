using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP READ-ONLY LIVE — Inventory Mouse Item hold vs draw regression.
	/// Does not mutate Main.mouseItem / mouse buttons / PlayerInput / SpriteBatch / Scissor / mouse coords.
	/// Gate: <see cref="BindingListViewLiveDiagGate.MouseItemDiagEnabled"/> (default OFF).
	/// Log: deck log\Keyboard\ListView_MouseItem_LIVE.txt
	/// </summary>
	internal static class BindingListViewMouseItemDiag
	{
		private static bool _sessionStarted;
		private static bool _flushPending;
		private static bool _captureRestOfFrame;
		private static uint _captureFrame;

		private static int _prevMouseItemType = -1;
		private static int _prevMouseItemStack = -1;
		private static bool _prevMouseItemAir = true;
		private static bool _prevMouseLeft;
		private static bool _prevMouseLeftInitialized;
		private static int _sessionSeq;
		private static readonly StringBuilder _buffer = new StringBuilder(2048);

		private static string LogPath
		{
			get
			{
				string root = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
				Directory.CreateDirectory(root);
				return Path.Combine(root, "ListView_MouseItem_LIVE.txt");
			}
		}

		/// <summary>
		/// Creates the LIVE file and writes a session header once while the gate is ON.
		/// Safe to call every frame; no-op after the first successful start.
		/// </summary>
		public static void EnsureSessionStarted()
		{
			if (!BindingListViewLiveDiagGate.MouseItemDiagEnabled)
				return;
			if (_sessionStarted)
				return;

			_sessionStarted = true;
			BindingListViewLiveDiagGate.ResetSessionCounters();

			var sb = new StringBuilder(512);
			sb.AppendLine("===== ListView MouseItem LIVE SESSION HEADER =====");
			sb.Append("timeUtc=").AppendLine(DateTime.UtcNow.ToString("o"));
			sb.Append("timeLocal=").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
			sb.Append("diag=BindingListViewMouseItemDiag").AppendLine();
			sb.Append("MouseItemDiagEnabled=true").AppendLine();
			sb.Append("purpose=read-only hold-vs-draw CASE A/B/C/D + STATE A/B/C").AppendLine();
			sb.Append("flushPolicy=sessionHeader + inventoryTransitionOnly (no per-frame idle write)").AppendLine();
			sb.Append("armPolicy=playerInventory && (leftEdge | miType | miStack | miAir change)").AppendLine();
			try
			{
				Assembly asm = typeof(BindingListViewMouseItemDiag).Assembly;
				sb.Append("assembly=").AppendLine(asm.GetName().Name ?? "");
				sb.Append("assemblyVersion=").AppendLine(asm.GetName().Version?.ToString() ?? "");
				string loc = asm.Location ?? "";
				sb.Append("assemblyLocation=").AppendLine(loc);
				if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
					sb.Append("assemblyWriteUtc=").AppendLine(File.GetLastWriteTimeUtc(loc).ToString("o"));
			}
			catch (Exception ex)
			{
				sb.Append("assemblyInfoError=").AppendLine(ex.GetType().Name);
			}

			try
			{
				Mod mod = global::AdvancedKeybinds.AdvancedKeybinds.Instance;
				if (mod != null)
				{
					sb.Append("modName=").AppendLine(mod.Name ?? "");
					sb.Append("modVersion=").AppendLine(mod.Version?.ToString() ?? "");
				}
			}
			catch (Exception ex)
			{
				sb.Append("modInfoError=").AppendLine(ex.GetType().Name);
			}

			sb.Append("gameUpdateCountAtStart=").Append(Main.GameUpdateCount).AppendLine();
			sb.AppendLine("columns=timeUtcMs\tframe\tphase\tseq\tstateHint\takOpen\tclaimFront\tplayerInventory\tmouseX,Y\tmouseItemType\tmouseItemStack\tmouseItemIsAir\tmouseItemName\tmouseLeft\tmouseLeftRelease\tcurrentMouseLeft\tjustPressedMouseLeft\tjustReleasedMouseLeft\tmouseItemLayerActive\tmouseItemLayerDrew\titemBranchLikely\tcursorScale\tscissor\tscissorTest");
			sb.AppendLine("===== END HEADER / BEGIN SAMPLES =====");

			try
			{
				File.AppendAllText(LogPath, sb.ToString());
			}
			catch
			{
				// keep _sessionStarted true to avoid header spam if path briefly fails
			}
		}

		/// <summary>
		/// Inventory processing直後 (read-only).
		/// </summary>
		public static void NoteAfterInventory(BindingListViewState state)
		{
			CapturePhase("AfterInventory", state, layerActive: true, drewLayer: false);
		}

		/// <summary>
		/// Mouse Item Layer 直前 (read-only).
		/// </summary>
		public static void CapturePreMouseItemLayer(BindingListViewState state, bool layerActive)
		{
			CapturePhase("PreMouseItemLayer", state, layerActive, drewLayer: false);
		}

		/// <summary>
		/// Mouse Item Layer wrap 内部・vanilla Draw 直前 (read-only).
		/// </summary>
		public static void CaptureWrapBeforeDraw(BindingListViewState state, bool layerActive)
		{
			CapturePhase("MouseItemLayer_WrapBeforeDraw", state, layerActive, drewLayer: false);
		}

		/// <summary>
		/// Mouse Item Layer 直後 (read-only). drewLayer = vanilla layer.Draw() 戻り値.
		/// </summary>
		public static void CapturePostMouseItemLayer(BindingListViewState state, bool layerActive, bool drawReturned)
		{
			if (!BindingListViewLiveDiagGate.MouseItemDiagEnabled)
				return;

			CapturePhase("PostMouseItemLayer", state, layerActive, drewLayer: drawReturned);

			// End-of-frame baseline sync (read-only). Enables next-frame type/stack/air edge detect.
			RememberMouseItemAndButtons();
			if (_captureRestOfFrame && Main.GameUpdateCount == _captureFrame)
			{
				_captureRestOfFrame = false;
				_flushPending = false;
			}
		}

		/// <summary>
		/// Optional explicit flush; transition path already flushes.
		/// </summary>
		public static void FlushBuffer()
		{
			if (_buffer.Length == 0)
				return;
			if (!BindingListViewLiveDiagGate.MouseItemDiagEnabled)
			{
				_buffer.Clear();
				return;
			}

			string chunk = _buffer.ToString();
			_buffer.Clear();
			try
			{
				var sw = System.Diagnostics.Stopwatch.StartNew();
				File.AppendAllText(LogPath, chunk);
				sw.Stop();
				BindingListViewLiveDiagGate.NoteAppend(chunk.Length, sw.Elapsed.TotalMilliseconds);
			}
			catch
			{
				// ignore I/O failures
			}
		}

		private static void CapturePhase(
			string phase,
			BindingListViewState state,
			bool layerActive,
			bool drewLayer)
		{
			if (!BindingListViewLiveDiagGate.MouseItemDiagEnabled)
				return;

			EnsureSessionStarted();

			// Transition follow-ups are same-frame only.
			if (_captureRestOfFrame && Main.GameUpdateCount != _captureFrame)
			{
				_captureRestOfFrame = false;
				_flushPending = false;
			}

			bool transition = DetectInventoryTransition();
			if (transition)
			{
				_flushPending = true;
				_captureRestOfFrame = true;
				_captureFrame = Main.GameUpdateCount;
			}

			bool sameFrameFollowUp = _captureRestOfFrame && Main.GameUpdateCount == _captureFrame;
			if (!transition && !sameFrameFollowUp)
			{
				// Idle frames: update edge tracker only. No buffer / no file write.
				if (!_prevMouseLeftInitialized)
				{
					_prevMouseLeft = Main.mouseLeft;
					_prevMouseLeftInitialized = true;
				}
				else
				{
					_prevMouseLeft = Main.mouseLeft;
				}

				return;
			}

			AppendSnapshot(phase, state, layerActive, drewLayer);

			// Transition (and same-frame follow-up phases): flush immediately — not every idle frame.
			if (_flushPending)
				FlushBuffer();

			// Button edge: refresh each observed phase so Left is not double-fired same frame.
			if (!_prevMouseLeftInitialized)
				_prevMouseLeftInitialized = true;
			_prevMouseLeft = Main.mouseLeft;

			// After the first transition sample this frame, lock mouseItem baseline so later
			// phases do not re-arm; they continue via _captureRestOfFrame.
			if (transition)
				RememberMouseItemOnly();
		}

		/// <summary>
		/// Arm when playerInventory and any of: left edge, type/stack/IsAir change.
		/// </summary>
		private static bool DetectInventoryTransition()
		{
			if (!_prevMouseLeftInitialized)
			{
				_prevMouseLeft = Main.mouseLeft;
				_prevMouseLeftInitialized = true;
			}

			bool leftEdge = Main.mouseLeft && !_prevMouseLeft;
			bool air = IsMouseItemAir();
			int type = air ? 0 : Main.mouseItem.type;
			int stack = air ? 0 : Main.mouseItem.stack;

			// First sample after session: treat unknown prev as transition only if inventory open
			// and something actionable is present (avoid header-only noise on every boot frame).
			bool typeChanged = type != _prevMouseItemType;
			bool stackChanged = stack != _prevMouseItemStack;
			bool airChanged = air != _prevMouseItemAir;

			if (!Main.playerInventory)
				return false;

			if (leftEdge)
				return true;
			if (typeChanged || stackChanged || airChanged)
			{
				// Ignore the one-time -1 → 0 initialization unless the item is actually non-air
				// or air flipped from a known previous state.
				if (_prevMouseItemType < 0 && air && !leftEdge)
					return false;
				return true;
			}

			return false;
		}

		private static void RememberMouseItemOnly()
		{
			bool air = IsMouseItemAir();
			_prevMouseItemAir = air;
			_prevMouseItemType = air ? 0 : Main.mouseItem.type;
			_prevMouseItemStack = air ? 0 : Main.mouseItem.stack;
		}

		private static void RememberMouseItemAndButtons()
		{
			RememberMouseItemOnly();
			_prevMouseLeft = Main.mouseLeft;
			_prevMouseLeftInitialized = true;
		}

		private static bool IsMouseItemAir()
		{
			Item mi = Main.mouseItem;
			return mi == null || mi.IsAir || mi.type <= 0 || mi.stack <= 0;
		}

		private static void AppendSnapshot(
			string phase,
			BindingListViewState state,
			bool layerActive,
			bool drewLayer)
		{
			Item mi = Main.mouseItem;
			bool air = mi == null || mi.IsAir || mi.type <= 0 || mi.stack <= 0;
			int type = air ? 0 : mi.type;
			int stack = air ? 0 : mi.stack;
			string name = air ? "" : (mi.Name ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('"', '\'');
			if (name.Length > 40)
				name = name.Substring(0, 40);

			bool claim = BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick();
			bool open = state != null && state.IsOpen;
			string stateHint = !open ? "A" : (claim ? "C" : "B");

			bool curLeft = false;
			bool jpLeft = false;
			bool jrLeft = false;
			try
			{
				curLeft = PlayerInput.Triggers.Current.MouseLeft;
				jpLeft = PlayerInput.Triggers.JustPressed.MouseLeft;
				jrLeft = PlayerInput.Triggers.JustReleased.MouseLeft;
			}
			catch
			{
				// ignore shape differences
			}

			Rectangle scissor = Rectangle.Empty;
			bool scissorTest = false;
			float cursorScale = Main.cursorScale;
			try
			{
				GraphicsDevice gd = Main.instance?.GraphicsDevice;
				if (gd != null)
				{
					scissor = gd.ScissorRectangle;
					RasterizerState rs = gd.RasterizerState;
					scissorTest = rs != null && rs.ScissorTestEnable;
				}
			}
			catch
			{
				// read-only best effort
			}

			_sessionSeq++;
			var sb = new StringBuilder(480);
			sb.Append(DateTime.UtcNow.ToString("HH:mm:ss.fff"));
			sb.Append('\t').Append(Main.GameUpdateCount);
			sb.Append('\t').Append(phase);
			sb.Append("\tseq=").Append(_sessionSeq);
			sb.Append("\tstateHint=").Append(stateHint);
			sb.Append("\takOpen=").Append(open ? 1 : 0);
			sb.Append("\tclaimFront=").Append(claim ? 1 : 0);
			sb.Append("\tplayerInventory=").Append(Main.playerInventory ? 1 : 0);
			sb.Append("\tmouse=").Append(Main.mouseX).Append(',').Append(Main.mouseY);
			sb.Append("\tmouseItemType=").Append(type);
			sb.Append("\tmouseItemStack=").Append(stack);
			sb.Append("\tmouseItemIsAir=").Append(air ? 1 : 0);
			sb.Append("\tmouseItemName=\"").Append(name).Append('"');
			sb.Append("\tmouseLeft=").Append(Main.mouseLeft ? 1 : 0);
			sb.Append("\tmouseLeftRelease=").Append(Main.mouseLeftRelease ? 1 : 0);
			sb.Append("\tcurrentMouseLeft=").Append(curLeft ? 1 : 0);
			sb.Append("\tjustPressedMouseLeft=").Append(jpLeft ? 1 : 0);
			sb.Append("\tjustReleasedMouseLeft=").Append(jrLeft ? 1 : 0);
			sb.Append("\tmouseItemLayerActive=").Append(layerActive ? 1 : 0);
			sb.Append("\tmouseItemLayerDrew=").Append(drewLayer ? 1 : 0);
			sb.Append("\titemBranchLikely=").Append(air ? 0 : 1);
			sb.Append("\tcursorScale=").Append(cursorScale.ToString("0.###"));
			sb.Append("\tscissor=").Append(scissor.X).Append(',').Append(scissor.Y)
				.Append(',').Append(scissor.Width).Append('x').Append(scissor.Height);
			sb.Append("\tscissorTest=").Append(scissorTest ? 1 : 0);
			sb.AppendLine();
			_buffer.Append(sb);
		}
	}
}
