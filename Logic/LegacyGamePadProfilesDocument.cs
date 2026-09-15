using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Preserves full input profiles.json documents while editing Gamepad / Gamepad UI KeyStatus only.
	/// </summary>
	public static class LegacyGamePadProfilesDocument
	{
		public const string GamepadSection = LegacyGamePadMigrationOrchestrator.GamepadSection;
		public const string GamepadUiSection = LegacyGamePadMigrationOrchestrator.GamepadUiSection;

		public static JObject LoadOrEmpty(string path)
		{
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return new JObject { ["Selected Profile"] = "Custom", ["Custom"] = new JObject() };
			return JObject.Parse(File.ReadAllText(path));
		}

		public static string SelectedProfileName(JObject root)
			=> root?.Value<string>("Selected Profile") ?? "Custom";

		public static JObject SelectedProfileObject(JObject root)
		{
			if (root == null)
				return null;
			string name = SelectedProfileName(root);
			return root[name] as JObject ?? root["Custom"] as JObject;
		}

		public static void ReadGamepadMaps(
			JObject root,
			Dictionary<string, List<string>> gamepad,
			Dictionary<string, List<string>> gamepadUi)
		{
			JObject profile = SelectedProfileObject(root);
			if (profile == null)
				return;
			ReadSection(profile[GamepadSection] as JObject, gamepad);
			ReadSection(profile[GamepadUiSection] as JObject, gamepadUi);
		}

		public static void WriteGamepadMaps(
			JObject root,
			Dictionary<string, List<string>> gamepad,
			Dictionary<string, List<string>> gamepadUi)
		{
			if (root == null)
				return;
			string name = SelectedProfileName(root);
			if (root[name] is not JObject profile)
			{
				profile = new JObject();
				root[name] = profile;
			}

			profile[GamepadSection] = ToSectionObject(gamepad);
			profile[GamepadUiSection] = ToSectionObject(gamepadUi);
		}

		public static void PatchSelectedProfileGamepad(
			JObject root,
			ModeMutation gamepad,
			ModeMutation gamepadUi)
		{
			if (root == null)
				return;
			string name = SelectedProfileName(root);
			if (root[name] is not JObject profile)
			{
				profile = new JObject();
				root[name] = profile;
			}
			PatchSection(profile, GamepadSection, gamepad);
			PatchSection(profile, GamepadUiSection, gamepadUi);
		}

		private static void PatchSection(JObject profile, string sectionName, ModeMutation mutation)
		{
			if (mutation == null)
				return;
			if (profile[sectionName] is not JObject section)
			{
				section = new JObject();
				profile[sectionName] = section;
			}
			foreach (string action in mutation.RemoveActions)
				section.Remove(action);
			foreach (KeyValuePair<string, List<string>> pair in mutation.UpsertActions)
				section[pair.Key] = ToArray(pair.Value);
		}

		/// <summary>
		/// Deep-clone JSON excluding Gamepad / Gamepad UI under the selected profile (and optionally all profiles).
		/// Used to assert unrelated fields survive migration.
		/// </summary>
		public static JToken CloneExcludingGamepadSections(JObject root)
		{
			if (root == null)
				return null;
			JObject clone = (JObject)root.DeepClone();
			foreach (JProperty prop in clone.Properties().ToList())
			{
				if (prop.Value is not JObject profile)
					continue;
				profile.Remove(GamepadSection);
				profile.Remove(GamepadUiSection);
			}

			return clone;
		}

		public static JToken CloneExcludingSelectedProfileGamepadSections(JObject root)
		{
			if (root == null)
				return null;
			JObject clone = (JObject)root.DeepClone();
			JObject profile = SelectedProfileObject(clone);
			profile?.Remove(GamepadSection);
			profile?.Remove(GamepadUiSection);
			return clone;
		}

		public static bool SemanticEqualsUnrelated(JObject a, JObject b)
			=> JToken.DeepEquals(
				CloneExcludingSelectedProfileGamepadSections(a),
				CloneExcludingSelectedProfileGamepadSections(b));

		public static bool TryWriteAtomic(string path, JObject root)
			=> LegacyGamePadAtomicFileIo.TryWriteAllTextAtomic(path, root.ToString(Formatting.Indented));

		private static void ReadSection(JObject section, Dictionary<string, List<string>> map)
		{
			if (section == null || map == null)
				return;
			foreach (KeyValuePair<string, JToken> prop in section)
			{
				var list = new List<string>();
				if (prop.Value is JArray arr)
				{
					for (int i = 0; i < arr.Count; i++)
						list.Add(arr[i]?.ToString());
				}

				map[prop.Key] = list;
			}
		}

		private static JObject ToSectionObject(Dictionary<string, List<string>> map)
		{
			var o = new JObject();
			if (map == null)
				return o;
			foreach (KeyValuePair<string, List<string>> pair in map)
			{
				var arr = new JArray();
				if (pair.Value != null)
				{
					for (int i = 0; i < pair.Value.Count; i++)
						arr.Add(pair.Value[i]);
				}

				o[pair.Key] = arr;
			}

			return o;
		}

		private static JArray ToArray(IReadOnlyList<string> tokens)
		{
			var array = new JArray();
			if (tokens != null)
			{
				for (int i = 0; i < tokens.Count; i++)
					array.Add(tokens[i]);
			}
			return array;
		}
	}
}
