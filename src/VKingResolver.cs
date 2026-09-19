using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.VidKing
{
    /// <summary>
    /// Resolves *.vking text files into shortcut Movies or Episodes.
    ///
    /// Tier 1 (Option B): after resolving the embed URL, tries to extract a real MP4
    /// stream URL via the Playwright-based extractor. When successful, the real URL is
    /// stored in ShortcutPath so Jellyfin's own player can process it natively (direct
    /// play / transcoding / download / cast). When extraction fails or is disabled, the
    /// embed URL is kept and playback falls through to the iframe overlay (Tier 3).
    ///
    /// Tier 2 (Option A): for web clients that cannot play a cross-origin MP4 directly,
    /// a proxy endpoint (/VidKing/Stream/{itemId}) serves the bytes with CORS headers.
    /// The client script decides which URL to hand to the player.
    /// </summary>
    public class VKingResolver : IItemResolver
    {
        private readonly NamingOptions _namingOptions;
        private readonly ILogger<VKingResolver> _logger;
        private readonly VKingStreamExtractor _extractor;

        public VKingResolver(
            NamingOptions namingOptions,
            ILogger<VKingResolver> logger,
            VKingStreamExtractor extractor)
        {
            _namingOptions = namingOptions;
            _logger = logger;
            _extractor = extractor;
            _logger.LogInformation("VidKing: {Ext} resolver active (movies + tv)", Plugin.FileExtension);
        }

        public ResolverPriority Priority => ResolverPriority.Plugin;

        public BaseItem? ResolvePath(ItemResolveArgs args)
        {
            var isTv = args.GetCollectionType() == CollectionType.tvshows;

            if (args.IsDirectory)
            {
                return isTv ? ResolveSeries(args) : null;
            }

            if (!VKingUrl.IsVirtualFile(args.Path))
            {
                return null;
            }

            var contents = ReadFile(args.Path);
            if (contents is null)
            {
                return null;
            }

            var target = VKingUrl.Parse(contents);
            var config = Plugin.Instance?.Configuration;
            var movieBase = config?.MovieBaseUrl ?? string.Empty;
            var tvBase = config?.TvBaseUrl ?? string.Empty;

            return isTv
                ? ResolveEpisode(args, target, movieBase, tvBase)
                : ResolveMovie(args, target, movieBase);
        }

        private BaseItem? ResolveMovie(ItemResolveArgs args, VKingTarget? target, string movieBase)
        {
            var url = VKingUrl.Resolve(target, movieBase, string.Empty, null, null);
            if (url is null)
            {
                _logger.LogWarning("VidKing: {Path} has no id or URL, skipping", args.Path);
                return null;
            }

            var movie = new Movie
            {
                Path = args.Path,
                Name = Path.GetFileNameWithoutExtension(args.Path),
                IsShortcut = true,
                ShortcutPath = url,
                Container = Plugin.ShortcutContainer
            };

            StampProviderId(movie, target?.Id, args.Path);

            // If Jellyfin already has a valid MP4 ShortcutPath from a prior extraction,
            // keep it — don't overwrite with the embed URL, and don't re-extract.
            var existing = ReadExistingShortcutPath(args.Path);
            if (existing is not null && IsValidMp4Url(existing))
            {
                movie.ShortcutPath = existing;
                _logger.LogInformation(
                    "VidKing: {Path} preserving existing MP4 ShortcutPath {Existing} (skip re-resolve)",
                    args.Path, existing);
                return movie;
            }

            // Tier 1: try to extract a real MP4 URL. If successful, that replaces the
            // embed URL in ShortcutPath so the native player can use it directly.
            if (Plugin.Instance?.Configuration?.EnableStreamExtraction == true)
            {
                var extracted = ExtractRealUrlAsync(args.Path, target, movieBase, isMovie: true).GetAwaiter().GetResult();
                if (extracted is not null)
                {
                    movie.ShortcutPath = extracted;
                    _logger.LogInformation(
                        "VidKing: {Path} extracted real MP4 URL {Mp4Url} (embed {EmbedUrl} abandoned)",
                        args.Path, extracted, url);
                }
                else
                {
                    _logger.LogInformation(
                        "VidKing: {Path} extraction failed or disabled, keeping embed URL {Url} (Tier 3 iframe fallback)",
                        args.Path, url);
                }
            }

            _logger.LogInformation("VidKing: movie {Path} -> {Url}", args.Path, movie.ShortcutPath);
            return movie;
        }

        /// <summary>
        /// Read the ShortcutPath currently stored in Jellyfin's database for this item.
        /// If the DB has a valid MP4 URL, the resolver preserves it instead of overwriting
        /// with the embed URL on every access.
        /// </summary>
        private static string? ReadExistingShortcutPath(string path)
        {
            try
            {
                using var conn = new SqliteConnection("Data Source=" + Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Jellyfin", "data", "jellyfin.db"));
                conn.Open();
                using var cmd = new SqliteCommand(
                    "SELECT json_extract(Data, '$.ShortcutPath') FROM BaseItems WHERE Path = ?", conn);
                cmd.Parameters.AddWithValue("?", path);
                var raw = cmd.ExecuteScalar() as string;
                if (raw is not null && raw.StartsWith("http") && raw.Contains(".mp4"))
                    return raw;
            }
            catch (Exception ex)
            {
                // DB access can fail during startup; silently skip preservation.
                //_logger.LogWarning("VidKing: could not read existing ShortcutPath for {Path}: {Err}", path, ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Quick check: does this look like a real MP4 URL (not an embed HTML page)?
        /// </summary>
        private static bool IsValidMp4Url(string url)
        {
            return !string.IsNullOrEmpty(url)
                && url.StartsWith("http")
                && url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                && !url.Contains("/embed/");
        }

        private BaseItem? ResolveEpisode(ItemResolveArgs args, VKingTarget? target, string movieBase, string tvBase)
        {
            var parsed = new EpisodeResolver(_namingOptions)
                .Resolve(VKingUrl.ParserPath(args.Path), false, null, null, null, true);

            var url = VKingUrl.Resolve(target, movieBase, tvBase, parsed?.SeasonNumber, parsed?.EpisodeNumber);
            if (url is null)
            {
                _logger.LogWarning("VidKing: {Path} has no id or URL, skipping", args.Path);
                return null;
            }

            var season = target?.Season ?? parsed?.SeasonNumber;
            var episode = target?.Episode ?? parsed?.EpisodeNumber;
            if (!season.HasValue || !episode.HasValue)
            {
                _logger.LogWarning(
                    "VidKing: {Path} has no season/episode in the filename; add \"id/season/episode\" to the file",
                    args.Path);
            }

            var item = new Episode
            {
                Path = args.Path,
                Name = Path.GetFileNameWithoutExtension(args.Path),
                ParentIndexNumber = season,
                IndexNumber = episode,
                IsShortcut = true,
                ShortcutPath = url,
                Container = Plugin.ShortcutContainer
            };

            _logger.LogInformation(
                "VidKing: episode {Path} S{Season}E{Episode} -> {Url}",
                args.Path, season, episode, url);

            // Tier 1: try to extract a real MP4 URL.
            if (Plugin.Instance?.Configuration?.EnableStreamExtraction == true)
            {
                var extracted = ExtractRealUrlAsync(args.Path, target, tvBase, isMovie: false).GetAwaiter().GetResult();
                if (extracted is not null)
                {
                    item.ShortcutPath = extracted;
                    _logger.LogInformation(
                        "VidKing: {Path} extracted real MP4 URL {Mp4Url} (embed {EmbedUrl} abandoned)",
                        args.Path, extracted, url);
                }
            }

            _logger.LogInformation("VidKing: episode {Path} -> {Url}", args.Path, item.ShortcutPath);
            return item;
        }

        private async Task<string?> ExtractRealUrlAsync(
            string argsPath, VKingTarget? target, string baseUrl, bool isMovie,
            int? season = null, int? episode = null)
        {
            // Only attempt extraction for known base URLs (vidsrc.wiki / vidsrc.st / 1embed.cc).
            // If the user configured a custom base, skip extraction.
            if (!IsKnownExtractorBase(baseUrl))
            {
                _logger.LogDebug("VidKing: {Path} base URL {Base} is not a known extractor target, skipping extraction",
                    argsPath, baseUrl);
                return null;
            }

            var id = target?.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            try
            {
                var result = await _extractor.ExtractAsync(id, isMovie, season, episode, CancellationToken.None);
                if (result?.Mp4Url is not null)
                {
                    return result.Mp4Url;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VidKing: extraction failed for {Path}, falling back to embed URL", argsPath);
            }

            return null;
        }

        internal static bool IsKnownExtractorBase(string baseUrl)
        {
            return baseUrl.Contains("vidsrc.wiki", StringComparison.OrdinalIgnoreCase)
                || baseUrl.Contains("vidsrc.st", StringComparison.OrdinalIgnoreCase)
                || baseUrl.Contains("1embed.cc", StringComparison.OrdinalIgnoreCase)
                || baseUrl.Contains("zxcstream", StringComparison.OrdinalIgnoreCase)
                || baseUrl.Contains("zxcprime", StringComparison.OrdinalIgnoreCase);
        }

        private BaseItem? ResolveSeries(ItemResolveArgs args)
        {
            if (args.IsPhysicalRoot || args.IsVf || args.Parent is Series)
            {
                return null;
            }

            string? firstVKing = null;

            foreach (var file in Directory.EnumerateFiles(args.Path, "*", SearchOption.AllDirectories))
            {
                if (VKingUrl.IsVirtualFile(file))
                {
                    firstVKing ??= file;
                    continue;
                }

                if (IsRealVideo(file))
                {
                    return null;
                }
            }

            if (firstVKing is null)
            {
                return null;
            }

            var contents = ReadFile(firstVKing);
            var target = VKingUrl.Parse(contents);

            var series = new Series
            {
                Path = args.Path,
                Name = Path.GetFileName(args.Path.TrimEnd(Path.DirectorySeparatorChar))
            };

            StampProviderId(series, target?.Id, args.Path);
            _logger.LogInformation("VidKing: series folder {Path} claimed (id from {File})", args.Path, firstVKing);
            return series;
        }

        private bool IsRealVideo(string path)
        {
            var extension = Path.GetExtension(path.AsSpan());
            if (extension.IsEmpty)
            {
                return false;
            }

            foreach (var known in _namingOptions.VideoFileExtensions)
            {
                if (extension.Equals(known, StringComparison.OrdinalIgnoreCase))
                {
                    return !string.Equals(known, Plugin.FileExtension, StringComparison.OrdinalIgnoreCase);
                }
            }

            return false;
        }

        private void StampProviderId(BaseItem item, string? id, string path)
        {
            if (VKingUrl.TryGetProvider(id, out var provider))
            {
                item.SetProviderId(provider, id!);
                _logger.LogInformation("VidKing: {Path} tagged {Provider}={Id}", path, provider, id);
                return;
            }

            _logger.LogInformation(
                "VidKing: {Path} has no numeric TMDb / tt IMDb id, metadata will match on name",
                path);
        }

        private string? ReadFile(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "VidKing: cannot read {Path}", path);
                return null;
            }
        }
    }
}