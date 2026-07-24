using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.UI;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// One-shot / AK_DIAGNOSTICS path tracer for ComboPanel ESC registration vs parent navigation.
	/// Arm by creating empty file:
	///   Documents\My Games\Terraria\tModLoader\AdvancedKeybinds\combo-panel-esc-path-diag.arm
	/// Output:
	///   Desktop\cursor log\&lt;stamp&gt;\ComboPanel_ESC_経路診断.txt
	/// </summary>
	internal static class ComboPanelEscPathDiag
	{
		private static readonly object Gate = new();
		private static string _outPath;
		private static bool _armed;
		private static bool _resolved;
		private static int _lastFrame = int.MinValue;

		public static bool IsArmed
		{
			get
			{
				EnsureResolved();
				return _armed && !string.IsNullOrEmpty(_outPath);
			}
		}

		public static void Note(string phase, string detail = null)
		{
			if (!IsArmed)
				return;

			try
			{
				int frame = unchecked((int)Main.GameUpdateCount);
				var sb = new StringBuilder(512);
				sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
				sb.Append(" frame=").Append(frame);
				if (frame != _lastFrame)
				{
					_lastFrame = frame;
					sb.Append(" [NEW_FRAME]");
				}

				sb.Append(" | ").Append(phase);
				if (!string.IsNullOrEmpty(detail))
					sb.Append(' ').Append(detail);

				sb.Append(" | open=").Append(UI.ComboPanel.IsOpen);
				sb.Append(" regWait=").Append(UI.ComboPanel.DiagRegistrationWaitActive);
				sb.Append(" slot=").Append(UI.ComboPanel.DiagCapturingSlot);
				sb.Append(" escRegFrame=").Append(UI.ComboPanel.DiagEscapeRegisteredThisFrame);
				sb.Append(" escConFrame=").Append(UI.ComboPanel.DiagEscapeConsumedThisFrame);
				sb.Append(" keyEsc=").Append(Main.keyState.IsKeyDown(Keys.Escape));
				sb.Append(" oldEsc=").Append(Main.oldKeyState.IsKeyDown(Keys.Escape));
				sb.Append(" invJP=").Append(Trigger("Inventory", justPressed: true));
				sb.Append(" invCur=").Append(Trigger("Inventory", justPressed: false));
				sb.Append(" menuJP=").Append(Trigger("MenuInquiry", justPressed: true));
				sb.Append(" fancy=").Append(Main.inFancyUI);
				sb.Append(" menuMode=").Append(Main.menuMode);
				sb.Append(" ui=").Append(DescribeUi());
				if (Main.LocalPlayer != null)
					sb.Append(" relInv=").Append(Main.LocalPlayer.releaseInventory);

				sb.AppendLine();
				Append(_outPath, sb.ToString());
			}
			catch
			{
				// never break input
			}
		}

		public static void NoteStack(string phase, string detail = null)
		{
			if (!IsArmed)
				return;

			string stack;
			try
			{
				stack = new StackTrace(1, true).ToString();
			}
			catch
			{
				stack = "(stack unavailable)";
			}

			Note(phase, (detail ?? string.Empty) + "\nSTACK:\n" + stack);
		}

		private static bool Trigger(string name, bool justPressed)
		{
			TriggersSet set = justPressed ? PlayerInput.Triggers.JustPressed : PlayerInput.Triggers.Current;
			if (set?.KeyStatus == null || !set.KeyStatus.TryGetValue(name, out bool v))
				return false;
			return v;
		}

		private static string DescribeUi()
		{
			try
			{
				UIState ingame = Main.InGameUI?.CurrentState;
				UIState menu = Main.MenuUI?.CurrentState;
				string a = ingame == null ? "-" : ingame.GetType().FullName;
				string b = menu == null ? "-" : menu.GetType().FullName;
				return "InGame=" + a + ";Menu=" + b;
			}
			catch
			{
				return "?";
			}
		}

		private static void EnsureResolved()
		{
			if (_resolved)
				return;

			lock (Gate)
			{
				if (_resolved)
					return;

				_resolved = true;
#if !AK_DIAGNOSTICS
				_armed = false;
				return;
#else
				try
				{
					string arm = Path.Combine(
						Main.SavePath ?? string.Empty,
						"AdvancedKeybinds",
						"combo-panel-esc-path-diag.arm");
					// Also accept under Documents tModLoader folder when SavePath unset early.
					if (!File.Exists(arm))
					{
						string alt = Path.Combine(
							Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
							"My Games", "Terraria", "tModLoader", "AdvancedKeybinds",
							"combo-panel-esc-path-diag.arm");
						arm = alt;
					}

					_armed = File.Exists(arm);
					if (!_armed)
						return;

					string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
					string dir = Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
						"cursor log",
						stamp);
					Directory.CreateDirectory(dir);
					_outPath = Path.Combine(dir, "ComboPanel_ESC_経路診断.txt");
					Append(_outPath,
						"ComboPanel ESC path diag armed\r\n"
						+ "arm=" + arm + "\r\n"
						+ "out=" + _outPath + "\r\n"
						+ "---\r\n");
				}
				catch
				{
					_armed = false;
					_outPath = null;
				}
#endif
			}
		}

		private static void Append(string path, string text)
		{
			lock (Gate)
			{
				File.AppendAllText(path, text, Encoding.UTF8);
			}
		}
	}
}
