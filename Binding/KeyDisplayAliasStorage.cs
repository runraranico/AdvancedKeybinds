using System;
using System.Collections.Generic;
using System.IO;
using AdvancedKeybinds.Core;
using Newtonsoft.Json;
using Terraria;

namespace AdvancedKeybinds.Binding
{
	/// <summary>
	/// Per-key display aliases (Option A): Enter rename on a combo slot saves a global name for that
	/// physical token. Survives slot clears and applies anywhere the key is shown or re-registered.
	/// </summary>
	public static class KeyDisplayAliasStorage
	{
		private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase);

		public const int MaxDisplayLength = 24;

		private static string FilePath => Path.Combine(Main.SavePath, "AdvancedKeybinds", "key-display-aliases.json");

		public static void Initialize()
		{
			LoadFromFile();
		}

		public static void Unload()
		{
			Aliases.Clear();
		}

		/// <summary>
		/// Stable dictionary key for a captured/saved token.
		/// Prefer <see cref="PhysicalKeyIdentity"/> so vanilla KeyStatus names (e.g. <c>Q</c>)
		/// and SC:/VK: capture tokens for the same physical key share one alias bucket.
		/// Does not rewrite KeyStatus / bindings.json tokens — display lookup only.
		/// </summary>
		public static string ResolveStorageKey(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return null;

			token = token.Trim();

			// Display label only — never a save token. Map to MouseN so panel pick / legacy
			// aliases share the same bucket as Controls KeyStatus.
			token = DisplayAliasStorageKeyPolicy.RemapDisplayLabelToStorageToken(token);

			// Vanilla list rows keep Keys names; key-info pick stores SC:. Unify here.
			if (PhysicalKeyIdentity.TryResolve(token, out string physicalId)
				&& !string.IsNullOrEmpty(physicalId))
				return physicalId;

			string canonical = StableKeyToken.CanonicalizeOrNull(token);
			if (canonical != null)
				return canonical;

			if (PhysicalKeyToken.TryCanonicalize(token, out string physical))
				return physical;

			return token;
		}

		public static string GetDisplay(string token)
		{
			string key = ResolveStorageKey(token);
			if (key == null)
				return null;

			return Aliases.TryGetValue(key, out string display) ? display : null;
		}

		/// <summary>Sets or clears the global display name for <paramref name="token"/>.</summary>
		public static void SetDisplay(string token, string display)
		{
			string key = ResolveStorageKey(token);
			if (key == null)
				return;

			display = string.IsNullOrWhiteSpace(display) ? null : display.Trim();
			if (display != null && display.Length > MaxDisplayLength)
				display = display[..MaxDisplayLength];

			if (display == null)
			{
				if (!Aliases.TryGetValue(key, out string removed))
					return;

				Aliases.Remove(key);

				SaveToFile();
				return;
			}

			Aliases.TryGetValue(key, out string previous);
			if (string.Equals(previous, display, StringComparison.Ordinal))
				return;

			Aliases[key] = display;

			SaveToFile();
		}

		/// <summary>Removes every global display alias and persists immediately.</summary>
		public static int ClearAll()
		{
			int count = Aliases.Count;
			if (count == 0)
				return 0;

			Aliases.Clear();
			SaveToFile();
			return count;
		}

		/// <summary>True when any global display alias is stored.</summary>
		public static bool HasAny()
			=> Aliases.Count > 0;

		private static void LoadFromFile()
		{
			Aliases.Clear();

			try
			{
				if (File.Exists(FilePath))
				{
					string json = File.ReadAllText(FilePath);
					var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
					if (loaded != null)
					{
						foreach (KeyValuePair<string, string> pair in loaded)
						{
							if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
								continue;

							string key = ResolveStorageKey(pair.Key) ?? pair.Key.Trim();
							string display = pair.Value.Trim();
							if (display.Length > MaxDisplayLength)
								display = display[..MaxDisplayLength];

							if (StableKeyToken.IsDisplayAlias(key))
								continue;

							Aliases[key] = display;
						}
					}
				}

			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to load key display aliases: " + ex.Message);
			}
		}

		private static void SaveToFile()
		{
			try
			{
				string directory = Path.GetDirectoryName(FilePath);
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);

				File.WriteAllText(FilePath, JsonConvert.SerializeObject(Aliases, Formatting.Indented));
			}
			catch (Exception ex)
			{
				AdvancedKeybinds.Instance?.Logger.Warn("Advanced Keybinds failed to save key display aliases: " + ex.Message);
			}
		}
	}
}
