using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Terraria;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP LIVE only — Inventory ItemSlot left-click through AK ALL KEY.
	/// Does not change product input behavior. Remove after Localization final regression.
	/// Log: deck log\Keyboard\ListView_HitDiag_FULL.txt  (lines: CLICKPATH)
	/// Gate: <see cref="BindingListViewLiveDiagGate.ClickPathDiagEnabled"/> and FileOutputEnabled (default OFF).
	/// Residual mouse/item snapshots are skipped when gated off — not only Append.
	/// </summary>
	internal static class BindingListViewClickPathDiag
	{
		private static bool _prevMouseLeft;
		private static bool _synced;
		private static int _prevMouseItemType;
		private static int _prevMouseItemStack;
		private static int _step;
		private static int _traceFrame = -1;
		private static bool _traceActive; // Inv+AK open + left activity this frame

		// Pre→Post Inventory same-frame snap
		private static bool _hasPreInv;
		private static uint _preFrame;
		private static bool _preClaim;
		private static bool _preOverWin;
		private static bool _preCalledOccupy;
		private static bool _preMIfaceAfter;
		private static bool _preBlockAfter;
		private static int _preItemType;
		private static int _preItemStack;
		private static Point _preHitUi;
		private static Rectangle _preWin;

		public static void ResetSessionCounters()
		{
			_step = 0;
			_traceActive = false;
			_hasPreInv = false;
		}

		/// <summary>
		/// PostUpdateInput: track Press/Hold/Release. Activates dense tracing for the click window.
		/// </summary>
		public static void CapturePostUpdateClick(
			BindingListViewState state,
			Point hitMouseUi,
			bool overListChrome,
			bool foregroundCovers,
			bool claimFront,
			bool acceptingInput)
		{
			// Gate before residual mouse/item snapshots — not only before Append.
			if (!BindingListViewLiveDiagGate.ClickPathDiagEnabled
			    || !BindingListViewLiveDiagGate.FileOutputEnabled)
				return;

			bool left = Main.mouseLeft;
			bool pressEdge = left && !_prevMouseLeft;
			bool releaseEdge = !left && _prevMouseLeft;
			bool hold = left && _prevMouseLeft;
			ReadMouseItem(out int itemType, out int itemStack);
			bool itemChanged = _synced && (itemType != _prevMouseItemType || itemStack != _prevMouseItemStack);

			bool listOpen = state != null && state.IsOpen;
			bool invOpen = Main.playerInventory;
			bool interesting = listOpen && invOpen && (pressEdge || releaseEdge || hold || itemChanged);

			if (interesting)
			{
				BeginTraceFrame();
				Rectangle win = BindingListViewLayout.WindowRect(state);
				Rectangle body = BindingListViewLayout.BodyRect(state);
				bool hitWin = !win.IsEmpty && win.Contains(hitMouseUi);
				bool hitList = !body.IsEmpty && body.Contains(hitMouseUi);

				var sb = NewLine("PostUpdateInput");
				sb.Append("\tevent=");
				if (pressEdge)
					sb.Append("PressEdge");
				else if (releaseEdge)
					sb.Append("ReleaseEdge");
				else if (hold)
					sb.Append("Hold");
				else
					sb.Append("ItemChanged");
				AppendMouse(sb, left, pressEdge, releaseEdge, hold);
				AppendAk(sb, state, hitMouseUi, win, body, hitWin, hitList, overListChrome, foregroundCovers, claimFront, acceptingInput);
				AppendFlags(sb, ReadMIface(), Main.blockMouse, "atPostUpdate");
				AppendMouseItem(sb, itemType, itemStack, _prevMouseItemType, _prevMouseItemStack, itemChanged);
				AppendHoverItem(sb);
				sb.AppendLine();
				ForceAppend(sb.ToString());
			}

			_prevMouseLeft = left;
			_prevMouseItemType = itemType;
			_prevMouseItemStack = itemStack;
			_synced = true;
		}

		/// <summary>TryOccupyMouseBeforeInventory — ENTER / SKIP / EXIT with reasons.</summary>
		public static void TraceTryOccupy(
			string gate, // ENTER | SKIP_LIST_CLOSED | SKIP_NO_CLAIM | EXIT_AFTER_OCCUPY | EXIT_NO_OCCUPY
			BindingListViewState state,
			Point hitMouseUi,
			Rectangle windowUi,
			Rectangle bodyUi,
			bool listOpen,
			bool overChrome,
			bool covers,
			bool claimFront,
			bool calledOccupy,
			bool mIfaceBefore,
			bool blockBefore,
			bool mIfaceAfter,
			bool blockAfter)
		{
			if (!BindingListViewLiveDiagGate.ClickPathDiagEnabled
			    || !BindingListViewLiveDiagGate.FileOutputEnabled)
				return;

			bool left = Main.mouseLeft;
			bool shouldLog = _traceActive || (listOpen && Main.playerInventory && left);
			if (!shouldLog && gate != null && gate.StartsWith("ENTER", StringComparison.Ordinal))
			{
				// ENTER once when inv+list+left even if PostUpdate missed
				if (listOpen && Main.playerInventory && left)
				{
					BeginTraceFrame();
					shouldLog = true;
				}
			}

			if (!shouldLog)
			{
				// Still keep pre-inv snap silently when left+inv for PostInventory compare
				if (listOpen && Main.playerInventory && left && gate != null && gate.StartsWith("EXIT", StringComparison.Ordinal))
					StorePreInv(claimFront, overChrome, calledOccupy, mIfaceAfter, blockAfter, hitMouseUi, windowUi);
				return;
			}

			BeginTraceFrame();
			bool hitWin = !windowUi.IsEmpty && windowUi.Contains(hitMouseUi);
			bool hitList = !bodyUi.IsEmpty && bodyUi.Contains(hitMouseUi);
			ReadMouseItem(out int itemType, out int itemStack);

			var sb = NewLine("TryOccupyMouseBeforeInventory");
			sb.Append("\tgate=").Append(gate);
			AppendMouse(sb, left, left && !_prevMouseLeft, false, left);
			AppendAk(sb, state, hitMouseUi, windowUi, bodyUi, hitWin, hitList, overChrome, covers, claimFront, hitList && claimFront);
			sb.Append("\tcalledOccupy=").Append(calledOccupy);
			sb.Append("\tmIfaceBefore=").Append(mIfaceBefore).Append("\tmIfaceAfter=").Append(mIfaceAfter);
			sb.Append("\tblockBefore=").Append(blockBefore).Append("\tblockAfter=").Append(blockAfter);
			AppendMouseItem(sb, itemType, itemStack, _prevMouseItemType, _prevMouseItemStack, false);
			AppendHypothesis(sb, listOpen, hitWin, overChrome, covers, claimFront, calledOccupy, mIfaceAfter, blockAfter);
			AppendHoverItem(sb);
			sb.AppendLine();
			ForceAppend(sb.ToString());

			if (gate != null && gate.StartsWith("EXIT", StringComparison.Ordinal))
				StorePreInv(claimFront, overChrome, calledOccupy, mIfaceAfter, blockAfter, hitMouseUi, windowUi);
		}

		/// <summary>OccupyFrontWindowMouse — ENTER / EXIT (CLAIM only path).</summary>
		public static void TraceOccupyFront(string gate, string reason, bool blockWheel, bool mIfaceBefore, bool blockBefore, bool mIfaceAfter, bool blockAfter)
		{
			if (!BindingListViewLiveDiagGate.ClickPathDiagEnabled
			    || !BindingListViewLiveDiagGate.FileOutputEnabled
			    || !_traceActive)
				return;

			var sb = NewLine("OccupyFrontWindowMouse");
			sb.Append("\tgate=").Append(gate);
			sb.Append("\treason=").Append(reason ?? "");
			sb.Append("\tblockWheel=").Append(blockWheel);
			sb.Append("\tmIfaceBefore=").Append(mIfaceBefore).Append("\tmIfaceAfter=").Append(mIfaceAfter);
			sb.Append("\tblockBefore=").Append(blockBefore).Append("\tblockAfter=").Append(blockAfter);
			sb.Append("\tmouseLeft=").Append(Main.mouseLeft);
			sb.AppendLine();
			ForceAppend(sb.ToString());
		}

		/// <summary>Fired when ItemSlot.LeftClick was skipped by BindingListViewItemSlotGuard.</summary>
		public static void TraceItemSlotLeftClickBlocked(string overload, int context, int slot)
		{
			if (!BindingListViewLiveDiagGate.ClickPathDiagEnabled
			    || !BindingListViewLiveDiagGate.FileOutputEnabled)
				return;

			bool listOpen = true;
			bool invOpen = Main.playerInventory;
			bool left = Main.mouseLeft;
			if (!_traceActive && !(listOpen && invOpen && left))
				return;

			BeginTraceFrame();
			ReadMouseItem(out int itemType, out int itemStack);
			var sb = NewLine("ItemSlotLeftClickBlocked");
			sb.Append("\toverload=").Append(overload);
			sb.Append("\tcontext=").Append(context);
			sb.Append("\tslot=").Append(slot);
			sb.Append("\tmouseLeft=").Append(left);
			sb.Append("\tinvOpen=").Append(invOpen);
			AppendFlags(sb, ReadMIface(), Main.blockMouse, "atItemSlotBlock");
			AppendMouseItem(sb, itemType, itemStack, _prevMouseItemType, _prevMouseItemStack, false);
			sb.Append("\tRESULT=LEFTCLICK_BLOCKED\tclassHint=F_guard");
			sb.AppendLine();
			ForceAppend(sb.ToString());
		}

		/// <summary>Layer immediately after Vanilla: Inventory.</summary>
		public static void CapturePostInventoryLayer(BindingListViewState state)
		{
			if (!BindingListViewLiveDiagGate.ClickPathDiagEnabled
			    || !BindingListViewLiveDiagGate.FileOutputEnabled
			    || state == null
			    || !state.IsOpen)
				return;
			if (!_hasPreInv || _preFrame != Main.GameUpdateCount)
				return;
			if (!_traceActive && !Main.mouseLeft)
				return;

			ReadMouseItem(out int itemType, out int itemStack);
			bool mIface = ReadMIface();
			bool block = Main.blockMouse;
			bool itemChanged = itemType != _preItemType || itemStack != _preItemStack;

			var sb = NewLine("PostInventoryLayer");
			sb.Append("\tpreClaimFront=").Append(_preClaim);
			sb.Append("\tpreOverChrome=").Append(_preOverWin);
			sb.Append("\tpreCalledOccupy=").Append(_preCalledOccupy);
			sb.Append("\tpreMIfaceAfter=").Append(_preMIfaceAfter);
			sb.Append("\tpreBlockAfter=").Append(_preBlockAfter);
			sb.Append("\tpostMIface=").Append(mIface);
			sb.Append("\tpostBlock=").Append(block);
			sb.Append("\tmIfaceCleared=").Append(_preMIfaceAfter && !mIface);
			sb.Append("\tblockCleared=").Append(_preBlockAfter && !block);
			AppendMouseItem(sb, itemType, itemStack, _preItemType, _preItemStack, itemChanged);
			AppendHoverItem(sb);
			sb.Append("\tpreHitUi=").Append(_preHitUi.X).Append(',').Append(_preHitUi.Y);
			sb.Append("\tpreWin=").Append(FmtRect(_preWin));
			sb.Append("\tmouseLeft=").Append(Main.mouseLeft);
			sb.Append("\tinv=").Append(Main.playerInventory);

			if (_preClaim && _preCalledOccupy && itemChanged)
				sb.Append("\tRESULT=GRAB_DESPITE_OCCUPY\tclassHint=F_or_E");
			else if (!_preClaim && itemChanged && Main.mouseLeft)
				sb.Append("\tRESULT=GRAB_WITHOUT_CLAIM\tclassHint=A_or_B_or_G");
			else if (_preClaim && _preCalledOccupy && !itemChanged && Main.mouseLeft)
				sb.Append("\tRESULT=OCCUPY_HELD_NO_GRAB\tclassHint=occupy_ok_this_frame");
			else if (_preMIfaceAfter && !mIface)
				sb.Append("\tRESULT=MIFACE_CLEARED\tclassHint=E");
			else if (itemChanged)
				sb.Append("\tRESULT=ITEM_CHANGED\tclassHint=unknown");
			else
				sb.Append("\tRESULT=NO_ITEM_CHANGE");

			if (_preMIfaceAfter && !mIface)
				sb.Append("\tNOTE=mIface_cleared_during_or_after_Inventory");
			if (_preBlockAfter && !block)
				sb.Append("\tNOTE=blockMouse_cleared_during_or_after_Inventory");
			if (_preClaim && _preCalledOccupy && _preMIfaceAfter && _preBlockAfter && itemChanged)
				sb.Append("\tNOTE=flags_were_true_pre_Inventory_but_grab_happened");

			sb.AppendLine();
			ForceAppend(sb.ToString());
		}

		// --- helpers ---

		private static void BeginTraceFrame()
		{
			int f = (int)Main.GameUpdateCount;
			if (f != _traceFrame)
			{
				_traceFrame = f;
				_step = 0;
			}

			_traceActive = true;
		}

		private static void StorePreInv(
			bool claim,
			bool over,
			bool calledOccupy,
			bool mIfaceAfter,
			bool blockAfter,
			Point hitUi,
			Rectangle win)
		{
			ReadMouseItem(out int t, out int s);
			_hasPreInv = true;
			_preFrame = Main.GameUpdateCount;
			_preClaim = claim;
			_preOverWin = over;
			_preCalledOccupy = calledOccupy;
			_preMIfaceAfter = mIfaceAfter;
			_preBlockAfter = blockAfter;
			_preItemType = t;
			_preItemStack = s;
			_preHitUi = hitUi;
			_preWin = win;
		}

		private static StringBuilder NewLine(string phase)
		{
			var sb = new StringBuilder(768);
			sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
			sb.Append("\tCLICKPATH");
			sb.Append("\tstep=").Append(++_step);
			sb.Append("\tphase=").Append(phase);
			sb.Append("\tframe=").Append(Main.GameUpdateCount);
			return sb;
		}

		private static void AppendMouse(StringBuilder sb, bool left, bool press, bool release, bool hold)
		{
			sb.Append("\tmouseLeft=").Append(left);
			sb.Append("\tprevMouseLeft=").Append(_prevMouseLeft);
			sb.Append("\tpressEdge=").Append(press);
			sb.Append("\treleaseEdge=").Append(release);
			sb.Append("\thold=").Append(hold);
			sb.Append("\tMainMouse=").Append(Main.mouseX).Append(',').Append(Main.mouseY);
		}

		private static void AppendAk(
			StringBuilder sb,
			BindingListViewState state,
			Point hitUi,
			Rectangle win,
			Rectangle body,
			bool hitWin,
			bool hitList,
			bool overChrome,
			bool covers,
			bool claimFront,
			bool accepting)
		{
			sb.Append("\tlistOpen=").Append(state != null && state.IsOpen);
			sb.Append("\tmax=").Append(state != null && state.IsMaximized);
			sb.Append("\tmode=").Append(state != null ? state.ContentMode.ToString() : "?");
			sb.Append("\tinvOpen=").Append(Main.playerInventory);
			sb.Append("\thitUi=").Append(hitUi.X).Append(',').Append(hitUi.Y);
			sb.Append("\twin=").Append(FmtRect(win));
			sb.Append("\tbody=").Append(FmtRect(body));
			sb.Append("\thitWin=").Append(hitWin);
			sb.Append("\thitList=").Append(hitList);
			sb.Append("\toverChrome=").Append(overChrome);
			sb.Append("\tcovers=").Append(covers);
			sb.Append("\tclaimFront=").Append(claimFront);
			sb.Append("\tAcc=").Append(accepting);
			sb.Append("\tUIScale=").Append(Main.UIScale.ToString("0.###"));
			sb.Append("\tscr=").Append(Main.screenWidth).Append('x').Append(Main.screenHeight);
		}

		private static void AppendFlags(StringBuilder sb, bool mIface, bool block, string tag)
		{
			sb.Append("\tmIface=").Append(mIface);
			sb.Append("\tblockMouse=").Append(block);
			sb.Append("\tflagsAt=").Append(tag);
		}

		private static void AppendMouseItem(StringBuilder sb, int type, int stack, int prevType, int prevStack, bool changed)
		{
			sb.Append("\tmouseItem=").Append(type).Append('x').Append(stack);
			sb.Append("\tprevMouseItem=").Append(prevType).Append('x').Append(prevStack);
			sb.Append("\titemChanged=").Append(changed);
		}

		/// <summary>Best-effort ItemSlot hover observation without patching Terraria.</summary>
		private static void AppendHoverItem(StringBuilder sb)
		{
			try
			{
				Item h = Main.HoverItem;
				if (h == null || h.IsAir)
					sb.Append("\thoverItem=0x0");
				else
					sb.Append("\thoverItem=").Append(h.type).Append('x').Append(h.stack);
			}
			catch
			{
				sb.Append("\thoverItem=?");
			}
		}

		private static void AppendHypothesis(
			StringBuilder sb,
			bool listOpen,
			bool hitWin,
			bool overChrome,
			bool covers,
			bool claimFront,
			bool calledOccupy,
			bool mIfaceAfter,
			bool blockAfter)
		{
			sb.Append("\tclassHint=");
			if (!listOpen)
				sb.Append('C');
			else if (!hitWin && !overChrome)
				sb.Append("A_or_G");
			else if (covers)
				sb.Append('B');
			else if (!claimFront)
				sb.Append("A_or_B");
			else if (!calledOccupy)
				sb.Append('C');
			else if (!mIfaceAfter || !blockAfter)
				sb.Append('D');
			else
				sb.Append("D_ok_wait_PostInventory_for_E_F_H");
		}

		private static string FmtRect(Rectangle r)
			=> r.IsEmpty ? "empty" : (r.X + "," + r.Y + "-" + r.Right + "," + r.Bottom);

		private static bool ReadMIface()
		{
			try
			{
				if (Main.myPlayer < 0 || Main.myPlayer >= Main.player.Length)
					return false;
				Player p = Main.LocalPlayer;
				return p != null && p.active && p.mouseInterface;
			}
			catch
			{
				return false;
			}
		}

		private static void ReadMouseItem(out int type, out int stack)
		{
			try
			{
				Item it = Main.mouseItem;
				if (it == null || it.IsAir)
				{
					type = 0;
					stack = 0;
					return;
				}

				type = it.type;
				stack = it.stack;
			}
			catch
			{
				type = -1;
				stack = -1;
			}
		}

		private static void ForceAppend(string line)
		{
			if (!BindingListViewLiveDiagGate.ClickPathDiagEnabled
			    || !BindingListViewLiveDiagGate.FileOutputEnabled)
				return;

			try
			{
				string dir = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games",
					"Terraria",
					"tModLoader",
					"deck log",
					"Keyboard");
				Directory.CreateDirectory(dir);
				var sw = System.Diagnostics.Stopwatch.StartNew();
				File.AppendAllText(Path.Combine(dir, "ListView_HitDiag_FULL.txt"), line, Encoding.UTF8);
				sw.Stop();
				BindingListViewLiveDiagGate.NoteAppend(
					line != null ? Encoding.UTF8.GetByteCount(line) : 0,
					sw.Elapsed.TotalMilliseconds);
			}
			catch
			{
				// never break gameplay
			}
		}
	}
}
