using System;
using AdvancedKeybinds.Diagnostics;
using AdvancedKeybinds.Patches;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AdvancedKeybinds.Logic
{
	/// <summary>
	/// HOLDゲージ専用の座標空間・変換・surface解決。
	/// UIScale / screenPosition / Matrix / 正規化はここに集約する。
	/// </summary>
	internal static class HoldHudCoords
	{
		public const int MinStableW = 640;
		public const int MinStableH = 360;
		public const float RoundTripAnomalyPx = 1f;
		public const float FeetAnomalyMaxUi = 120f;
		public const float MouseAnomalySlackUi = 48f;

		private static int _gamePixelSurfaceW;
		private static int _gamePixelSurfaceH;
		private static string _gamePixelSurfaceSource = "unset";

		public static int GamePixelSurfaceW => Math.Max(1, _gamePixelSurfaceW);
		public static int GamePixelSurfaceH => Math.Max(1, _gamePixelSurfaceH);
		public static string GamePixelSurfaceSource => _gamePixelSurfaceSource;
		public static bool HasGamePixelSurface
			=> _gamePixelSurfaceW >= MinStableW && _gamePixelSurfaceH >= MinStableH;

		public static float UiScale => Math.Max(0.01f, Main.UIScale);

		public readonly struct SurfaceCatalog
		{
			public readonly int ClientW;
			public readonly int ClientH;
			public readonly int BackBufferW;
			public readonly int BackBufferH;
			public readonly int ViewportW;
			public readonly int ViewportH;
			public readonly int MainScreenW;
			public readonly int MainScreenH;
			public readonly bool HasClient;
			public readonly bool UiZoomPolluted;

			public SurfaceCatalog(
				int clientW, int clientH, bool hasClient,
				int bbW, int bbH, int vpW, int vpH,
				int mainW, int mainH, bool uiZoomPolluted)
			{
				ClientW = clientW;
				ClientH = clientH;
				HasClient = hasClient;
				BackBufferW = bbW;
				BackBufferH = bbH;
				ViewportW = vpW;
				ViewportH = vpH;
				MainScreenW = mainW;
				MainScreenH = mainH;
				UiZoomPolluted = uiZoomPolluted;
			}

			public string Format()
				=> "client=" + (HasClient ? ClientW + "x" + ClientH : "n/a")
					+ " backbuffer=" + BackBufferW + "x" + BackBufferH
					+ " viewport=" + ViewportW + "x" + ViewportH
					+ " mainScreen=" + MainScreenW + "x" + MainScreenH
					+ " uiZoomPolluted=" + UiZoomPolluted;
		}

		public static SurfaceCatalog CaptureSurfaces()
		{
			try
			{
			bool hasClient = GameWindowHandle.TryGetClientSize(out int clientW, out int clientH);
			int bbW = 0;
			int bbH = 0;
			int vpW = 0;
			int vpH = 0;
			try
			{
				var gd = Main.instance?.GraphicsDevice;
				if (gd != null)
				{
					vpW = gd.Viewport.Width;
					vpH = gd.Viewport.Height;
					bbW = gd.PresentationParameters.BackBufferWidth;
					bbH = gd.PresentationParameters.BackBufferHeight;
				}
			}
			catch
			{
				// keep zeros
			}

			int mainW = Math.Max(1, Main.screenWidth);
			int mainH = Math.Max(1, Main.screenHeight);
			float ui = UiScale;
			// SetZoom_UI shrinks Main.screen* toward UI units. Check both axes and UIScale round-trip.
			bool pollutedByWidth = hasClient && clientW >= MinStableW
				&& mainW > 0 && mainW < clientW * 0.90f;
			bool pollutedByHeight = hasClient && clientH >= MinStableH
				&& mainH > 0 && mainH < clientH * 0.90f;
			bool pollutedByUiScale = false;
			if (hasClient && clientW >= MinStableW && clientH >= MinStableH && ui > 0.01f)
			{
				float expectW = mainW * ui;
				float expectH = mainH * ui;
				pollutedByUiScale =
					Math.Abs(expectW - clientW) > Math.Max(8f, clientW * 0.05f)
					|| Math.Abs(expectH - clientH) > Math.Max(8f, clientH * 0.05f);
				// Only treat as UI-zoom pollution when main is smaller than client (SetZoom_UI pattern).
				pollutedByUiScale = pollutedByUiScale && (mainW < clientW * 0.95f || mainH < clientH * 0.95f);
			}

			bool polluted = pollutedByWidth || pollutedByHeight || pollutedByUiScale;

			return new SurfaceCatalog(
				clientW, clientH, hasClient,
				bbW, bbH, vpW, vpH,
				mainW, mainH, polluted);
			}
			finally
			{
			}
		}

		public static bool IsUiZoomScreenPolluted() => CaptureSurfaces().UiZoomPolluted;

		public static void InvalidateGamePixelSurface(string reason)
		{
			_gamePixelSurfaceW = 0;
			_gamePixelSurfaceH = 0;
			_gamePixelSurfaceSource = "unset";
		}

		/// <summary>
		/// Priority: when client≠backbuffer prefer backbuffer (Identity draw space);
		/// else client → backbuffer → viewport → Main.screenWidth (not when UI-zoom polluted).
		/// </summary>
		public static void RefreshGamePixelSurface(string phase)
		{
			try
			{
			SurfaceCatalog cat = CaptureSurfaces();
			int chosenW;
			int chosenH;
			string why;

			bool clientOk = TryPickStable(cat.HasClient, cat.ClientW, cat.ClientH, out int cW, out int cH);
			bool bbOk = TryPickStable(true, cat.BackBufferW, cat.BackBufferH, out int bW, out int bH);
			bool clientBbMismatch = clientOk && bbOk && (cW != bW || cH != bH);

			if (clientBbMismatch)
			{
				chosenW = bW;
				chosenH = bH;
				why = "priority=backbuffer(client-mismatch) " + cat.Format();
			}
			else if (clientOk)
			{
				chosenW = cW;
				chosenH = cH;
				why = "priority=client " + cat.Format();
			}
			else if (bbOk)
			{
				chosenW = bW;
				chosenH = bH;
				why = "priority=backbuffer " + cat.Format();
			}
			else if (TryPickStable(true, cat.ViewportW, cat.ViewportH, out chosenW, out chosenH))
			{
				why = "priority=viewport " + cat.Format();
			}
			else if (!cat.UiZoomPolluted
				&& TryPickStable(true, cat.MainScreenW, cat.MainScreenH, out chosenW, out chosenH))
			{
				why = "priority=mainScreen(not-polluted) " + cat.Format();
			}
			else if (HasGamePixelSurface)
			{
				why = "keep-previous(polluted-or-unstable) " + cat.Format();
				LogSurfaceChoice(phase, _gamePixelSurfaceW, _gamePixelSurfaceH, _gamePixelSurfaceSource, why, false);
				return;
			}
			else if (cat.UiZoomPolluted && cat.HasClient)
			{
				chosenW = cat.ClientW;
				chosenH = cat.ClientH;
				why = "boot-fallback=client-while-polluted " + cat.Format();
			}
			else
			{
				chosenW = Math.Max(1, cat.MainScreenW);
				chosenH = Math.Max(1, cat.MainScreenH);
				why = "boot-fallback=mainScreen " + cat.Format();
			}

			bool changed = chosenW != _gamePixelSurfaceW || chosenH != _gamePixelSurfaceH;
			string sourceTag = why.Split(' ')[0];
			if (changed)
			{
				_gamePixelSurfaceW = chosenW;
				_gamePixelSurfaceH = chosenH;
				_gamePixelSurfaceSource = sourceTag;
			}

			LogSurfaceChoice(phase, chosenW, chosenH, _gamePixelSurfaceSource, why, changed);
			}
			finally
			{
			}
		}

		private static bool TryPickStable(bool available, int w, int h, out int ow, out int oh)
		{
			ow = w;
			oh = h;
			return available && w >= MinStableW && h >= MinStableH;
		}

		private static void LogSurfaceChoice(
			string phase, int w, int h, string source, string why, bool changed)
		{
		}

		public static void GetGamePixelSurface(out int width, out int height)
		{
			if (!HasGamePixelSurface)
				RefreshGamePixelSurface("lazy-get");
			width = GamePixelSurfaceW;
			height = GamePixelSurfaceH;
		}

		public static Vector2 WorldToGamePx(Vector2 world)
		{
			Vector2 worldMinusScreen = world - Main.screenPosition;
			return Vector2.Transform(worldMinusScreen, Main.GameViewMatrix.ZoomMatrix);
		}

		public static Vector2 WorldMinusScreenThroughTransformationMatrix(Vector2 worldMinusScreen)
			=> Vector2.Transform(worldMinusScreen, Main.GameViewMatrix.TransformationMatrix);

		public static Vector2 GamePxToUi(Vector2 gamePx) => gamePx / UiScale;

		public static Vector2 UiToGamePx(Vector2 ui) => ui * UiScale;

		public static float GamePxToUiScalar(float gamePx) => gamePx / UiScale;

		public static float UiToGamePxScalar(float ui) => ui * UiScale;

		public static Vector2 NormalizeGamePx(Vector2 gamePx, int surfaceW, int surfaceH)
			=> new(gamePx.X / Math.Max(1, surfaceW), gamePx.Y / Math.Max(1, surfaceH));

		public static Vector2 DenormalizeToGamePx(float normX, float normY, int surfaceW, int surfaceH)
			=> new(normX * Math.Max(1, surfaceW), normY * Math.Max(1, surfaceH));

		public static Vector2 UiOriginToGamePx(
			Vector2 originUi, int uiSurfaceW, int uiSurfaceH, int gameW, int gameH)
			=> new(
				originUi.X / Math.Max(1, uiSurfaceW) * gameW,
				originUi.Y / Math.Max(1, uiSurfaceH) * gameH);

		public static Vector2 PlayerFeetWorld(Player player)
			=> new(player.position.X + player.width * 0.5f, player.position.Y + player.height);

		/// <summary>Hitbox top-center in world space (Custom head-side init baseline).</summary>
		public static Vector2 PlayerTopWorld(Player player)
			=> new(player.position.X + player.width * 0.5f, player.position.Y);

		public static Vector2 PlayerFeetGamePx(Player player)
			=> WorldToGamePx(PlayerFeetWorld(player));

		public static Vector2 PlayerFeetUi(Player player)
			=> GamePxToUi(PlayerFeetGamePx(player));

		public static Vector2 GetMouseClientPx()
		{
			if (GameWindowHandle.TryGetClientCursor(out int x, out int y))
				return new Vector2(x, y);
			if (Main.hasFocus)
				return new Vector2(Main.mouseX, Main.mouseY);
			MouseState mouse = Mouse.GetState();
			return new Vector2(mouse.X, mouse.Y);
		}

		/// <summary>
		/// Mouse in Custom draw/hit space (gamePixelSurface). When Win32 client size ≠ surface
		/// (typically backbuffer on mismatch), ratio-map client → surface. Never assume equal.
		/// </summary>
		public static Vector2 GetMouseCustomGamePx()
		{
			Vector2 clientMouse = GetMouseClientPx();
			SurfaceCatalog cat = CaptureSurfaces();
			GetGamePixelSurface(out int surfW, out int surfH);
			if (!cat.HasClient || cat.ClientW < 1 || cat.ClientH < 1)
				return clientMouse;

			if (cat.ClientW == surfW && cat.ClientH == surfH)
				return clientMouse;

			return new Vector2(
				clientMouse.X / cat.ClientW * surfW,
				clientMouse.Y / cat.ClientH * surfH);
		}

		public static Vector2 GetMouseUi()
		{
			if (Main.hasFocus)
				return new Vector2(Main.mouseX, Main.mouseY);

			float ui = UiScale;
			if (GameWindowHandle.TryGetClientCursor(out int cx, out int cy))
				return new Vector2(cx / ui, cy / ui);

			MouseState mouse = Mouse.GetState();
			return new Vector2(mouse.X / ui, mouse.Y / ui);
		}

		public static Rectangle DrawRectFromOriginGamePx(
			Vector2 originGamePx, float widthGamePx, float heightGamePx)
		{
			return new Rectangle(
				(int)MathF.Floor(originGamePx.X - widthGamePx * 0.5f),
				(int)MathF.Floor(originGamePx.Y),
				Math.Max(1, (int)MathF.Ceiling(widthGamePx)),
				Math.Max(1, (int)MathF.Ceiling(heightGamePx)));
		}

		public static Rectangle HitRectFromDrawRectGamePx(
			Rectangle drawRect, int padX, int padY, int surfaceW, int surfaceH)
		{
			int left = padX;
			int right = padX;
			int top = padY;
			int bottom = padY;

			int roomLeft = Math.Max(0, drawRect.X);
			int roomRight = Math.Max(0, surfaceW - (drawRect.X + drawRect.Width));
			int roomTop = Math.Max(0, drawRect.Y);
			int roomBottom = Math.Max(0, surfaceH - (drawRect.Y + drawRect.Height));

			left = Math.Min(left, roomLeft);
			right = Math.Min(right, roomRight);
			top = Math.Min(top, roomTop);
			bottom = Math.Min(bottom, roomBottom);

			int needX = padX * 2 - (left + right);
			if (needX > 0)
			{
				int addLeft = Math.Min(needX, roomLeft - left);
				left += addLeft;
				needX -= addLeft;
				if (needX > 0)
					right += Math.Min(needX, roomRight - right);
			}

			int needY = padY * 2 - (top + bottom);
			if (needY > 0)
			{
				int addTop = Math.Min(needY, roomTop - top);
				top += addTop;
				needY -= addTop;
				if (needY > 0)
					bottom += Math.Min(needY, roomBottom - bottom);
			}

			return new Rectangle(
				drawRect.X - left,
				drawRect.Y - top,
				drawRect.Width + left + right,
				drawRect.Height + top + bottom);
		}

		public static void ValidateRoundTrip(
			string stage,
			Vector2 inputGamePx,
			float customNormX,
			float customNormY,
			int surfaceW,
			int surfaceH)
		{
		}

		public static void CheckFeetAnomaly(Vector2 finalOriginUi, float expectedOffsetYUi)
		{
		}

		public static void CheckMouseAnomaly(Vector2 finalOriginUi, Vector2 expectedOffsetUi)
		{
		}

		public static void LogCustomLayout(
			string stage,
			Rectangle drawRect,
			Rectangle hitRect,
			Vector2 mouseClientPx,
			Vector2 customNorm,
			Vector2 originGamePx)
		{
		}

		public static void LogPhaseSnapshot(string phase, AdvancedKeybindsConfig config = null)
		{
		}

		public static void LogUiToCustomPromotion(
			Vector2 originUi, Vector2 gamePx, Vector2 customNorm, int gameW, int gameH)
		{
		}
	}
}
