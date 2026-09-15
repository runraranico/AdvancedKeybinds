using System;
using System.IO;
using System.Text;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Atomic file helpers for legacy GP migration (Terraria-free).
	/// Prefer temp + replace so partial writes do not corrupt the destination.
	/// </summary>
	public static class LegacyGamePadAtomicFileIo
	{
		public static bool TryCopyFileNoOverwrite(string src, string dest)
		{
			if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dest) || !File.Exists(src))
				return false;
			if (File.Exists(dest))
				return false;
			string dir = Path.GetDirectoryName(dest);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);
			File.Copy(src, dest, overwrite: false);
			return File.Exists(dest);
		}

		/// <summary>
		/// Write via temp file then replace. On failure the original path is left unchanged when possible.
		/// </summary>
		public static bool TryWriteAllTextAtomic(string path, string contents)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			contents ??= string.Empty;
			string dir = Path.GetDirectoryName(path);
			if (string.IsNullOrEmpty(dir))
				dir = ".";
			Directory.CreateDirectory(dir);

			string temp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
			try
			{
				File.WriteAllText(temp, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				if (File.Exists(path))
				{
					string backup = path + ".atomic-bak-" + Guid.NewGuid().ToString("N");
					File.Replace(temp, path, backup, ignoreMetadataErrors: true);
					try { File.Delete(backup); } catch { /* best-effort */ }
				}
				else
				{
					File.Move(temp, path);
				}

				return true;
			}
			catch
			{
				try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
				return false;
			}
		}

		/// <summary>
		/// Restore bytes via temp file then Replace/Move. Mid-failure must not leave a half-written destination.
		/// Cleans up temp and Replace backup files best-effort.
		/// </summary>
		public static bool TryWriteAllBytesAtomic(string path, byte[] bytes)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			bytes ??= Array.Empty<byte>();
			string dir = Path.GetDirectoryName(path);
			if (string.IsNullOrEmpty(dir))
				dir = ".";
			Directory.CreateDirectory(dir);

			string temp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
			string replaceBackup = null;
			try
			{
				File.WriteAllBytes(temp, bytes);
				if (File.Exists(path))
				{
					replaceBackup = path + ".atomic-bak-" + Guid.NewGuid().ToString("N");
					File.Replace(temp, path, replaceBackup, ignoreMetadataErrors: true);
					try { File.Delete(replaceBackup); } catch { /* best-effort */ }
					replaceBackup = null;
				}
				else
				{
					File.Move(temp, path);
				}

				return true;
			}
			catch
			{
				try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
				try
				{
					if (!string.IsNullOrEmpty(replaceBackup) && File.Exists(replaceBackup))
						File.Delete(replaceBackup);
				}
				catch { /* ignore */ }
				return false;
			}
		}

		public static bool TryBackupWithStamp(string path, string stamp, out string destPath)
		{
			destPath = null;
			if (string.IsNullOrEmpty(path) || !File.Exists(path))
				return true; // missing = not required
			destPath = path + ".bak-" + (stamp ?? "stamp");
			return TryCopyFileNoOverwrite(path, destPath);
		}
	}
}
