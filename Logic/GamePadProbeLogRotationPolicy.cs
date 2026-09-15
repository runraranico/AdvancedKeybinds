using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AdvancedKeybinds.Core
{
	/// <summary>
	/// Injectable limits for GamePadProbe rotated logs (tests use tiny values; product uses 10MiB × 5).
	/// Does not touch legacy GamePadProbe_LIVE.txt or dated history files.
	/// </summary>
	public sealed class GamePadProbeLogRotationOptions
	{
		public const long DefaultMaxBytesPerGeneration = 10L * 1024L * 1024L;
		public const int DefaultMaxGenerations = 5;

		public long MaxBytesPerGeneration { get; set; } = DefaultMaxBytesPerGeneration;
		public int MaxGenerations { get; set; } = DefaultMaxGenerations;
		public string FileNamePrefix { get; set; } = "GamePadProbe";
		public string ManifestFileName { get; set; } = "GamePadProbe_manifest.txt";
		public int FlushLineThreshold { get; set; } = 32;
	}

	/// <summary>Pure helpers for fixed-generation rotation (no Terraria / no file I/O).</summary>
	public static class GamePadProbeLogRotationPolicy
	{
		public static string GenerationFileName(string prefix, int index)
		{
			if (string.IsNullOrEmpty(prefix))
				prefix = "GamePadProbe";
			if (index < 0)
				index = 0;
			return prefix + "_" + index.ToString("00", CultureInfo.InvariantCulture) + ".txt";
		}

		public static int NormalizeIndex(int index, int maxGenerations)
		{
			if (maxGenerations <= 0)
				return 0;
			if (index < 0)
				return 0;
			return index % maxGenerations;
		}

		public static int NextIndex(int currentIndex, int maxGenerations)
			=> NormalizeIndex(currentIndex + 1, maxGenerations);

		public static bool ShouldRotateBeforeWrite(long bytesWritten, long upcomingLineBytes, long maxBytesPerGeneration)
		{
			if (maxBytesPerGeneration <= 0)
				return false;
			// Rotate when the current generation already reached the cap; never drop the upcoming line.
			_ = upcomingLineBytes;
			return bytesWritten >= maxBytesPerGeneration;
		}

		public static long EstimateLineBytes(string line)
		{
			if (line == null)
				return 2;
			return Encoding.UTF8.GetByteCount(line) + 2;
		}

		/// <summary>
		/// Parse manifest current index. Returns false when missing/corrupt (caller recovers from files).
		/// </summary>
		public static bool TryParseManifestCurrent(string manifestText, out int currentIndex)
		{
			currentIndex = 0;
			if (string.IsNullOrWhiteSpace(manifestText))
				return false;

			string[] lines = manifestText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < lines.Length; i++)
			{
				string line = lines[i].Trim();
				if (line.StartsWith("current=", StringComparison.OrdinalIgnoreCase))
				{
					string value = line.Substring("current=".Length).Trim();
					if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
						&& parsed >= 0)
					{
						currentIndex = parsed;
						return true;
					}

					return false;
				}
			}

			return false;
		}

		public static string FormatManifest(int currentIndex, GamePadProbeLogRotationOptions options)
		{
			if (options == null)
				options = new GamePadProbeLogRotationOptions();
			var sb = new StringBuilder(128);
			sb.AppendLine("version=1");
			sb.Append("current=").AppendLine(currentIndex.ToString(CultureInfo.InvariantCulture));
			sb.Append("prefix=").AppendLine(options.FileNamePrefix ?? "GamePadProbe");
			sb.Append("maxBytes=").AppendLine(options.MaxBytesPerGeneration.ToString(CultureInfo.InvariantCulture));
			sb.Append("maxGens=").AppendLine(options.MaxGenerations.ToString(CultureInfo.InvariantCulture));
			return sb.ToString();
		}

		/// <summary>
		/// Legacy / out-of-band names that must never be opened or overwritten by rotation.
		/// </summary>
		public static bool IsProtectedLegacyProbeFileName(string fileName)
		{
			if (string.IsNullOrEmpty(fileName))
				return false;
			if (string.Equals(fileName, "GamePadProbe_LIVE.txt", StringComparison.OrdinalIgnoreCase))
				return true;
			// Dated history: GamePadProbe_yyyy-MM-dd_HHmmss.txt
			if (fileName.StartsWith("GamePadProbe_", StringComparison.OrdinalIgnoreCase)
				&& fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
				&& fileName.Length > "GamePadProbe_".Length + 4)
			{
				string mid = fileName.Substring("GamePadProbe_".Length, fileName.Length - "GamePadProbe_".Length - 4);
				if (mid.Length >= 15 && mid[4] == '-' && mid[7] == '-' && mid.IndexOf('_') > 0)
					return true;
			}

			return false;
		}
	}

	/// <summary>
	/// Fixed-generation rotated text log. Failures never throw to callers (optional out error).
	/// </summary>
	public sealed class GamePadProbeRotatedLogStore : IDisposable
	{
		private readonly string _directory;
		private readonly GamePadProbeLogRotationOptions _options;
		private StreamWriter _writer;
		private int _currentIndex;
		private long _bytesWritten;
		private int _bufferedLines;
		private bool _opened;
		private bool _disabled;
		private string _lastError;

		public GamePadProbeRotatedLogStore(string directory, GamePadProbeLogRotationOptions options)
		{
			_directory = directory ?? throw new ArgumentNullException(nameof(directory));
			_options = options ?? new GamePadProbeLogRotationOptions();
			if (_options.MaxGenerations < 1)
				_options.MaxGenerations = 1;
			if (_options.MaxBytesPerGeneration < 1)
				_options.MaxBytesPerGeneration = 1;
			if (_options.FlushLineThreshold < 1)
				_options.FlushLineThreshold = 1;
		}

		public int CurrentGenerationIndex => _currentIndex;
		public long CurrentGenerationBytes => _bytesWritten;
		public bool IsOpen => _opened && _writer != null;
		public string LastError => _lastError;
		public bool LoggingDisabled => _disabled;

		/// <summary>When true, never create or append files.</summary>
		public void SetLoggingDisabled(bool disabled)
		{
			_disabled = disabled;
			if (disabled)
				CloseQuiet();
		}

		public void EnsureOpen()
		{
			if (_disabled || _opened)
				return;
			try
			{
				Directory.CreateDirectory(_directory);
				_currentIndex = RecoverCurrentIndex();
				OpenGeneration(_currentIndex, append: true, writeBannerIfEmpty: true);
				_opened = true;
			}
			catch (Exception ex)
			{
				_lastError = ex.GetType().Name + ": " + ex.Message;
				CloseQuiet();
			}
		}

		public bool TryWriteLine(string line, bool flushImmediate)
		{
			if (_disabled)
				return false;
			try
			{
				EnsureOpen();
				if (_writer == null)
					return false;

				long lineBytes = GamePadProbeLogRotationPolicy.EstimateLineBytes(line);
				if (GamePadProbeLogRotationPolicy.ShouldRotateBeforeWrite(
					_bytesWritten, lineBytes, _options.MaxBytesPerGeneration))
				{
					if (!TryRotate())
						return false;
				}

				_writer.WriteLine(line ?? string.Empty);
				_bytesWritten += lineBytes;
				_bufferedLines++;

				if (flushImmediate || _bufferedLines >= _options.FlushLineThreshold)
					FlushQuiet();

				return true;
			}
			catch (Exception ex)
			{
				_lastError = ex.GetType().Name + ": " + ex.Message;
				return false;
			}
		}

		public void Flush()
		{
			if (_disabled)
				return;
			FlushQuiet();
		}

		public void Dispose()
		{
			try
			{
				FlushQuiet();
			}
			finally
			{
				CloseQuiet();
			}
		}

		private bool TryRotate()
		{
			try
			{
				FlushQuiet();
				int next = GamePadProbeLogRotationPolicy.NextIndex(_currentIndex, _options.MaxGenerations);
				CloseWriterOnly();
				OpenGeneration(next, append: false, writeBannerIfEmpty: true);
				_currentIndex = next;
				WriteManifestQuiet();
				return _writer != null;
			}
			catch (Exception ex)
			{
				_lastError = ex.GetType().Name + ": " + ex.Message;
				return false;
			}
		}

		private void OpenGeneration(int index, bool append, bool writeBannerIfEmpty)
		{
			index = GamePadProbeLogRotationPolicy.NormalizeIndex(index, _options.MaxGenerations);
			string name = GamePadProbeLogRotationPolicy.GenerationFileName(_options.FileNamePrefix, index);
			if (GamePadProbeLogRotationPolicy.IsProtectedLegacyProbeFileName(name))
				throw new InvalidOperationException("Refusing protected probe file name: " + name);

			string path = Path.Combine(_directory, name);
			bool existed = File.Exists(path);
			long existingLen = existed ? new FileInfo(path).Length : 0L;

			_writer = new StreamWriter(path, append, Encoding.UTF8)
			{
				AutoFlush = false
			};
			_currentIndex = index;
			_bytesWritten = append && existed ? existingLen : 0L;
			_bufferedLines = 0;

			if (writeBannerIfEmpty && (!append || existingLen == 0L))
			{
				string banner = "# GamePadProbe gen="
					+ index.ToString("00", CultureInfo.InvariantCulture)
					+ " maxBytes="
					+ _options.MaxBytesPerGeneration.ToString(CultureInfo.InvariantCulture)
					+ " maxGens="
					+ _options.MaxGenerations.ToString(CultureInfo.InvariantCulture);
				_writer.WriteLine(banner);
				_bytesWritten += GamePadProbeLogRotationPolicy.EstimateLineBytes(banner);
				FlushQuiet();
			}

			WriteManifestQuiet();
		}

		private int RecoverCurrentIndex()
		{
			string manifestPath = Path.Combine(_directory, _options.ManifestFileName ?? "GamePadProbe_manifest.txt");
			try
			{
				if (File.Exists(manifestPath))
				{
					string text = File.ReadAllText(manifestPath);
					if (GamePadProbeLogRotationPolicy.TryParseManifestCurrent(text, out int parsed))
						return GamePadProbeLogRotationPolicy.NormalizeIndex(parsed, _options.MaxGenerations);
				}
			}
			catch
			{
				// fall through to file scan
			}

			int bestIndex = 0;
			DateTime bestWrite = DateTime.MinValue;
			bool any = false;
			for (int i = 0; i < _options.MaxGenerations; i++)
			{
				string path = Path.Combine(
					_directory,
					GamePadProbeLogRotationPolicy.GenerationFileName(_options.FileNamePrefix, i));
				try
				{
					if (!File.Exists(path))
						continue;
					var info = new FileInfo(path);
					if (!any || info.LastWriteTimeUtc >= bestWrite)
					{
						any = true;
						bestWrite = info.LastWriteTimeUtc;
						bestIndex = i;
					}
				}
				catch
				{
					// ignore
				}
			}

			return bestIndex;
		}

		private void WriteManifestQuiet()
		{
			try
			{
				string manifestPath = Path.Combine(
					_directory,
					_options.ManifestFileName ?? "GamePadProbe_manifest.txt");
				File.WriteAllText(manifestPath, GamePadProbeLogRotationPolicy.FormatManifest(_currentIndex, _options));
			}
			catch (Exception ex)
			{
				_lastError = ex.GetType().Name + ": " + ex.Message;
			}
		}

		private void FlushQuiet()
		{
			try
			{
				_writer?.Flush();
				_bufferedLines = 0;
			}
			catch (Exception ex)
			{
				_lastError = ex.GetType().Name + ": " + ex.Message;
			}
		}

		private void CloseWriterOnly()
		{
			try { _writer?.Dispose(); } catch { /* ignore */ }
			_writer = null;
			_bytesWritten = 0;
			_bufferedLines = 0;
		}

		private void CloseQuiet()
		{
			CloseWriterOnly();
			_opened = false;
		}
	}
}
