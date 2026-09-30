using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.UI.Chat;
using Terraria.GameContent;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP: full dump for hit-test investigation. Remove after fix confirmed.
	/// File: deck log\Keyboard\ListView_HitDiag_FULL.txt
	/// </summary>
	internal static class BindingListViewHitDiag
	{
		private const bool Enabled = true;
		private const int LogThrottleMs = 40;
		/// <summary>Raised hard — truncation was destroying useful sessions. 80MB.</summary>
		private const long MaxBytes = 80L * 1024L * 1024L;
		private const long KeepBytesAfterTrim = 60L * 1024L * 1024L;

		private static string _logPath;
		private static bool _sessionStarted;
		private static DateTime _lastLogUtc = DateTime.MinValue;
		private static string _lastSignature = string.Empty;
		private static bool? _lastAccepting;
		private static int _seq;
		private static int _lastWheelLoggedFrame = -1;

		private static Sample _postInput;
		private static Sample _uiLayer;
		private static bool _hasPostInput;
		private static bool _hasUiLayer;

		private struct Sample
		{
			public string Phase;
			public uint GameFrame;
			public int RawMainX;
			public int RawMainY;
			public int HitUiX;
			public int HitUiY;
			public int PiMouseX;
			public int PiMouseY;
			public int XnaMouseX;
			public int XnaMouseY;
			public int LastMouseX;
			public int LastMouseY;
			public int ScreenW;
			public int ScreenH;
			public float UiScale;
			public float GameZoom;
			public bool HasFocus;
			public bool PlayerInventory;
			public bool MapFullscreen;
			public bool InGameUiBusy;
			public Rectangle WindowUi;
			public Rectangle BodyUi;
			public Rectangle TitleUi;
			public bool RawDirectBody;
			public bool RawDivScaleBody;
			public bool RawMulScaleBody;
			public bool HitContainsBody;
			public bool HitContainsWindow;
			public bool HitContainsTitle;
			public bool Accepting;
			public bool MouseInterface;
			public bool SuppressHotbar;
			public int FrozenHotbar;
			public bool OwnsWheelFocus;
			public bool WheelFocusSuspended;
			public bool PointerOverWindowCached;
			public int ScrollDelta;
			public int ScrollDeltaUi;
			public bool WheelMoved;
			public float ScrollOffsetUi;
			public float MaxScrollUi;
			public int SelectedItem;
			public bool ListOpen;
			public bool ListMaximized;
			public string ContentMode;
			public float PosUiX;
			public float PosUiY;
			public float WidthUi;
			public float BodyHeightUi;
			public int HitDxLeft;
			public int HitDxRight;
			public int HitDyTop;
			public int HitDyBottom;
			public int RawDxLeft;
			public int RawDxRight;
			public string HitEdgeTag;
			public string RawEdgeTag;
			public string MainOverHitRatio;
		}

		public static void BeginSession(string reason)
		{
			if (!Enabled || !BindingListViewLiveDiagGate.FileOutputEnabled)
				return;
			try
			{
				EnsureLogPath();
				_sessionStarted = false;
				_seq = 0;
				_lastAccepting = null;
				_lastSignature = string.Empty;
				_lastWheelLoggedFrame = -1;
				_hasPostInput = false;
				_hasUiLayer = false;
				WriteSessionHeader(reason ?? "BeginSession");
				_sessionStarted = true;
			}
			catch
			{
				// ignore
			}
		}

		public static void CapturePostUpdateInput(
			BindingListViewState state,
			Point rawMouse,
			Point hitMouseUi,
			bool acceptingInput,
			bool suppressHotbar,
			int frozenHotbar,
			bool ownsWheelFocus,
			bool wheelFocusSuspended,
			bool pointerOverWindowCached,
			int scrollDeltaBeforeConsume = 0,
			int scrollDeltaUiBeforeConsume = 0)
		{
			if (!Enabled || !BindingListViewLiveDiagGate.FileOutputEnabled || state == null || !state.IsOpen)
				return;

			EnsureSessionOnce("first-capture-while-open");
			_postInput = BuildSample(
				"PostUpdateInput",
				state,
				rawMouse,
				hitMouseUi,
				acceptingInput,
				suppressHotbar,
				frozenHotbar,
				ownsWheelFocus,
				wheelFocusSuspended,
				pointerOverWindowCached,
				scrollDeltaBeforeConsume,
				scrollDeltaUiBeforeConsume);
			_hasPostInput = true;

			bool edge = _lastAccepting == null || acceptingInput != _lastAccepting.Value;
			bool wheel = _postInput.WheelMoved;
			MaybeAppendLog(_postInput, force: edge || wheel);
			if (edge)
				AppendEdgeBlock("AccChanged", _lastAccepting, acceptingInput);
			_lastAccepting = acceptingInput;
		}

		public static void CaptureUiLayer(
			BindingListViewState state,
			Point rawMouse,
			Point hitMouseUi,
			bool acceptingInput,
			bool suppressHotbar,
			int frozenHotbar,
			bool ownsWheelFocus,
			bool wheelFocusSuspended,
			bool pointerOverWindowCached,
			int scrollDeltaBeforeConsume = 0,
			int scrollDeltaUiBeforeConsume = 0)
		{
			if (!Enabled || !BindingListViewLiveDiagGate.FileOutputEnabled || state == null || !state.IsOpen)
				return;

			EnsureSessionOnce("first-capture-while-open");
			_uiLayer = BuildSample(
				"UILayer",
				state,
				rawMouse,
				hitMouseUi,
				acceptingInput,
				suppressHotbar,
				frozenHotbar,
				ownsWheelFocus,
				wheelFocusSuspended,
				pointerOverWindowCached,
				scrollDeltaBeforeConsume,
				scrollDeltaUiBeforeConsume);
			_hasUiLayer = true;
			MaybeAppendLog(_uiLayer, force: _uiLayer.WheelMoved);

			if (_hasPostInput)
				AppendPairSummary();
		}

		public static void DrawOverlay(SpriteBatch sb)
		{
			if (!Enabled || !BindingListViewLiveDiagGate.FileOutputEnabled || sb == null)
				return;
			if (!_hasPostInput && !_hasUiLayer)
				return;

			Sample s = _hasUiLayer ? _uiLayer : _postInput;
			var sbText = new StringBuilder(720);
			sbText.AppendLine("FULL log → deck log\\Keyboard\\ListView_HitDiag_FULL.txt (80MB cap)");
			sbText.Append("UIScale=").Append(s.UiScale.ToString("0.###"));
			sbText.Append(" Acc=").Append(s.Accepting);
			sbText.Append(" mIface=").Append(s.MouseInterface);
			sbText.Append(" hitEdge=").Append(s.HitEdgeTag);
			sbText.Append(" hitWin=").Append(s.HitContainsWindow);
			sbText.Append(" inv=").Append(s.PlayerInventory);
			sbText.Append(" frame=").Append(s.GameFrame);
			sbText.AppendLine();
			sbText.AppendLine("CLICKPATH: Inv+AK → left-click slot through window → tell agent");

			if (_hasPostInput)
			{
				sbText.Append("[Post] Main=").Append(_postInput.RawMainX).Append(',').Append(_postInput.RawMainY);
				sbText.Append(" hitUi=").Append(_postInput.HitUiX).Append(',').Append(_postInput.HitUiY);
				sbText.Append(" Acc=").Append(_postInput.Accepting);
				sbText.Append(" scr=").Append(_postInput.ScreenW).Append('x').Append(_postInput.ScreenH);
				sbText.AppendLine();
			}

			if (_hasUiLayer)
			{
				sbText.Append("[UI] Main=").Append(_uiLayer.RawMainX).Append(',').Append(_uiLayer.RawMainY);
				sbText.Append(" hitUi=").Append(_uiLayer.HitUiX).Append(',').Append(_uiLayer.HitUiY);
				sbText.Append(" Acc=").Append(_uiLayer.Accepting);
				sbText.Append(" scr=").Append(_uiLayer.ScreenW).Append('x').Append(_uiLayer.ScreenH);
				sbText.AppendLine();
			}

			if (_hasPostInput && _hasUiLayer)
			{
				sbText.Append("PAIR AccMatch=")
					.Append(_postInput.Accepting == _uiLayer.Accepting);
				if (_postInput.Accepting != _uiLayer.Accepting)
					sbText.Append(" ANOMALY");
				sbText.AppendLine();
			}

			ChatManager.DrawColorCodedStringWithShadow(
				sb,
				FontAssets.MouseText.Value,
				sbText.ToString(),
				new Vector2(12f, 120f),
				Color.Yellow,
				0f,
				Vector2.Zero,
				new Vector2(0.8f));

			DrawRectBorder(sb, s.BodyUi, Color.Lime * 0.9f);
		}

		private static Sample BuildSample(
			string phase,
			BindingListViewState state,
			Point raw,
			Point hitUi,
			bool acceptingInput,
			bool suppressHotbar,
			int frozenHotbar,
			bool ownsWheelFocus,
			bool wheelFocusSuspended,
			bool pointerOverWindowCached,
			int scrollDeltaBeforeConsume,
			int scrollDeltaUiBeforeConsume)
		{
			Rectangle win = BindingListViewLayout.WindowRect(state);
			Rectangle body = BindingListViewLayout.BodyRect(state);
			Rectangle title = BindingListViewLayout.TitleBarRect(state);
			float scale = Main.UIScale;
			if (scale <= 0.01f)
				scale = 1f;

			int xnaX = 0, xnaY = 0;
			try
			{
				MouseState ms = Mouse.GetState();
				xnaX = ms.X;
				xnaY = ms.Y;
			}
			catch { /* ignore */ }

			int piX = 0, piY = 0;
			try
			{
				piX = PlayerInput.MouseX;
				piY = PlayerInput.MouseY;
			}
			catch { /* ignore */ }

			bool mouseInterface = false;
			int selected = -1;
			try
			{
				if (Main.myPlayer >= 0 && Main.myPlayer < Main.player.Length)
				{
					Player p = Main.LocalPlayer;
					if (p != null)
					{
						mouseInterface = p.mouseInterface;
						selected = p.selectedItem;
					}
				}
			}
			catch { /* ignore */ }

			bool inGameUi = false;
			try
			{
				inGameUi = Main.InGameUI != null && Main.InGameUI.CurrentState != null;
			}
			catch { /* ignore */ }

			float zoom = 1f;
			try { zoom = Main.GameZoomTarget; }
			catch { zoom = 1f; }

			var divPt = new Point((int)(raw.X / scale), (int)(raw.Y / scale));
			var mulPt = new Point((int)(raw.X * scale), (int)(raw.Y * scale));

			// Prefer pre-consume deltas (inside region zeros PlayerInput before Capture).
			int scroll = scrollDeltaBeforeConsume;
			int scrollUi = scrollDeltaUiBeforeConsume;
			if (scroll == 0 && scrollUi == 0)
			{
				scroll = PlayerInput.ScrollWheelDelta;
				scrollUi = PlayerInput.ScrollWheelDeltaForUI;
			}

			bool wheelMoved = scroll != 0 || scrollUi != 0;

			ComputeEdgeDeltas(hitUi, body, out int hL, out int hR, out int hT, out int hB);
			ComputeEdgeDeltas(raw, body, out int rL, out int rR, out int rT, out int rB);

			string ratio = "-";
			if (hitUi.X != 0)
				ratio = (raw.X / (double)hitUi.X).ToString("0.###") + "," +
					(hitUi.Y != 0 ? (raw.Y / (double)hitUi.Y).ToString("0.###") : "-");

			return new Sample
			{
				Phase = phase,
				GameFrame = Main.GameUpdateCount,
				RawMainX = raw.X,
				RawMainY = raw.Y,
				HitUiX = hitUi.X,
				HitUiY = hitUi.Y,
				PiMouseX = piX,
				PiMouseY = piY,
				XnaMouseX = xnaX,
				XnaMouseY = xnaY,
				LastMouseX = Main.lastMouseX,
				LastMouseY = Main.lastMouseY,
				ScreenW = Main.screenWidth,
				ScreenH = Main.screenHeight,
				UiScale = scale,
				GameZoom = zoom,
				HasFocus = Main.hasFocus,
				PlayerInventory = Main.playerInventory,
				MapFullscreen = Main.mapFullscreen,
				InGameUiBusy = inGameUi,
				WindowUi = win,
				BodyUi = body,
				TitleUi = title,
				RawDirectBody = !body.IsEmpty && body.Contains(raw),
				RawDivScaleBody = !body.IsEmpty && body.Contains(divPt),
				RawMulScaleBody = !body.IsEmpty && body.Contains(mulPt),
				HitContainsBody = !body.IsEmpty && body.Contains(hitUi),
				HitContainsWindow = !win.IsEmpty && win.Contains(hitUi),
				HitContainsTitle = !title.IsEmpty && title.Contains(hitUi),
				Accepting = acceptingInput,
				MouseInterface = mouseInterface,
				SuppressHotbar = suppressHotbar,
				FrozenHotbar = frozenHotbar,
				OwnsWheelFocus = ownsWheelFocus,
				WheelFocusSuspended = wheelFocusSuspended,
				PointerOverWindowCached = pointerOverWindowCached,
				ScrollDelta = scroll,
				ScrollDeltaUi = scrollUi,
				WheelMoved = wheelMoved,
				ScrollOffsetUi = state.ScrollOffsetUi,
				MaxScrollUi = state.MaxScrollUi,
				SelectedItem = selected,
				ListOpen = state.IsOpen,
				ListMaximized = state.IsMaximized,
				ContentMode = state.ContentMode.ToString(),
				PosUiX = state.PositionUi.X,
				PosUiY = state.PositionUi.Y,
				WidthUi = state.WidthUi,
				BodyHeightUi = state.BodyHeightUi,
				HitDxLeft = hL,
				HitDxRight = hR,
				HitDyTop = hT,
				HitDyBottom = hB,
				RawDxLeft = rL,
				RawDxRight = rR,
				HitEdgeTag = EdgeTag(hL, hR, hT, hB),
				RawEdgeTag = EdgeTag(rL, rR, rT, rB),
				MainOverHitRatio = ratio
			};
		}

		/// <summary>
		/// Positive = inside past that edge; negative = outside past that edge.
		/// dxLeft = x - body.Left (neg = left of body).
		/// dxRight = body.Right - 1 - x (neg = right of body).
		/// </summary>
		private static void ComputeEdgeDeltas(
			Point p, Rectangle body, out int dxL, out int dxR, out int dyT, out int dyB)
		{
			if (body.IsEmpty)
			{
				dxL = dxR = dyT = dyB = int.MinValue;
				return;
			}

			dxL = p.X - body.Left;
			dxR = body.Right - 1 - p.X;
			dyT = p.Y - body.Top;
			dyB = body.Bottom - 1 - p.Y;
		}

		private static string EdgeTag(int dxL, int dxR, int dyT, int dyB)
		{
			if (dxL == int.MinValue)
				return "no-body";
			bool inside = dxL >= 0 && dxR >= 0 && dyT >= 0 && dyB >= 0;
			if (inside)
			{
				int min = Math.Min(Math.Min(dxL, dxR), Math.Min(dyT, dyB));
				if (min <= 1)
					return "INSIDE_EDGE1px";
				return "INSIDE";
			}

			if (dxL < 0 && dxR >= 0)
				return "OUT_LEFT_" + (-dxL) + "px";
			if (dxR < 0 && dxL >= 0)
				return "OUT_RIGHT_" + (-dxR) + "px";
			if (dyT < 0 && dyB >= 0)
				return "OUT_TOP_" + (-dyT) + "px";
			if (dyB < 0 && dyT >= 0)
				return "OUT_BOTTOM_" + (-dyB) + "px";
			return "OUT_CORNER";
		}

		private static void MaybeAppendLog(Sample s, bool force)
		{
			string sig = s.Phase + "|" + s.HitUiX + "|" + s.HitUiY + "|" + s.Accepting + "|"
				+ s.RawMainX + "|" + s.HasFocus + "|" + s.ScreenW + "|" + s.WheelMoved + "|"
				+ s.HitEdgeTag;
			DateTime now = DateTime.UtcNow;
			if (!force && sig == _lastSignature && (now - _lastLogUtc).TotalMilliseconds < LogThrottleMs)
				return;
			_lastSignature = sig;
			_lastLogUtc = now;

			if (s.WheelMoved)
			{
				int frame = (int)s.GameFrame;
				if (frame == _lastWheelLoggedFrame && !force)
				{
					// still log but avoid identical spam handled by sig
				}
				else
					_lastWheelLoggedFrame = frame;
			}

			AppendLine(FormatSampleLine(s));
		}

		private static void AppendEdgeBlock(string why, bool? prevAcc, bool newAcc)
		{
			var sb = new StringBuilder(1024);
			sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
			sb.Append("\tEDGE");
			sb.Append("\tseq=").Append(++_seq);
			sb.Append("\twhy=").Append(why);
			sb.Append("\tprevAcc=").Append(prevAcc.HasValue ? prevAcc.Value.ToString() : "null");
			sb.Append("\tnewAcc=").Append(newAcc);
			if (_hasPostInput)
			{
				sb.Append("\tPostMain=").Append(_postInput.RawMainX).Append(',').Append(_postInput.RawMainY);
				sb.Append("\tPostHitUi=").Append(_postInput.HitUiX).Append(',').Append(_postInput.HitUiY);
				sb.Append("\tPostEdge=").Append(_postInput.HitEdgeTag);
				sb.Append("\tPostRawEdge=").Append(_postInput.RawEdgeTag);
				sb.Append("\tPostHitDxL=").Append(_postInput.HitDxLeft);
				sb.Append("\tPostHitDxR=").Append(_postInput.HitDxRight);
				sb.Append("\tPostRatio=").Append(_postInput.MainOverHitRatio);
				sb.Append("\tPostScr=").Append(_postInput.ScreenW).Append('x').Append(_postInput.ScreenH);
			}

			if (_hasUiLayer)
			{
				sb.Append("\tUiMain=").Append(_uiLayer.RawMainX).Append(',').Append(_uiLayer.RawMainY);
				sb.Append("\tUiHitUi=").Append(_uiLayer.HitUiX).Append(',').Append(_uiLayer.HitUiY);
				sb.Append("\tUiEdge=").Append(_uiLayer.HitEdgeTag);
				sb.Append("\tUiScr=").Append(_uiLayer.ScreenW).Append('x').Append(_uiLayer.ScreenH);
			}

			if (_hasPostInput)
			{
				sb.Append("\tbody=").Append(FormatRect(_postInput.BodyUi));
				sb.Append("\tBAD_PostRawDirect=").Append(_postInput.RawDirectBody);
				sb.Append("\tBAD_PostRawMul=").Append(_postInput.RawMulScaleBody);
				sb.Append("\thotbar=").Append(_postInput.SelectedItem);
				sb.Append("\tsup=").Append(_postInput.SuppressHotbar);
				sb.Append("\tmIface=").Append(_postInput.MouseInterface);
				sb.Append("\tinv=").Append(_postInput.PlayerInventory);
				sb.Append("\tfocus=").Append(_postInput.HasFocus);
			}

			sb.AppendLine();
			AppendLine(sb.ToString());
		}

		private static void AppendPairSummary()
		{
			if (!_hasPostInput || !_hasUiLayer)
				return;

			var sb = new StringBuilder(768);
			sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
			sb.Append("\tPAIR");
			sb.Append("\tseq=").Append(++_seq);
			sb.Append("\tframe=").Append(_postInput.GameFrame);
			sb.Append("\tAccPost=").Append(_postInput.Accepting);
			sb.Append("\tAccUi=").Append(_uiLayer.Accepting);
			sb.Append("\tAccMatch=").Append(_postInput.Accepting == _uiLayer.Accepting);
			sb.Append("\tPostMain=").Append(_postInput.RawMainX).Append(',').Append(_postInput.RawMainY);
			sb.Append("\tPostHitUi=").Append(_postInput.HitUiX).Append(',').Append(_postInput.HitUiY);
			sb.Append("\tPostEdge=").Append(_postInput.HitEdgeTag);
			sb.Append("\tPostHitDxL/R=").Append(_postInput.HitDxLeft).Append('/').Append(_postInput.HitDxRight);
			sb.Append("\tUiMain=").Append(_uiLayer.RawMainX).Append(',').Append(_uiLayer.RawMainY);
			sb.Append("\tUiHitUi=").Append(_uiLayer.HitUiX).Append(',').Append(_uiLayer.HitUiY);
			sb.Append("\tUiEdge=").Append(_uiLayer.HitEdgeTag);
			sb.Append("\tbody=").Append(FormatRect(_postInput.BodyUi));
			sb.Append("\tratioPost=").Append(_postInput.MainOverHitRatio);
			sb.Append("\tBAD_PostRawDirect=").Append(_postInput.RawDirectBody);
			sb.Append("\tBAD_PostRawMul=").Append(_postInput.RawMulScaleBody);
			sb.Append("\tBAD_PostRawDiv=").Append(_postInput.RawDivScaleBody);
			sb.Append("\thitTitle=").Append(_postInput.HitContainsTitle);
			sb.Append("\tmIfacePost=").Append(_postInput.MouseInterface);
			sb.Append("\tmIfaceUi=").Append(_uiLayer.MouseInterface);
			sb.Append("\tsup=").Append(_postInput.SuppressHotbar);
			sb.Append("\tfrozen=").Append(_postInput.FrozenHotbar);
			sb.Append("\tfocus=").Append(_postInput.HasFocus);
			sb.Append("\tinv=").Append(_postInput.PlayerInventory);
			sb.Append("\tinGameUI=").Append(_postInput.InGameUiBusy);
			sb.Append("\twheel=").Append(_postInput.ScrollDelta).Append('/').Append(_postInput.ScrollDeltaUi);
			sb.Append("\twheelMoved=").Append(_postInput.WheelMoved);
			sb.Append("\tscrollOff=").Append(_postInput.ScrollOffsetUi.ToString("0.#"));
			sb.Append("\tmaxScroll=").Append(_postInput.MaxScrollUi.ToString("0.#"));
			sb.Append("\thotbar=").Append(_postInput.SelectedItem);
			sb.Append("\tmode=").Append(_postInput.ContentMode);
			sb.Append("\tUIScale=").Append(_postInput.UiScale.ToString("0.###"));
			sb.Append("\tscrPost=").Append(_postInput.ScreenW).Append('x').Append(_postInput.ScreenH);
			sb.Append("\tscrUi=").Append(_uiLayer.ScreenW).Append('x').Append(_uiLayer.ScreenH);
			if (_postInput.Accepting != _uiLayer.Accepting)
				sb.Append("\tANOMALY_ACC_MISMATCH");
			if (_postInput.RawDirectBody && !_postInput.Accepting)
				sb.Append("\tNOTE_rawDirect_would_false_Inside");
			if (_postInput.RawMulScaleBody && !_postInput.Accepting)
				sb.Append("\tNOTE_old_*scale_would_false_Inside");
			if (_postInput.WheelMoved)
				sb.Append("\tWHEEL_EVENT");
			if (_postInput.WheelMoved && _postInput.Accepting)
				sb.Append("\tWHEEL_INSIDE");
			if (_postInput.WheelMoved && !_postInput.Accepting)
				sb.Append("\tWHEEL_OUTSIDE");
			sb.AppendLine();
			AppendLine(sb.ToString());
		}

		private static string FormatSampleLine(Sample s)
		{
			var sb = new StringBuilder(1024);
			sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
			sb.Append('\t').Append(s.Phase);
			sb.Append("\tseq=").Append(++_seq);
			sb.Append("\tframe=").Append(s.GameFrame);
			sb.Append("\tMainXY=").Append(s.RawMainX).Append(',').Append(s.RawMainY);
			sb.Append("\thitUiXY=").Append(s.HitUiX).Append(',').Append(s.HitUiY);
			sb.Append("\tratioMain/hit=").Append(s.MainOverHitRatio);
			sb.Append("\tPiXY=").Append(s.PiMouseX).Append(',').Append(s.PiMouseY);
			sb.Append("\tXnaXY=").Append(s.XnaMouseX).Append(',').Append(s.XnaMouseY);
			sb.Append("\tLastXY=").Append(s.LastMouseX).Append(',').Append(s.LastMouseY);
			sb.Append("\tscr=").Append(s.ScreenW).Append('x').Append(s.ScreenH);
			sb.Append("\tUIScale=").Append(s.UiScale.ToString("0.###"));
			sb.Append("\tZoom=").Append(s.GameZoom.ToString("0.###"));
			sb.Append("\tfocus=").Append(s.HasFocus);
			sb.Append("\tinv=").Append(s.PlayerInventory);
			sb.Append("\tmapFS=").Append(s.MapFullscreen);
			sb.Append("\tinGameUI=").Append(s.InGameUiBusy);
			sb.Append("\twin=").Append(FormatRect(s.WindowUi));
			sb.Append("\tbody=").Append(FormatRect(s.BodyUi));
			sb.Append("\ttitle=").Append(FormatRect(s.TitleUi));
			sb.Append("\tposUi=").Append(s.PosUiX.ToString("0.#")).Append(',').Append(s.PosUiY.ToString("0.#"));
			sb.Append("\tw=").Append(s.WidthUi.ToString("0.#"));
			sb.Append("\tbodyH=").Append(s.BodyHeightUi.ToString("0.#"));
			sb.Append("\topen=").Append(s.ListOpen);
			sb.Append("\tmax=").Append(s.ListMaximized);
			sb.Append("\tmode=").Append(s.ContentMode);
			sb.Append("\thitEdge=").Append(s.HitEdgeTag);
			sb.Append("\trawEdgeVsBody=").Append(s.RawEdgeTag);
			sb.Append("\thitDxL/R/T/B=")
				.Append(s.HitDxLeft).Append('/')
				.Append(s.HitDxRight).Append('/')
				.Append(s.HitDyTop).Append('/')
				.Append(s.HitDyBottom);
			sb.Append("\trawDxL/R=").Append(s.RawDxLeft).Append('/').Append(s.RawDxRight);
			sb.Append("\trawDirectBody=").Append(s.RawDirectBody);
			sb.Append("\tBAD_rawDivBody=").Append(s.RawDivScaleBody);
			sb.Append("\tBAD_rawMulBody=").Append(s.RawMulScaleBody);
			sb.Append("\thitBody=").Append(s.HitContainsBody);
			sb.Append("\thitWin=").Append(s.HitContainsWindow);
			sb.Append("\thitTitle=").Append(s.HitContainsTitle);
			sb.Append("\tAcc=").Append(s.Accepting);
			sb.Append("\tmIface=").Append(s.MouseInterface);
			sb.Append("\tsupHotbar=").Append(s.SuppressHotbar);
			sb.Append("\tfrozen=").Append(s.FrozenHotbar);
			sb.Append("\townsF=").Append(s.OwnsWheelFocus);
			sb.Append("\tsuspF=").Append(s.WheelFocusSuspended);
			sb.Append("\tptrWinCache=").Append(s.PointerOverWindowCached);
			sb.Append("\twheelD=").Append(s.ScrollDelta).Append('/').Append(s.ScrollDeltaUi);
			sb.Append("\twheelMoved=").Append(s.WheelMoved);
			sb.Append("\tscrollOff=").Append(s.ScrollOffsetUi.ToString("0.#"));
			sb.Append("\tmaxScroll=").Append(s.MaxScrollUi.ToString("0.#"));
			sb.Append("\thotbarSlot=").Append(s.SelectedItem);
			sb.AppendLine();
			return sb.ToString();
		}

		private static string FormatRect(Rectangle r)
			=> r.Left + "," + r.Top + "-" + r.Right + "," + r.Bottom;

		private static void EnsureSessionOnce(string reason)
		{
			if (_sessionStarted)
				return;
			BeginSession(reason);
		}

		private static void WriteSessionHeader(string reason)
		{
			if (!BindingListViewLiveDiagGate.FileOutputEnabled)
				return;

			var sb = new StringBuilder(2048);
			sb.AppendLine("================================================================================");
			sb.AppendLine("AK ALL KEY HitDiag FULL LOG (TEMP) — field guide");
			sb.Append("SessionStart=").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			sb.Append("Reason=").AppendLine(reason);
			sb.AppendLine("Path=deck log\\Keyboard\\ListView_HitDiag_FULL.txt");
			sb.AppendLine("MaxBytes=80MB (trim keeps newest ~60MB only if exceeded)");
			sb.AppendLine("--------------------------------------------------------------------------------");
			sb.AppendLine("HOW TO READ (important)");
			sb.AppendLine("  Acc=True  → ListView accepts input (production gate, UI mouse × UI body)");
			sb.AppendLine("  Acc=False → ListView does NOT claim input / should not eat Hotbar wheel");
			sb.AppendLine("  hitUiXY   → mouse AFTER SetZoom_UI (same space as body/window/title)");
			sb.AppendLine("  MainXY    → Main.mouseX/Y AS READ IN THAT PHASE (space differs by phase!)");
			sb.AppendLine("  PostUpdateInput MainXY ≈ backbuffer/original; UILayer MainXY ≈ UI units");
			sb.AppendLine("  scrPost vs scrUi: often 3840x2160 vs 2553x1436 when UIScale≈1.5 — expected");
			sb.AppendLine("  ratioMain/hit ≈ UIScale means Post Main is unscaled and hitUi is UI");
			sb.AppendLine("  hitEdge   → relative to body using hitUi (OUT_LEFT_Npx / INSIDE / INSIDE_EDGE1px)");
			sb.AppendLine("  rawEdgeVsBody → if you wrongly compare MainXY to UI body (shows false hits)");
			sb.AppendLine("  BAD_*     → comparison-only (old buggy OR logic). NOT used for Acc");
			sb.AppendLine("  NOTE_rawDirect_would_false_Inside → Main in body numbers but Acc correctly false");
			sb.AppendLine("  NOTE_old_*scale_would_false_Inside → old *scale OR would false Inside");
			sb.AppendLine("  EDGE      → Acc flipped False↔True (best lines for boundary tests)");
			sb.AppendLine("  PAIR      → same-frame Post+UI summary");
			sb.AppendLine("  WHEEL_EVENT → scroll delta non-zero (captured BEFORE Consume)");
			sb.AppendLine("  WHEEL_INSIDE / WHEEL_OUTSIDE → Acc at that wheel event");
			sb.AppendLine("  scrollOff / maxScroll → list scroll position after ProcessWheel");
			sb.AppendLine("  ANOMALY_ACC_MISMATCH → Post Acc != UI Acc (bug if frequent)");
			sb.AppendLine("  CLICKPATH  → Inventory click-through investigation (TEMP)");
			sb.AppendLine("  CLICKPATH phases: PostUpdateInput → TryOccupy(ENTER/SKIP/EXIT) → OccupyFront → PostInventoryLayer");
			sb.AppendLine("  CLICKPATH gates: ENTER | SKIP_LIST_CLOSED | SKIP_NO_CLAIM | EXIT_AFTER_OCCUPY");
			sb.AppendLine("  CLICKPATH classHint A–I on TryOccupy / RESULT on PostInventory");
			sb.AppendLine("  RESULT=GRAB_DESPITE_OCCUPY → claimFront+occupy but mouseItem changed (E/D)");
			sb.AppendLine("  RESULT=GRAB_WITHOUT_CLAIM → click grab while claimFront false (B/F)");
			sb.AppendLine("  hypothesisHint=B_hitWinFalse / B_coversTrue / C_flagsSet_…");
			sb.AppendLine("--------------------------------------------------------------------------------");
			sb.AppendLine("WHAT TO DO FOR A GOOD CAPTURE");
			sb.AppendLine("  1) Build+Reload (this header rewrites the file)");
			sb.AppendLine("  2) Open Inventory + AK ALL KEY over it");
			sb.AppendLine("  3) Left-click a slot visible through AK window (try grab)");
			sb.AppendLine("  4) Say 'ログ読んで' — agent greps CLICKPATH lines");
			sb.AppendLine("================================================================================");
			File.WriteAllText(_logPath, sb.ToString(), Encoding.UTF8);
		}

		private static void AppendLine(string line)
		{
			if (!BindingListViewLiveDiagGate.FileOutputEnabled)
				return;

			try
			{
				EnsureLogPath();
				var sw = System.Diagnostics.Stopwatch.StartNew();
				File.AppendAllText(_logPath, line, Encoding.UTF8);
				sw.Stop();
				BindingListViewLiveDiagGate.NoteAppend(
					line != null ? Encoding.UTF8.GetByteCount(line) : 0,
					sw.Elapsed.TotalMilliseconds);

				var fi = new FileInfo(_logPath);
				if (fi.Exists && fi.Length > MaxBytes)
				{
					string all = File.ReadAllText(_logPath, Encoding.UTF8);
					int keep = (int)Math.Min(KeepBytesAfterTrim, all.Length);
					if (all.Length > keep)
					{
						string tail = all.Substring(all.Length - keep);
						File.WriteAllText(
							_logPath,
							"--- truncated oldest (kept newest ~60MB); re-open list for fresh header ---\n" + tail,
							Encoding.UTF8);
					}
				}
			}
			catch
			{
				// never break gameplay
			}
		}

		private static void EnsureLogPath()
		{
			if (!string.IsNullOrEmpty(_logPath))
				return;

			string dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				"My Games",
				"Terraria",
				"tModLoader",
				"deck log",
				"Keyboard");
			Directory.CreateDirectory(dir);
			_logPath = Path.Combine(dir, "ListView_HitDiag_FULL.txt");
		}

		private static void DrawRectBorder(SpriteBatch sb, Rectangle r, Color c)
		{
			if (r.Width <= 0 || r.Height <= 0)
				return;
			Texture2D tex = TextureAssets.MagicPixel.Value;
			sb.Draw(tex, new Rectangle(r.X, r.Y, r.Width, 1), c);
			sb.Draw(tex, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), c);
			sb.Draw(tex, new Rectangle(r.X, r.Y, 1, r.Height), c);
			sb.Draw(tex, new Rectangle(r.Right - 1, r.Y, 1, r.Height), c);
		}
	}
}
