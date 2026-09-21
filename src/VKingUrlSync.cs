using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VidKing
{
    /// <summary>
    /// Keeps every resolved .vking item pointing at the URL the current configuration
    /// builds. ShortcutPath is written once, when the file is first scanned, and a rescan
    /// will not revisit a file whose path has not changed - so without this, editing a
    /// base URL in the plugin config would only ever reach files added afterwards.
    /// Runs over the whole library whenever the configuration is saved.
    /// </summary>
    public sealed class VKingUrlSync : IHostedService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<VKingUrlSync> _logger;

        // One sweep at a time; a second save while one is running is pointless, because
        // the running sweep reads the configuration fresh for every item anyway.
        private readonly SemaphoreSlim _sweeping = new(1, 1);
        private readonly VKingStreamExtractor _extractor;

        public VKingUrlSync(
            ILibraryManager libraryManager,
            ILogger<VKingUrlSync> logger,
            VKingStreamExtractor extractor)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _extractor = extractor;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (Plugin.Instance is not null)
            {
                Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (Plugin.Instance is not null)
            {
                Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// URL this item resolves to under the configuration as it stands, written back
        /// when it differs from what is stored. Reading the .vking file again costs a few
        /// bytes and is what makes a configuration change visible without a rescan.
        /// </summary>
        public async Task<string> CurrentUrlAsync(Video item, CancellationToken cancellationToken)
        {
            // A .mp4/.m3u8 ShortcutPath is a real extracted direct link (Tier 1) -
            // strictly better than anything a base-URL config change could produce, since
            // the configured base only ever builds an iframe fallback page. Never rebuild
            // over one, or a config edit (or this same sync running again) would keep
            // throwing away successful extractions for no gain.
            if (item.ShortcutPath is not null
                && (item.ShortcutPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    || item.ShortcutPath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)))
            {
                return item.ShortcutPath;
            }

            string contents;
            try
            {
                contents = await File.ReadAllTextAsync(item.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "VidKing: cannot re-read {Path}, keeping the stored URL", item.Path);
                return item.ShortcutPath ?? string.Empty;
            }

            var config = Plugin.Instance?.Configuration;
            var episode = item as Episode;
            var url = VKingUrl.Resolve(
                VKingUrl.Parse(contents),
                config?.MovieBaseUrl ?? string.Empty,
                config?.TvBaseUrl ?? string.Empty,
                episode?.ParentIndexNumber,
                episode?.IndexNumber);

            if (url is null || string.Equals(url, item.ShortcutPath, StringComparison.Ordinal))
            {
                return item.ShortcutPath ?? string.Empty;
            }

            _logger.LogInformation(
                "VidKing: {Name} now points at {Url} (was {Old})",
                item.Name,
                url,
                item.ShortcutPath);

            item.ShortcutPath = url;
            await _libraryManager
                .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);

            return url;
        }

        /// <summary>
        /// Re-points every .vking item in the library. Returns how many actually moved.
        /// </summary>
        public async Task<int> SweepAsync(CancellationToken cancellationToken)
        {
            // ponytail: no query filters on file extension, so this asks for every movie
            // and episode and picks ours out by path. It runs when an admin saves the
            // config page, not on any hot path.
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true
            });

            var moved = 0;
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (item is not Video video
                    || !VKingUrl.IsVirtualFile(video.Path)
                    || string.IsNullOrEmpty(video.ShortcutPath))
                {
                    continue;
                }

                var before = video.ShortcutPath;
                var after = await CurrentUrlAsync(video, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(before, after, StringComparison.Ordinal))
                {
                    moved++;
                }
            }

            return moved;
        }

        private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
        {
            // Clear the extractor cache so re-scanned items pick up the new extraction
            // setting (enabled/disabled) and base URL.
            if (e is PluginConfiguration cfg)
            {
                _extractor.ClearCache();
                _logger.LogInformation(
                    "VidKing: configuration changed, extraction cache cleared (EnableStreamExtraction={Enabled})",
                    cfg.EnableStreamExtraction);
            }

            // The config page is waiting on this save; sweep behind it.
            _ = Task.Run(async () =>
            {
                if (!await _sweeping.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
                {
                    _logger.LogInformation("VidKing: a URL sweep is already running, skipping this one");
                    return;
                }

                try
                {
                    var moved = await SweepAsync(CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("VidKing: configuration saved, {Count} item(s) re-pointed", moved);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "VidKing: URL sweep failed");
                }
                finally
                {
                    _sweeping.Release();
                }
            });
        }
    }
}
