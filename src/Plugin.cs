using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.VidKing
{
    // ==========================================
    // 1. CONFIGURATION MODEL
    // ==========================================
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Base URL for movie ids: &lt;base&gt;/embed/movie/&lt;id&gt;.</summary>
        public string MovieBaseUrl { get; set; } = "https://cinesrc.st";

        /// <summary>Base URL for series ids: &lt;base&gt;/embed/tv/&lt;id&gt;/&lt;season&gt;/&lt;episode&gt;.</summary>
        public string TvBaseUrl { get; set; } = "https://cinesrc.st";

        /// <summary>
        /// When true, the resolver attempts to extract a real MP4 stream URL (Option B) from
        /// the upstream embed page using a Playwright-based extractor. When successful, the
        /// real URL replaces the embed URL in ShortcutPath and Jellyfin's native player can
        /// play it directly. When extraction fails or is disabled, playback falls back to the
        /// iframe overlay (Tier 3). The extractor script (VKingStreamExtractor.py) must be
        /// deployed alongside the plugin DLL and Playwright + Chromium must be available.
        /// </summary>
        public bool EnableStreamExtraction { get; set; } = false;
    }

    // ==========================================
    // 2. MAIN PLUGIN ENTRY POINT
    // ==========================================
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>Virtual file extension, lower case, dot included.</summary>
        public const string FileExtension = ".vking";

        /// <summary>
        /// Container reported for a resolved item. Core never probes shortcut items, so
        /// BaseItem.GetVersionInfo falls back to the local file's extension - which would
        /// hand ffmpeg "-f vking" and kill playback before it starts. EncodingHelper's
        /// GetInputFormat maps "strm" to null (no -f at all, ffmpeg autodetects), which is
        /// exactly how core's own .strm files reach the encoder.
        /// </summary>
        public const string ShortcutContainer = "strm";

        public override string Name => "VidKing Integration";
        public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin? Instance { get; private set; }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "vidking",
                    EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
                }
            };
        }
    }

    // ==========================================
    // 3. VIRTUAL FILE -> URL (pure, testable)
    // ==========================================

    /// <summary>
    /// What a .vking file asks for: either an explicit URL, or an id plus optional
    /// season/episode written as "id/season/episode".
    /// </summary>
    public sealed class VKingTarget
    {
        public string? Url { get; init; }

        public string? Id { get; init; }

        public int? Season { get; init; }

        public int? Episode { get; init; }
    }

    public static class VKingUrl
    {
        /// <summary>Movie form: &lt;base&gt;/embed/movie/&lt;id&gt;.</summary>
        public static string BuildMovie(string baseUrl, string id)
        {
            return string.Concat(baseUrl.TrimEnd('/'), "/embed/movie/", Uri.EscapeDataString(id));
        }

        /// <summary>
        /// Sites in the vidsrc-clone family that need the TV episode as query params
        /// (?s=&amp;e=) instead of path segments - cinesrc.st moved to this format,
        /// confirmed by curl (path form 404s, query form 200s). Keep in sync with
        /// TV_URL_QUERY_PARAM_HOSTS in VKingStreamExtractor.py.
        /// </summary>
        private static readonly string[] QueryParamEpisodeHosts = { "cinesrc.st" };

        /// <summary>Episode form: path segments normally, query params for sites that need it.</summary>
        public static string BuildEpisode(string baseUrl, string id, int season, int episode)
        {
            var trimmed = baseUrl.TrimEnd('/');
            var encodedId = Uri.EscapeDataString(id);
            var host = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

            if (Array.Exists(QueryParamEpisodeHosts, h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
            {
                return string.Concat(
                    trimmed, "/embed/tv/", encodedId,
                    "?s=", season.ToString(CultureInfo.InvariantCulture),
                    "&e=", episode.ToString(CultureInfo.InvariantCulture));
            }

            return string.Concat(
                trimmed,
                "/embed/tv/",
                encodedId,
                "/",
                season.ToString(CultureInfo.InvariantCulture),
                "/",
                episode.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Reads the first meaningful line of a .vking file. Blank lines and lines
        /// starting with '#' are skipped; a blank/comment-only file returns null so the
        /// scanner skips it entirely.
        /// </summary>
        public static VKingTarget? Parse(string? contents)
        {
            if (string.IsNullOrWhiteSpace(contents))
            {
                return null;
            }

            foreach (var raw in contents.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return new VKingTarget { Url = line };
                }

                // "id" or "id/season/episode"
                var parts = line.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                if (parts.Length >= 3
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var season)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode))
                {
                    return new VKingTarget { Id = parts[0], Season = season, Episode = episode };
                }

                return new VKingTarget { Id = parts[0] };
            }

            return null;
        }

        /// <summary>
        /// Final URL for a parsed target. Season/episode come from the file when given,
        /// otherwise from the filename parse the caller passes in. Both present -> episode
        /// URL off the TV base, otherwise movie URL off the movie base. Returns null when
        /// there is nothing playable, including when the base for that form is blank.
        /// </summary>
        public static string? Resolve(
            VKingTarget? target,
            string movieBaseUrl,
            string tvBaseUrl,
            int? season,
            int? episode)
        {
            if (target is null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(target.Url))
            {
                return target.Url;
            }

            if (string.IsNullOrEmpty(target.Id))
            {
                return null;
            }

            var s = target.Season ?? season;
            var e = target.Episode ?? episode;

            if (s.HasValue && e.HasValue)
            {
                return string.IsNullOrWhiteSpace(tvBaseUrl)
                    ? null
                    : BuildEpisode(tvBaseUrl, target.Id, s.Value, e.Value);
            }

            return string.IsNullOrWhiteSpace(movieBaseUrl) ? null : BuildMovie(movieBaseUrl, target.Id);
        }

        /// <summary>
        /// Which metadata provider an id belongs to: all digits is TMDb, "tt1234567" is
        /// IMDb, anything else (or a bare URL) gets no id and falls back to name matching.
        /// </summary>
        public static bool TryGetProvider(string? id, out MetadataProvider provider)
        {
            provider = default;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            if (id.All(char.IsAsciiDigit))
            {
                provider = MetadataProvider.Tmdb;
                return true;
            }

            if (id.Length > 2
                && id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                && id.AsSpan(2).ToString().All(char.IsAsciiDigit))
            {
                provider = MetadataProvider.Imdb;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Emby.Naming's episode parser ignores paths whose extension it does not know as
        /// video, and .vking is deliberately not registered as one. Hand the parser the
        /// same name with a known video extension so S01E02 is read normally.
        /// </summary>
        public static string ParserPath(string path)
        {
            return Path.ChangeExtension(path, ".mkv");
        }

        /// <summary>True when the path is a .vking virtual file.</summary>
        public static bool IsVirtualFile(string? path)
        {
            return path is not null
                && string.Equals(Path.GetExtension(path), Plugin.FileExtension, StringComparison.OrdinalIgnoreCase);
        }
    }
}
