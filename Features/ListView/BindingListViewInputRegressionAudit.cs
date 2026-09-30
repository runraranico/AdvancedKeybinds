using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;

namespace AdvancedKeybinds.Features.ListView
{
	/// <summary>
	/// TEMP READ-ONLY input regression auditor (investigation only).
	/// Compares physical Keyboard.GetState / GetAsyncKeyState vs Main.keyState.
	/// Does NOT mutate Main.* / PlayerInput / keyState / mouse / layers / hooks.
	/// Gate: <see cref="BindingListViewLiveDiagGate.InputRegressionAuditEnabled"/> (default OFF).
	/// Log: deck log\Keyboard\ListView_InputRegression_LIVE.txt
	/// </summary>
	internal static class BindingListViewInputRegressionAudit
	{
		private const int MaxFailLinesPerSecond = 40;
		private const int StickyConfirmFrames = 2;

		private static bool _sessionStarted;
		private static bool _prevAkOpen;
		private static int _failLinesThisSecond;
		private static int _secondUtc = -1;
		private static int _keysScanned;
		private static int _idlePassKeys;
		private static int _stickyFailKeys;
		private static readonly HashSet<string> _stickyFailNames = new HashSet<string>(StringComparer.Ordinal);
		private static readonly Dictionary<Keys, byte> _stickyFrames = new Dictionary<Keys, byte>();
		private static readonly Dictionary<Keys, bool> _prevPhysDown = new Dictionary<Keys, bool>();
		private static readonly Dictionary<Keys, bool> _prevMainDown = new Dictionary<Keys, bool>();
		private static readonly StringBuilder _buffer = new StringBuilder(4096);

		private static readonly Keys[] ModifierKeys =
		{
			Keys.LeftShift, Keys.RightShift,
			Keys.LeftControl, Keys.RightControl,
			Keys.LeftAlt, Keys.RightAlt,
			Keys.LeftWindows, Keys.RightWindows
		};

		private static string LogPath
		{
			get
			{
				string root = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"My Games", "Terraria", "tModLoader", "deck log", "Keyboard");
				Directory.CreateDirectory(root);
				return Path.Combine(root, "ListView_InputRegression_LIVE.txt");
			}
		}

		public static void Tick(BindingListViewState state)
		{
			if (!BindingListViewLiveDiagGate.InputRegressionAuditEnabled)
				return;

			EnsureSession(state);

			bool akOpen = state != null && state.IsOpen;
			bool claim = BindingListViewSystem.ShouldBlockInventoryItemSlotLeftClick();
			if (akOpen != _prevAkOpen)
			{
				AppendLine(
					"STATE_EDGE\takOpen=" + (akOpen ? 1 : 0)
					+ "\tclaimFront=" + (claim ? 1 : 0)
					+ "\tfrontCapture=" + (ReadFrontCapture() ? 1 : 0)
					+ "\tnote=" + (_prevAkOpen && !akOpen ? "OpenToClose" : "CloseToOpen"));
				DumpResidualState(state, claim);
				_prevAkOpen = akOpen;
				Flush();
			}

			KeyboardState phys = default;
			try
			{
				phys = Keyboard.GetState();
			}
			catch
			{
				return;
			}

			KeyboardState main = Main.keyState;
			_keysScanned = 0;
			_idlePassKeys = 0;

			foreach (Keys key in Enum.GetValues(typeof(Keys)))
			{
				if (!IsAuditableKey(key))
					continue;

				_keysScanned++;
				bool physDown = SafeIsDown(phys, key) || AsyncKeyDown(key);
				bool mainDown = SafeIsDown(main, key);

				_prevPhysDown.TryGetValue(key, out bool prevPhys);
				_prevMainDown.TryGetValue(key, out bool prevMain);

				// Sticky: physical UP but Main.keyState DOWN for consecutive frames.
				if (!physDown && mainDown)
				{
					_stickyFrames.TryGetValue(key, out byte n);
					n++;
					_stickyFrames[key] = n;
					if (n >= StickyConfirmFrames)
					{
						string name = key.ToString();
						if (_stickyFailNames.Add(name))
							_stickyFailKeys++;
						if (ThrottleFail())
						{
							AppendLine(
								"FAIL_STICKY\tkey=" + name
								+ "\tphys=UP\tmain=DOWN"
								+ "\takOpen=" + (akOpen ? 1 : 0)
								+ "\tclaimFront=" + (claim ? 1 : 0)
								+ "\tframes=" + n);
						}
					}
				}
				else
				{
					_stickyFrames[key] = 0;
					if (!physDown && !mainDown)
						_idlePassKeys++;
				}

				// Transition log for modifiers only (low volume).
				if (IsModifier(key) && (physDown != prevPhys || mainDown != prevMain))
				{
					AppendLine(
						"MOD_EDGE\tkey=" + key
						+ "\tphys=" + (physDown ? "DOWN" : "UP")
						+ "\tmain=" + (mainDown ? "DOWN" : "UP")
						+ "\takOpen=" + (akOpen ? 1 : 0)
						+ "\tclaimFront=" + (claim ? 1 : 0));
				}

				_prevPhysDown[key] = physDown;
				_prevMainDown[key] = mainDown;
			}

			AuditMouse(akOpen, claim);
			Flush();
		}

		private static void AuditMouse(bool akOpen, bool claim)
		{
			bool curL = false, jpL = false, jrL = false;
			bool curR = false, jpR = false, jrR = false;
			try
			{
				curL = PlayerInput.Triggers.Current.MouseLeft;
				jpL = PlayerInput.Triggers.JustPressed.MouseLeft;
				jrL = PlayerInput.Triggers.JustReleased.MouseLeft;
				curR = PlayerInput.Triggers.Current.MouseRight;
				jpR = PlayerInput.Triggers.JustPressed.MouseRight;
				jrR = PlayerInput.Triggers.JustReleased.MouseRight;
			}
			catch
			{
				// ignore
			}

			// When AK closed and not claiming: swallow must not be "stuck" via front capture.
			// (Main.mouse* is refilled each frame by vanilla; we flag residual capture latch only.)
			if (!akOpen && ReadFrontCapture())
			{
				if (ThrottleFail())
					AppendLine("FAIL_CAPTURE_RESIDUAL_WHEN_CLOSED\tfrontCapture=1");
			}

			// JustPressed must not remain true while button is up (best-effort).
			if (!Main.mouseLeft && jpL && !claim)
			{
				if (ThrottleFail())
					AppendLine(
						"FAIL_MOUSE_JP_WHILE_UP\tbutton=Left\takOpen=" + (akOpen ? 1 : 0)
						+ "\tclaimFront=" + (claim ? 1 : 0)
						+ "\tmouseLeft=0\tjp=1\tjr=" + (jrL ? 1 : 0) + "\tcur=" + (curL ? 1 : 0));
			}

			if (!Main.mouseRight && jpR && !claim)
			{
				if (ThrottleFail())
					AppendLine(
						"FAIL_MOUSE_JP_WHILE_UP\tbutton=Right\takOpen=" + (akOpen ? 1 : 0)
						+ "\tclaimFront=" + (claim ? 1 : 0));
			}
		}

		private static void DumpResidualState(BindingListViewState state, bool claim)
		{
			AppendLine(
				"RESIDUAL\takOpen=" + (state != null && state.IsOpen ? 1 : 0)
				+ "\tclaimFront=" + (claim ? 1 : 0)
				+ "\tfrontCapture=" + (ReadFrontCapture() ? 1 : 0)
				+ "\tmouseLeft=" + (Main.mouseLeft ? 1 : 0)
				+ "\tmouseLeftRelease=" + (Main.mouseLeftRelease ? 1 : 0)
				+ "\tblockMouse=" + (Main.blockMouse ? 1 : 0)
				+ "\tmiType=" + (Main.mouseItem == null || Main.mouseItem.IsAir ? 0 : Main.mouseItem.type)
				+ "\tmiAir=" + (Main.mouseItem == null || Main.mouseItem.IsAir ? 1 : 0)
				+ "\tleftAltMain=" + (SafeIsDown(Main.keyState, Keys.LeftAlt) ? 1 : 0)
				+ "\trightAltMain=" + (SafeIsDown(Main.keyState, Keys.RightAlt) ? 1 : 0)
				+ "\tleftAltPhys=" + (AsyncKeyDown(Keys.LeftAlt) ? 1 : 0)
				+ "\trightAltPhys=" + (AsyncKeyDown(Keys.RightAlt) ? 1 : 0));
		}

		private static void EnsureSession(BindingListViewState state)
		{
			if (_sessionStarted)
				return;
			_sessionStarted = true;
			_prevAkOpen = state != null && state.IsOpen;

			var sb = new StringBuilder(512);
			sb.AppendLine("===== ListView Input Regression Audit SESSION =====");
			sb.Append("timeLocal=").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
			sb.AppendLine("mode=READ_ONLY consistency (no inject, no mutate)");
			sb.AppendLine("phys=Keyboard.GetState + GetAsyncKeyState(best-effort)");
			sb.AppendLine("game=Main.keyState");
			sb.AppendLine("fail=phys UP && main DOWN for >=2 frames (STICKY)");
			sb.AppendLine("note=Full PHASE2-5 press/release PASS requires user physical key sweep; idle sticky scan is automatic");
			sb.AppendLine("===== BEGIN =====");
			try
			{
				File.AppendAllText(LogPath, sb.ToString());
			}
			catch
			{
				// ignore
			}
		}

		private static bool ThrottleFail()
		{
			int sec = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);
			if (sec != _secondUtc)
			{
				_secondUtc = sec;
				_failLinesThisSecond = 0;
			}

			if (_failLinesThisSecond >= MaxFailLinesPerSecond)
				return false;
			_failLinesThisSecond++;
			return true;
		}

		private static void AppendLine(string line)
		{
			_buffer.Append(DateTime.UtcNow.ToString("HH:mm:ss.fff"));
			_buffer.Append('\t').Append(Main.GameUpdateCount);
			_buffer.Append('\t').AppendLine(line);
		}

		private static void Flush()
		{
			if (_buffer.Length == 0)
				return;
			string chunk = _buffer.ToString();
			_buffer.Clear();
			try
			{
				File.AppendAllText(LogPath, chunk);
			}
			catch
			{
				// ignore
			}
		}

		private static bool IsAuditableKey(Keys key)
		{
			// FNA Keys is keyboard-only (no LeftButton/RightButton etc.).
			if (key == Keys.None)
				return false;
			return (int)key > 0;
		}

		private static bool IsModifier(Keys key)
		{
			for (int i = 0; i < ModifierKeys.Length; i++)
			{
				if (ModifierKeys[i] == key)
					return true;
			}

			return false;
		}

		private static bool SafeIsDown(KeyboardState state, Keys key)
		{
			try
			{
				return state.IsKeyDown(key);
			}
			catch
			{
				return false;
			}
		}

		private static bool ReadFrontCapture()
			=> BindingListViewSystem.FrontMouseCaptureActiveForAudit;

		private static bool AsyncKeyDown(Keys key)
		{
			int vk = KeysToVk(key);
			if (vk == 0)
				return false;
			try
			{
				return (GetAsyncKeyState(vk) & 0x8000) != 0;
			}
			catch
			{
				return false;
			}
		}

		private static int KeysToVk(Keys key)
		{
			return key switch
			{
				Keys.LeftShift => 0xA0,
				Keys.RightShift => 0xA1,
				Keys.LeftControl => 0xA2,
				Keys.RightControl => 0xA3,
				Keys.LeftAlt => 0xA4,
				Keys.RightAlt => 0xA5,
				Keys.LeftWindows => 0x5B,
				Keys.RightWindows => 0x5C,
				Keys.Escape => 0x1B,
				Keys.Space => 0x20,
				Keys.Tab => 0x09,
				Keys.Enter => 0x0D,
				Keys.Back => 0x08,
				Keys.Delete => 0x2E,
				_ when key >= Keys.A && key <= Keys.Z => 0x41 + (key - Keys.A),
				_ when key >= Keys.D0 && key <= Keys.D9 => 0x30 + (key - Keys.D0),
				_ when key >= Keys.F1 && key <= Keys.F12 => 0x70 + (key - Keys.F1),
				_ => (int)key <= 255 ? (int)key : 0
			};
		}

		[DllImport("user32.dll")]
		private static extern short GetAsyncKeyState(int vKey);
	}
}
