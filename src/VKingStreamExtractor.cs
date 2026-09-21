using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        /// Extract a real MP4 URL for the given TMDb id, trying each base in <paramref
        /// name="bases"/> in order until one yields a direct link. Returns null when
        /// extraction is disabled, fails, or none of the sites serve a direct MP4.
        ///
        /// Results are cached per id (and per base list, so a config change that adds a
        /// site is not served a stale miss) so re-resolving an item does not re-launch the
        /// browser. For TV episodes the season/episode are passed to the Python script so
        /// it navigates to the episode-specific embed page.
        /// </summary>
        public async Task<ExtractedResult?> ExtractAsync(
            string tmdbId, bool isMovie, int? season, int? episode, IReadOnlyList<string> bases, CancellationToken ct)
        {
            // season/episode matter: without them every episode of a series shares one
            // cache key, so the second episode extracted in a server's lifetime silently
            // gets served the first episode's cached URL instead of its own (caught live
            // testing Zero Day S01E02 - returned S01E01's link in 0.2s, a cache hit, not
            // a real extraction).
            var cacheKey = $"{tmdbId}|{isMovie}|{season}|{episode}|{string.Join(",", bases)}";
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
                _logger.LogInformation("VidKing: launching extractor for {Id} ({Type})", tmdbId, isMovie ? "movie" : "tv");

                var startInfo = new ProcessStartInfo
                {
                    FileName = _pythonPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // ArgumentList, not a joined Arguments string: tmdbId comes from .vking file
                // contents, and a joined string would let an id like "123 --timeout 999"
                // smuggle extra flags into the Python argparse call. ArgumentList passes each
                // element through as a single argv entry, no shell/space parsing involved.
                startInfo.ArgumentList.Add(_scriptPath);
                startInfo.ArgumentList.Add("--id");
                startInfo.ArgumentList.Add(tmdbId);
                startInfo.ArgumentList.Add("--type");
                startInfo.ArgumentList.Add(isMovie ? "movie" : "tv");
                // 25s budget per candidate site - the script splits this across bases and
                // tries each in turn, so more candidates needs proportionally more total time.
                // Was 15s: live bcine.ru/cinesrc.st navigations ran 19-21s, so the 4-base
                // sweep (60s script budget) got hard-killed by the C# timeout mid-navigation
                // on the last candidate instead of ever finishing (Lucky S01E04, 2026-09-21).
                var perBaseSeconds = 25;
                var scriptTimeoutSeconds = perBaseSeconds * Math.Max(bases.Count, 1);

                startInfo.ArgumentList.Add("--timeout");
                startInfo.ArgumentList.Add(scriptTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--season");
                startInfo.ArgumentList.Add(season?.ToString() ?? "1");
                startInfo.ArgumentList.Add("--episode");
                startInfo.ArgumentList.Add(episode?.ToString() ?? "1");
                startInfo.ArgumentList.Add("--bases");
                startInfo.ArgumentList.Add(string.Join(",", bases));

                using var process = new Process
                {
                    StartInfo = startInfo,
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

                // Grace buffer over the script's own --timeout for browser launch/teardown,
                // so the process is killed for genuinely hanging rather than racing the
                // script's own internal deadline.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(scriptTimeoutSeconds + 10));
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

                // success:false output has no mp4Url key at all (every source failed) -
                // TryGetProperty so that's a clean null, not a KeyNotFoundException logged
                // upstream as "extraction crashed".
                if (!root.TryGetProperty("mp4Url", out var urlProp))
                {
                    return null;
                }

                var url = urlProp.GetString();
                if (url is null
                    || !(url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                        || url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)))
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