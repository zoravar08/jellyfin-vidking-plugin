using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VidKing
{
    /// <summary>
    /// Support API for the injected web-client script and for native playback.
    ///
    /// Three-tier playback:
    ///   Tier 1 (Option B): when ShortcutPath is a real MP4 URL (extraction succeeded),
    ///     Jellyfin's native player handles it — server-side ffmpeg, apps, and web
    ///     (Jellyfin proxies the remote URL so CORS is not an issue on the client).
    ///   Tier 2 (Option A): /VidKing/Stream/{itemId} is a CORS-proxy for web clients that
    ///     need to fetch the MP4 directly (rare — only when Jellyfin's built-in remote
    ///     streaming does not apply). Most clients never hit this.
    ///   Tier 3 (existing): /VidKing/Embed/{itemId} returns the HTML embed URL for the
    ///     iframe overlay when ShortcutPath points at a player page.
    /// </summary>
    [ApiController]
    [Route("VidKing")]
    public class VKingController : ControllerBase
    {
        // Honey yellow, the accent this server's own web UI uses for focus rings.
        private const string PlayerColor = "f6b23a";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

        // Appended to every embed URL. Both spellings of autoplay are sent because
        // vidking reads autoPlay and vidsrc reads autoplay, and each ignores the other.
        // autonext is deliberately absent: the player would advance to the next episode
        // inside the iframe while progress kept being reported against this one.
        private static readonly (string Key, string Value)[] PlayerOptions =
        {
            ("color", PlayerColor),
            ("autoPlay", "true"),
            ("autoplay", "true"),
            ("sub", "en"),
            ("controls", "0")
        };

        // ponytail: process-lifetime memo, no expiry - a URL that serves a player page
        // today is not going to start serving MP4 bytes this week. Restart clears it.
        private static readonly ConcurrentDictionary<string, bool> _isEmbedPage = new();

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly VKingUrlSync _urlSync;
        private readonly VKingStreamExtractor _extractor;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<VKingController> _logger;

        public VKingController(
            ILibraryManager libraryManager,
            IUserManager userManager,
            VKingUrlSync urlSync,
            VKingStreamExtractor extractor,
            IHttpClientFactory httpClientFactory,
            ILogger<VKingController> logger)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _urlSync = urlSync;
            _extractor = extractor;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Playback info for a .vking item. The client script calls this to decide how to
        /// play: iframe overlay (embed page) or normal Jellyfin playback (real media).
        ///
        /// When extraction succeeded, ShortcutPath is a real MP4 URL — the server returns
        /// Mode=Stream so the client lets Jellyfin's native player handle it. When the URL
        /// is an HTML embed page, Mode=Iframe and the client opens the overlay.
        /// </summary>
        [HttpGet("Info/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VKingPlaybackInfo>> GetInfo(
            [FromRoute] Guid itemId,
            CancellationToken cancellationToken)
        {
            var requested = _libraryManager.GetItemById(itemId);
            var item = requested as Video ?? NextEpisode(requested);
            if (item is null || !VKingUrl.IsVirtualFile(item.Path) || string.IsNullOrEmpty(item.ShortcutPath))
            {
                return NotFound();
            }

            var url = await _urlSync.CurrentUrlAsync(item, cancellationToken).ConfigureAwait(false);

            // Determine if this is a real media URL or an embed page.
            var isEmbed = await IsEmbedPageAsync(url, cancellationToken).ConfigureAwait(false);

            if (isEmbed)
            {
                return new VKingPlaybackInfo
                {
                    Mode = PlaybackMode.Iframe,
                    Url = WithPlayerOptions(url),
                    ItemId = item.Id
                };
            }

            // Real media (MP4 from 1Embed or similar). Jellyfin's native player handles it.
            // The client lets normal playback proceed — no iframe needed.
            _logger.LogInformation(
                "VidKing: {Name} ({Id}) is real media at {Url} — native playback (Tier 1)",
                item.Name, item.Id, url);

            return new VKingPlaybackInfo
            {
                Mode = PlaybackMode.Stream,
                Url = url,
                ItemId = item.Id
            };
        }

        /// <summary>
        /// URL to open in an iframe, but only for a .vking item whose target is an HTML
        /// player page. A target that is real media 404s, so the client leaves it to
        /// Jellyfin's own player - which handles it exactly like a .strm.
        /// </summary>
        [HttpGet("Embed/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<VKingEmbedInfo>> GetEmbed(
            [FromRoute] Guid itemId,
            CancellationToken cancellationToken)
        {
            var requested = _libraryManager.GetItemById(itemId);
            var item = requested as Video ?? NextEpisode(requested);
            if (item is null || !VKingUrl.IsVirtualFile(item.Path) || string.IsNullOrEmpty(item.ShortcutPath))
            {
                return NotFound();
            }

            var url = await _urlSync.CurrentUrlAsync(item, cancellationToken).ConfigureAwait(false);
            if (!await IsEmbedPageAsync(url, cancellationToken).ConfigureAwait(false))
            {
                return NotFound();
            }

            // ItemId, not itemId: Play on a series poster asks about the folder, and
            // playback has to be reported against the episode that actually plays.
            return new VKingEmbedInfo { Url = WithPlayerOptions(url), ItemId = item.Id };
        }

        /// <summary>
        /// Duration reported by the embed page's own player. Nothing probes an HTML page,
        /// so that player is the only place this item's runtime exists - and with no
        /// runtime Jellyfin draws no progress bar and never auto-marks the item watched.
        /// </summary>
        [HttpPost("Runtime/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> SetRuntime(
            [FromRoute] Guid itemId,
            [FromQuery] double seconds,
            CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId) as Video;
            if (item is null || !VKingUrl.IsVirtualFile(item.Path))
            {
                return NotFound();
            }

            var runtime = ToTicks(seconds);
            if (runtime is null || item.RunTimeTicks == runtime)
            {
                return NoContent();
            }

            item.RunTimeTicks = runtime;
            await _libraryManager
                .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("VidKing: {Name} runtime {Seconds}s from the embed player", item.Name, seconds);
            return NoContent();
        }

        /// <summary>
        /// Streaming proxy for web clients that cannot play a cross-origin MP4 directly.
        /// Relays bytes from the real MP4 URL with CORS headers so the browser's video
        /// element can load it. This is Tier 2 — most clients use Jellyfin's built-in
        /// remote streaming (Tier 1) and never hit this endpoint.
        ///
        /// Usage: the client fetches this URL and hands it to the video element. The proxy
        /// forwards Range headers so seeking works.
        /// </summary>
        [HttpGet("Stream/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task Stream(
            [FromRoute] Guid itemId,
            CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId) as Video;
            if (item is null || !VKingUrl.IsVirtualFile(item.Path) || string.IsNullOrEmpty(item.ShortcutPath))
            {
                Response.StatusCode = 404;
                return;
            }

            var url = item.ShortcutPath;

            // Only proxy if the URL looks like a remote stream (not a local file).
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Response.StatusCode = 404;
                return;
            }

            // Parse Range header manually: "bytes=START-END" or "bytes=START-"
            var rangeHeader = Request.Headers.ContainsKey("Range")
                ? Request.Headers["Range"].ToString()
                : null;

            long start = 0;
            long? requestedEnd = null;
            bool hasRange = false;

            if (rangeHeader is not null && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                hasRange = true;
                var rangeBody = rangeHeader["bytes".Length..].Trim();
                var dashPos = rangeBody.IndexOf('-');
                if (dashPos >= 0)
                {
                    var startStr = rangeBody[..dashPos];
                    if (long.TryParse(startStr, out var s))
                    {
                        start = s;
                    }

                    var endStr = rangeBody[(dashPos + 1)..];
                    if (long.TryParse(endStr, out var e))
                    {
                        requestedEnd = e;
                    }
                }
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                if (hasRange)
                {
                    // Cap at 10 MB per chunk so one request can't pull the whole file, but
                    // honor a smaller client-requested end instead of always forcing 10 MB -
                    // a client asking for a small range (e.g. a moov-atom probe) should get
                    // back what it asked for, not 10 MB it will discard.
                    var maxChunkEnd = start + 10L * 1024 * 1024;
                    var chunkEnd = requestedEnd.HasValue ? Math.Min(requestedEnd.Value, maxChunkEnd) : maxChunkEnd;
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, chunkEnd);
                }

                using var response = await _httpClientFactory
                    .CreateClient(NamedClient.Default)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Response.StatusCode = 502;
                    return;
                }

                // Mirror the upstream content type.
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "video/mp4";
                Response.ContentType = contentType;

                // Mirror content length if available.
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > 0)
                {
                    Response.Headers.ContentLength = contentLength.Value;
                }

                // CORS headers for web clients.
                Response.Headers.Append("Access-Control-Allow-Origin", "*");
                Response.Headers.Append("Access-Control-Expose-Headers", "Content-Length, Content-Range, Accept-Ranges");

                // Check if upstream supports range requests via Content-Range in 206 responses.
                if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                {
                    var contentRange = response.Content.Headers.ContentRange;
                    if (contentRange is not null)
                    {
                        Response.StatusCode = 206;
                        Response.Headers.ContentRange = contentRange.ToString();
                    }
                    else
                    {
                        Response.StatusCode = 200;
                    }
                }
                else
                {
                    Response.StatusCode = 200;
                }

                // Stream the bytes to the response.
                using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await responseStream.CopyToAsync(Response.Body, 64 * 1024, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VidKing: stream proxy failed for {Url}", url);
                Response.StatusCode = 502;
            }
        }

        /// <summary>
        /// The browser-side script itself, so the JavaScript Injector entry stays a
        /// one-line loader and updates ship with the plugin.
        /// </summary>
        [HttpGet("ClientScript.js")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetClientScript()
        {
            var stream = GetType().Assembly
                .GetManifestResourceStream("Jellyfin.Plugin.VidKing.Configuration.clientScript.js");

            return stream is null ? NotFound() : File(stream, "application/javascript");
        }

        /// <summary>
        /// Triggers on-demand stream extraction for a .vking item. When extraction is
        /// enabled but the scanner did not extract (e.g. the item was added before
        /// extraction was turned on), the client can call this to extract the real MP4
        /// URL. Returns { ok: true } when extraction succeeds, { ok: false } otherwise.
        /// On success the item's ShortcutPath is updated to the real URL.
        /// </summary>
        [HttpPost("Extract/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ExtractResult>> Extract(
            [FromRoute] Guid itemId,
            CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId) as Video;
            if (item is null || !VKingUrl.IsVirtualFile(item.Path))
            {
                return NotFound();
            }

            // Already has a real MP4/HLS link — leave it alone. Unlike ResolveMovie /
            // ResolveEpisode, this endpoint had no such guard at all: any client call
            // (e.g. a stale client retrying Extract) would unconditionally re-run the
            // extractor and overwrite a perfectly working ShortcutPath with whatever the
            // next extraction happened to return.
            if (item.ShortcutPath is not null && VKingResolver.IsValidMediaUrl(item.ShortcutPath))
            {
                _logger.LogInformation(
                    "VidKing: on-demand extraction skipped for {Name} ({Id}) — already has a working link {Url}",
                    item.Name, item.Id, item.ShortcutPath);
                return new ExtractResult { Ok = true, Url = item.ShortcutPath };
            }

            var config = Plugin.Instance?.Configuration;
            if (config?.EnableStreamExtraction != true)
            {
                return new ExtractResult { Ok = false, Error = "Extraction disabled" };
            }

            var contents = await System.IO.File.ReadAllTextAsync(item.Path, cancellationToken).ConfigureAwait(false);
            var target = VKingUrl.Parse(contents);
            var id = target?.Id;

            if (string.IsNullOrWhiteSpace(id))
            {
                return new ExtractResult { Ok = false, Error = "No id in .vking file" };
            }

            var isMovie = item is Movie;
            var configuredBase = isMovie
                ? (config?.MovieBaseUrl ?? string.Empty)
                : (config?.TvBaseUrl ?? string.Empty);

            // season/episode were hardcoded null here, so every on-demand extraction
            // silently defaulted to S1E1 in the Python script regardless of which episode
            // was actually asked for (caught live: extracting Zero Day S01E02 returned
            // S01E01's stream). Jellyfin already resolved this episode's real
            // season/episode onto the item itself, so read it from there — falling back
            // to the .vking target's own season/episode only if that's somehow unset.
            var episodeItem = item as Episode;
            var season = episodeItem?.ParentIndexNumber ?? target?.Season;
            var episode = episodeItem?.IndexNumber ?? target?.Episode;

            // No base-URL gate here either: the configured base is only ever the Tier 3
            // iframe fallback now. Extraction always tries the known-good sources first.
            var candidates = VKingResolver.ExtractionCandidates(configuredBase);
            var result = await _extractor.ExtractAsync(id, isMovie, season, episode, candidates, cancellationToken).ConfigureAwait(false);

            if (result?.Mp4Url is not null)
            {
                item.ShortcutPath = result.Mp4Url;
                await _libraryManager
                    .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "VidKing: on-demand extraction succeeded for {Name} ({Id}): {Url}",
                    item.Name, item.Id, result.Mp4Url);

                return new ExtractResult { Ok = true, Url = result.Mp4Url };
            }

            return new ExtractResult { Ok = false, Error = "Extraction returned no URL" };
        }

        /// <summary>Episode a Play click on a Series or Season poster should land on: first</summary>
        private Episode? NextEpisode(BaseItem? folder)
        {
            if (folder is not (Series or Season))
            {
                return null;
            }

            var claim = HttpContext.User.FindFirst("Jellyfin-UserId")?.Value;
            var user = Guid.TryParse(claim, out var userId) && !userId.Equals(default)
                ? _userManager.GetUserById(userId)
                : null;

            return FirstEpisode(folder, user, unwatchedOnly: user is not null)
                ?? FirstEpisode(folder, user, unwatchedOnly: false);
        }

        private Episode? FirstEpisode(BaseItem folder, User? user, bool unwatchedOnly)
        {
            var query = new InternalItemsQuery(user)
            {
                AncestorIds = new[] { folder.Id },
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                OrderBy = new[]
                {
                    (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                    (ItemSortBy.IndexNumber, SortOrder.Ascending)
                },
                IsVirtualItem = false,
                IsPlayed = unwatchedOnly ? false : null,
                Recursive = true,
                Limit = 1
            };

            return _libraryManager.GetItemList(query).OfType<Episode>().FirstOrDefault();
        }

        /// <summary>
        /// True when the URL answers with a web page rather than media. Asks for the first
        /// byte only, so nothing is downloaded; a request that fails outright counts as a
        /// page, because ffmpeg would not have got anywhere either and the site's own
        /// player at least shows the user why.
        /// </summary>
        private async Task<bool> IsEmbedPageAsync(string url, CancellationToken cancellationToken)
        {
            if (_isEmbedPage.TryGetValue(url, out var known))
            {
                return known;
            }

            bool isPage;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);

                using var response = await _httpClientFactory
                    .CreateClient(NamedClient.Default)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);

                isPage = !response.IsSuccessStatusCode
                    || IsPageContentType(response.Content.Headers.ContentType?.MediaType);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "VidKing: {Url} unreachable, treating as embed page", url);
                isPage = true;
            }

            _isEmbedPage[url] = isPage;
            _logger.LogInformation("VidKing: {Url} is {Kind}", url, isPage ? "an embed page" : "playable media");
            return isPage;
        }

        /// <summary>
        /// Themes the embedded player, starts it without a second click, asks for English
        /// subtitles and hides the player's own chrome.
        /// </summary>
        internal static string WithPlayerOptions(string url)
        {
            var result = url;
            foreach (var (key, value) in PlayerOptions)
            {
                if (HasParameter(result, key))
                {
                    continue;
                }

                result += (result.Contains('?', StringComparison.Ordinal) ? '&' : '?') + key + "=" + value;
            }

            return result;
        }

        /// <summary>True when the URL's query string already sets this exact key.</summary>
        internal static bool HasParameter(string url, string key)
        {
            var query = url.IndexOf('?', StringComparison.Ordinal);
            if (query < 0)
            {
                return false;
            }

            foreach (var pair in url[(query + 1)..].Split('&'))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                var name = equals < 0 ? pair : pair[..equals];
                if (string.Equals(name, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        internal static long? ToTicks(double seconds)
        {
            return double.IsFinite(seconds) && seconds > 0 && seconds <= 24 * 60 * 60
                ? (long)(seconds * TimeSpan.TicksPerSecond)
                : null;
        }

        internal static bool IsPageContentType(string? mediaType)
        {
            return mediaType is not null
                && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Reply body of <see cref="VKingController.GetEmbed"/>.</summary>
    public sealed class VKingEmbedInfo
    {
        public string Url { get; set; } = string.Empty;

        /// <summary>Item that actually plays - not the requested one when a folder was asked about.</summary>
        public Guid ItemId { get; set; }
    }

    /// <summary>Playback mode info returned by <see cref="VKingController.GetInfo"/>.</summary>
    public sealed class VKingPlaybackInfo
    {
        /// <summary>Whether to play via iframe overlay or let Jellyfin's native player handle it.</summary>
        public PlaybackMode Mode { get; set; }

        /// <summary>The URL to play: embed page URL for iframe mode, real media URL for stream mode.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>The item that actually plays.</summary>
        public Guid ItemId { get; set; }
    }

    public enum PlaybackMode
    {
        /// <summary>Use Jellyfin's native player (Tier 1) or the stream proxy (Tier 2).</summary>
        Stream = 0,

        /// <summary>Open the embed page in an iframe overlay (Tier 3).</summary>
        Iframe = 1
    }

    /// <summary>Reply body of <see cref="VKingController.Extract"/>.</summary>
    public sealed class ExtractResult
    {
        public bool Ok { get; set; }
        public string? Url { get; set; }
        public string? Error { get; set; }
    }
}