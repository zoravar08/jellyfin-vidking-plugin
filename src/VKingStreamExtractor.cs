using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VidKing
{
    /// <summary>
    /// Runs the Playwright-based Python extractor to obtain a real MP4 URL from 1Embed.cc.
    ///
    /// The Python script (/VKingStreamExtractor.py, deployed alongside the plugin) launches a
    /// headless browser, navigates to the 1Embed embed page for the given TMDb id, clicks play,
    /// and captures the Cloudflare Worker MP4 URL from the browser's network responses. That URL
    /// is a direct, playable MP4 that Jellyfin's server-side player can consume natively.
    ///
    /// Extraction is expensive (~10-15s) and requires Playwright + Chromium, so it is done once
    /// per item at resolve time and the result is cached in memory for the lifetime of the server
    /// process.
    /// </summary>
    public sealed class VKingStreamExtractor
    {
        private readonly ILogger<VKingStreamExtractor> _logger;
        private readonly string _scriptPath;
        private readonly string _pythonPath;
        private readonly ConcurrentDictionary<string, ExtractedResult> _cache = new();

        public VKingStreamExtractor(ILogger<VKingStreamExtractor> logger)
        {
            _logger = logger;

            // Locate the Python script deployed next to the plugin assembly.
            var asmDir = Path.GetDirectoryName(typeof(VKingStreamExtractor).Assembly.Location);
            _scriptPath = Path.Combine(asmDir!, "VKingStreamExtractor.py");

            // Use the venv Python if available, else system python3.
            _pythonPath = FindPython();

            if (!File.Exists(_scriptPath))
            {
                _logger.LogWarning(
                    "VidKing: extractor script not found at {Path} — stream extraction disabled. " +
                    "Place VKingStreamExtractor.py next to Jellyfin.Plugin.VidKing.dll.",
                    _scriptPath);
            }

            if (_pythonPath is null)
            {
                _logger.LogWarning(
                    "VidKing: python3 not found on PATH — stream extraction disabled.");
            }

            _logger.LogInformation(
                "VidKing: extractor initialized (script={Script}, python={Python})",
                _scriptPath, _pythonPath ?? "(none)");
        }

        /// <summary>
        /// Extract a real MP4 URL for the given TMDb id. Returns null when extraction is
        /// disabled, fails, or the site does not serve a direct MP4.
        ///
        /// Results are cached per id so re-resolving an item (e.g. config change) does not
        /// re-launch the browser. For TV episodes the season/episode are passed to the Python
        /// script so it navigates to the episode-specific embed page.
        /// </summary>
        public async Task<ExtractedResult?> ExtractAsync(string tmdbId, bool isMovie, int? season, int? episode, CancellationToken ct)
        {
            var cacheKey = $"{tmdbId}|{isMovie}";
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            if (_scriptPath is null || !File.Exists(_scriptPath) || _pythonPath is null)
            {
                _logger.LogWarning("VidKing: extraction unavailable — script={Script}, python={Python}",
                    _scriptPath is null ? "null" : File.Exists(_scriptPath) ? "exists" : "MISSING",
                    _pythonPath is null ? "null" : File.Exists(_pythonPath) ? "exists" : "MISSING");
                return null;
            }

            try
            {
                var args = new[]
                {
                    _scriptPath,
                    "--id", tmdbId,
                    "--type", isMovie ? "movie" : "tv",
                    "--timeout", "15",
                    "--season", season?.ToString() ?? "1",
                    "--episode", episode?.ToString() ?? "1"
                };

                _logger.LogInformation("VidKing: launching extractor for {Id} ({Type})", tmdbId, isMovie ? "movie" : "tv");

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _pythonPath,
                        Arguments = string.Join(" ", args),
                        RedirectStandardOutput = true,
                        RedirectStandardError = false,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                var stdout = new StringBuilder();
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        stdout.Append(e.Data);
                        stdout.Append('\n');
                    }
                };

                process.Start();
                process.BeginOutputReadLine();

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

                await process.WaitForExitAsync(linked.Token);

                var output = stdout.ToString().Trim();
                _logger.LogInformation(
                    "VidKing: extractor output for {Id}: {Output}",
                    tmdbId, Truncate(output, 500));
                var result = ParseOutput(output);

                if (result?.Mp4Url is not null)
                {
                    _logger.LogInformation(
                        "VidKing: extracted MP4 URL for {Id}: {Url} (size={Size}, duration={Duration}s)",
                        tmdbId, MaskUrl(result.Mp4Url), result.Size, result.Duration);
                    _cache[cacheKey] = result;
                    return result;
                }

                _logger.LogWarning(
                    "VidKing: extractor returned no MP4 URL for {Id}. Output: {Output}",
                    tmdbId, Truncate(output, 300));
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VidKing: extraction crashed for {Id}", tmdbId);
                return null;
            }
        }

        /// <summary>
        /// Clear the extraction cache. Used when configuration changes so re-resolved items
        /// pick up new extractor settings.
        /// </summary>
        public void ClearCache() => _cache.Clear();

        private static ExtractedResult? ParseOutput(string output)
        {
            try
            {
                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;

                var url = root.GetProperty("mp4Url").GetString();
                if (url is null || !url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new ExtractedResult
                {
                    Mp4Url = url,
                    Size = root.TryGetProperty("size", out var sizeProp)
                        ? sizeProp.GetInt64() : 0L,
                    Duration = root.TryGetProperty("duration", out var durProp)
                        ? (float)durProp.GetDouble() : 0f
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? FindPython()
        {
            // Check common locations.
            var candidates = new[]
            {
                "/tmp/playwright-venv/bin/python3",
                "/usr/bin/python3",
                "/usr/local/bin/python3",
                "python3"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = candidate,
                            Arguments = "-c \"import playwright; print('ok')\"",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    process.Start();
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                    if (process.ExitCode == 0)
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Try next candidate.
                }
            }

            return null;
        }

        private static string MaskUrl(string url)
        {
            // Sanitize URLs in logs: strip the long worker path segment.
            try
            {
                var uri = new Uri(url);
                var builder = new UriBuilder(uri) { Path = "/" };
                return builder.Uri.ToString();
            }
            catch
            {
                return url;
            }
        }

        private static string Truncate(string s, int max)
        {
            return s.Length <= max ? s : s[..max] + "...[truncated]";
        }
    }

    /// <summary>Result of a successful stream extraction.</summary>
    public sealed class ExtractedResult
    {
        /// <summary>Direct MP4 URL that Jellyfin's player can consume natively.</summary>
        public string Mp4Url { get; set; } = string.Empty;

        /// <summary>Content length from the MP4 HEAD response, if available.</summary>
        public long Size { get; set; }

        /// <summary>Duration in seconds from ffprobe, if available.</summary>
        public float Duration { get; set; }
    }
}