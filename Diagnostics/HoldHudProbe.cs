using System;
using System.IO;
using System.Text;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Diagnostics
{
	/// <summary>
	/// Hold HUD investigation log. Bodies compile only with AK_DIAGNOSTICS (public/Workshop off).
	/// Default OFF even in diagnostic builds (avoids per-frame disk I/O). Set <see cref="ForceEnabled"/> to re-arm.
	/// When enabled: size-capped append with simple rotation.
	/// </summary>
	internal static class HoldHudProbe
	{
		private static readonly object Gate = new();
		private static string _path;
		private static uint _lastSurfaceLogUpdate;
		private static uint _lastDrawLogUpdate;
		private static uint _lastFeetLogUpdate;
		private static uint _lastDragCheckLogUpdate;
		private static uint _lastHitTestLogUpdate;

		/// <summary>Max probe log size before rotate (AK_DIAGNOSTICS builds only).</summary>
		private const long MaxLogBytes = 2L * 1024L * 1024L;

		/// <summary>
		/// Manual arm for Hold HUD investigation. Default false — does not follow AK_DIAGNOSTICS alone.
		/// </summary>
		public static bool ForceEnabled;

		public static bool Enabled
		{
			get
			{
#if AK_DIAGNOSTICS
				return ForceEnabled;
#else
				return false;
#endif
			}
		}

		private static string Path
		{
			get
			{
				if (_path == null)
					_path = System.IO.Path.Combine(Main.SavePath, "AdvancedKeybinds", "hold-hud-probe.log");
				return _path;
			}
		}

		public static void Log(string message)
		{
			if (!Enabled)
				return;

			AkCpuMeasureSystem.Begin(AkCpuMeasureSystem.IdHoldHudProbeLog);
			try
			{
				string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message;
				lock (Gate)
				{
					string dir = System.IO.Path.GetDirectoryName(Path);
					if (!string.IsNullOrEmpty(dir))
						Directory.CreateDirectory(dir);
					RotateIfNeeded_NoLock();
					File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8);
				}
			}
			catch
			{
				// never throw from probe
			}
			finally
			{
				AkCpuMeasureSystem.End(AkCpuMeasureSystem.IdHoldHudProbeLog);
			}
		}

		private static void RotateIfNeeded_NoLock()
		{
			try
			{
				if (!File.Exists(Path))
					return;
				var info = new FileInfo(Path);
				if (info.Length < MaxLogBytes)
					return;
				string bak = Path + ".1";
				if (File.Exists(bak))
					File.Delete(bak);
				File.Move(Path, bak);
			}
			catch
			{
				// ignore rotation failure
			}
		}

		public static void LogSurface(string reason)
		{
			if (!Enabled)
				return;

			uint now = Main.GameUpdateCount;
			if (reason == "tick" && now - _lastSurfaceLogUpdate < 60)
				return;
			if (reason == "tick")
				_lastSurfaceLogUpdate = now;

			int screenW = Main.screenWidth;
			int screenH = Main.screenHeight;
			int vpW = 0, vpH = 0, bbW = 0, bbH = 0;
			try
			{
				var gd = Main.instance?.GraphicsDevice;
				if (gd != null)
				{
					var vp = gd.Viewport;
					vpW = vp.Width;
					vpH = vp.Height;
					bbW = gd.PresentationParameters.BackBufferWidth;
					bbH = gd.PresentationParameters.BackBufferHeight;
				}
			}
			catch { /* ignore */ }

			GameWindowHandle.TryGetClientSize(out int clientW, out int clientH);
			bool hasClient = GameWindowHandle.TryGetClientCursor(out int cx, out int cy);
			MouseState raw = Mouse.GetState();
			float ui = Math.Max(0.01f, Main.UIScale);
			HoldTierHudSystem.GetCustomPixelSurfaceSizePublic(out int customW, out int customH);
			var sb = new StringBuilder(480);
			sb.Append("SURFACE reason=").Append(reason);
			sb.Append(" screen=").Append(screenW).Append('x').Append(screenH);
			sb.Append(" gdViewport=").Append(vpW).Append('x').Append(vpH);
			sb.Append(" backbuffer=").Append(bbW).Append('x').Append(bbH);
			sb.Append(" client=").Append(clientW).Append('x').Append(clientH);
			sb.Append(" customSurface=").Append(customW).Append('x').Append(customH);
			sb.Append(" surfaceSource=").Append(Logic.HoldHudCoords.GamePixelSurfaceSource);
			sb.Append(" UIScale=").Append(ui.ToString("0.###"));
			sb.Append(" uiSize=").Append((int)Math.Round(screenW / ui)).Append('x').Append((int)Math.Round(screenH / ui));
			sb.Append(" focus=").Append(Main.hasFocus);
			sb.Append(" mainMouse=(").Append(Main.mouseX).Append(',').Append(Main.mouseY).Append(')');
			sb.Append(" mainMouse*ui=(").Append((Main.mouseX * ui).ToString("0.#")).Append(',')
				.Append((Main.mouseY * ui).ToString("0.#")).Append(')');
			sb.Append(" rawMouse=(").Append(raw.X).Append(',').Append(raw.Y).Append(')');
			if (hasClient)
			{
				sb.Append(" clientMouse=(").Append(cx).Append(',').Append(cy).Append(')');
				bool mainEqualsClient =
					Math.Abs(Main.mouseX - cx) <= 2 && Math.Abs(Main.mouseY - cy) <= 2;
				sb.Append(" mainEqualsClient=").Append(mainEqualsClient);
				sb.Append(" legacyScaleInflate=(")
					.Append((Main.mouseX * ui - cx).ToString("0.#")).Append(',')
					.Append((Main.mouseY * ui - cy).ToString("0.#")).Append(')');
			}
			else
				sb.Append(" clientMouse=n/a");
			sb.Append(" mismatchVP=").Append(vpW != 0 && (vpW != screenW || vpH != screenH));
			sb.Append(" mismatchClient=").Append(clientW > 0 && (clientW != screenW || clientH != screenH));
			sb.Append(" uiZoomPolluted=").Append(HoldTierHudSystem.IsUiZoomScreenPollutedPublic());
			Log(sb.ToString());
		}

		public static void LogState(string reason, AdvancedKeybindsConfig config)
		{
			if (!Enabled)
				return;

			if (config == null)
			{
				Log("STATE reason=" + reason + " config=null");
				return;
			}

			Log(
				"STATE reason=" + reason
				+ " anchor=" + config.HoldTierHudAnchor
				+ " preferCustom=" + Binding.ClientPrefs.HoldHudPreferCustom
				+ " enabled=" + config.HoldTierHudEnabled
				+ " allowDrag=" + config.HoldTierHudAllowDrag
				+ " customNorm=(" + config.HoldTierHudCustomPositionX.ToString("0.###") + ","
				+ config.HoldTierHudCustomPositionY.ToString("0.###") + ")"
				+ " dragging=" + HoldTierHudSystem.IsDragging
				+ " customPixelDrag=" + HoldTierHudSystem.IsCustomPixelDragActivePublic);
		}

		public static void LogCoordPipeline(
			string stage,
			Vector2 inputPixel,
			float normX,
			float normY,
			Vector2 drawPixel,
			int sw,
			int sh)
		{
			if (!Enabled)
				return;

			Log(
				"COORD stage=" + stage
				+ " inputPx=(" + inputPixel.X.ToString("0.#") + "," + inputPixel.Y.ToString("0.#") + ")"
				+ " storedNorm=(" + normX.ToString("0.###") + "," + normY.ToString("0.###") + ")"
				+ " drawPx=(" + drawPixel.X.ToString("0.#") + "," + drawPixel.Y.ToString("0.#") + ")"
				+ " surface=" + sw + "x" + sh
				+ " roundTripErr=("
				+ (drawPixel.X - inputPixel.X).ToString("0.#") + ","
				+ (drawPixel.Y - inputPixel.Y).ToString("0.#") + ")"
				+ " UIScale=" + Main.UIScale.ToString("0.###"));
		}

		public static void LogDrawThrottle(string line)
		{
			if (!Enabled)
				return;
			uint now = Main.GameUpdateCount;
			if (now - _lastDrawLogUpdate < 30)
				return;
			_lastDrawLogUpdate = now;
			Log(line);
		}

		public static void LogFeetCoordThrottle(string line)
		{
			if (!Enabled)
				return;
			uint now = Main.GameUpdateCount;
			if (now - _lastFeetLogUpdate < 30)
				return;
			_lastFeetLogUpdate = now;
			Log(line);
		}

		public static void LogDragCheck(string reason, bool result, string details = null)
		{
			if (!Enabled)
				return;

			uint now = Main.GameUpdateCount;
			bool important = result
				|| reason.IndexOf("edge", StringComparison.Ordinal) >= 0
				|| reason.IndexOf("begin", StringComparison.Ordinal) >= 0
				|| reason.IndexOf("mismatch", StringComparison.Ordinal) >= 0
				|| reason.IndexOf("anchor-not-custom", StringComparison.Ordinal) >= 0;
			if (!important && now - _lastDragCheckLogUpdate < 20)
				return;
			_lastDragCheckLogUpdate = now;
			Log(
				"DRAG-CHECK result=" + (result ? "true" : "false")
				+ " reason=" + reason
				+ (string.IsNullOrEmpty(details) ? "" : " " + details));
		}

		/// <summary>
		/// Custom head-side initial placement (or skip when a valid placement already exists).
		/// Always written when Enabled — not throttled — so unset vs existing decisions stay auditable.
		/// </summary>
		public static void LogCustomInit(
			string reason,
			bool saved,
			Vector2? playerTopWorld = null,
			Vector2? playerCenterWorld = null,
			Vector2? screenPosition = null,
			Vector2? worldToScreenGamePx = null,
			Vector2? barSize = null,
			float? headGap = null,
			Vector2? beforeClampGamePx = null,
			Vector2? afterClampGamePx = null,
			int? surfaceW = null,
			int? surfaceH = null,
			string surfaceSource = null,
			Vector2? storedNorm = null,
			Vector2? roundTripPx = null,
			Vector2? roundTripErr = null)
		{
			if (!Enabled)
				return;

			var sb = new StringBuilder(640);
			sb.Append("CUSTOM-INIT reason=").Append(reason);
			sb.Append(" saved=").Append(saved ? "true" : "false");
			if (playerTopWorld.HasValue)
				sb.Append(" playerTopWorld=(").Append(playerTopWorld.Value.X.ToString("0.#")).Append(',')
					.Append(playerTopWorld.Value.Y.ToString("0.#")).Append(')');
			if (playerCenterWorld.HasValue)
				sb.Append(" playerCenterWorld=(").Append(playerCenterWorld.Value.X.ToString("0.#")).Append(',')
					.Append(playerCenterWorld.Value.Y.ToString("0.#")).Append(')');
			if (screenPosition.HasValue)
				sb.Append(" screenPosition=(").Append(screenPosition.Value.X.ToString("0.#")).Append(',')
					.Append(screenPosition.Value.Y.ToString("0.#")).Append(')');
			if (worldToScreenGamePx.HasValue)
				sb.Append(" worldToScreenGamePx=(").Append(worldToScreenGamePx.Value.X.ToString("0.#")).Append(',')
					.Append(worldToScreenGamePx.Value.Y.ToString("0.#")).Append(')');
			sb.Append(" uiScale=").Append(Main.UIScale.ToString("0.###"));
			if (barSize.HasValue)
				sb.Append(" barSize=(").Append(barSize.Value.X.ToString("0.#")).Append(',')
					.Append(barSize.Value.Y.ToString("0.#")).Append(')');
			if (headGap.HasValue)
				sb.Append(" headGap=").Append(headGap.Value.ToString("0.#"));
			if (beforeClampGamePx.HasValue)
				sb.Append(" beforeClampGamePx=(").Append(beforeClampGamePx.Value.X.ToString("0.#")).Append(',')
					.Append(beforeClampGamePx.Value.Y.ToString("0.#")).Append(')');
			if (afterClampGamePx.HasValue)
				sb.Append(" afterClampGamePx=(").Append(afterClampGamePx.Value.X.ToString("0.#")).Append(',')
					.Append(afterClampGamePx.Value.Y.ToString("0.#")).Append(')');
			if (surfaceW.HasValue && surfaceH.HasValue)
				sb.Append(" surface=").Append(surfaceW.Value).Append('x').Append(surfaceH.Value);
			if (!string.IsNullOrEmpty(surfaceSource))
				sb.Append(" surfaceSource=").Append(surfaceSource);
			if (storedNorm.HasValue)
				sb.Append(" storedNorm=(").Append(storedNorm.Value.X.ToString("0.###")).Append(',')
					.Append(storedNorm.Value.Y.ToString("0.###")).Append(')');
			if (roundTripPx.HasValue)
				sb.Append(" roundTripPx=(").Append(roundTripPx.Value.X.ToString("0.#")).Append(',')
					.Append(roundTripPx.Value.Y.ToString("0.#")).Append(')');
			if (roundTripErr.HasValue)
				sb.Append(" roundTripErr=(").Append(roundTripErr.Value.X.ToString("0.#")).Append(',')
					.Append(roundTripErr.Value.Y.ToString("0.#")).Append(')');
			Log(sb.ToString());
		}

		public static void LogHitTest(
			Rectangle drawRect,
			Rectangle hitRect,
			Vector2 mousePx,
			Vector2 mouseUi,
			AdvancedKeybindsConfig config,
			string drawBranch,
			string hitBranch)
		{
			if (!Enabled)
				return;

			uint now = Main.GameUpdateCount;
			if (now - _lastHitTestLogUpdate < 20)
				return;
			_lastHitTestLogUpdate = now;
			bool over = hitRect.Contains((int)mousePx.X, (int)mousePx.Y);
			Log(
				"HIT-TEST over=" + over
				+ " draw=" + FormatRect(drawRect)
				+ " hit=" + FormatRect(hitRect)
				+ " mousePx=" + FormatVec(mousePx)
				+ " mouseUi=" + FormatVec(mouseUi)
				+ " drawBranch=" + drawBranch
				+ " hitBranch=" + hitBranch
				+ " anchor=" + (config?.HoldTierHudAnchor.ToString() ?? "null"));
		}

		private static string FormatRect(Rectangle r)
			=> "[" + r.X + "," + r.Y + " " + r.Width + "x" + r.Height + "]";

		private static string FormatVec(Vector2 v)
			=> "(" + v.X.ToString("0.#") + "," + v.Y.ToString("0.#") + ")";
	}
}
