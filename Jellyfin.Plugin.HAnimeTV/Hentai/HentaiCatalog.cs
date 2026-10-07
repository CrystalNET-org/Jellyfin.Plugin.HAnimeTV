using Jellyfin.Plugin.HAnimeTV.Configuration;
using Jellyfin.Plugin.HAnimeTV.Hanime;
using Jellyfin.Plugin.HAnimeTV.HentaiHaven;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HAnimeTV.Hentai
{
    /// <summary>
    /// A source's state, for the settings page.
    /// </summary>
    public sealed record HentaiSourceStatus(string Name, bool Enabled, int? Count, DateTimeOffset? Time, string? Error);

    /// <summary>
    /// The catalogs of hanime.tv and Hentai Haven, merged: an episode both have (same series
    /// and number) is listed once, from hanime.tv.
    /// </summary>
    public sealed class HentaiCatalog
    {
        private readonly HanimeClient _hanime;
        private readonly HentaiHavenClient _hentaiHaven;
        private readonly ILogger<HentaiCatalog> _logger;

        public HentaiCatalog(HanimeClient hanime, HentaiHavenClient hentaiHaven, ILogger<HentaiCatalog> logger)
        {
            _hanime = hanime;
            _hentaiHaven = hentaiHaven;
            _logger = logger;
        }

        public IReadOnlyList<HentaiSourceStatus> Status(HentaiSettings config) =>
        [
            new("hanime.tv", config.HanimeEnabled, _hanime.CatalogCount, _hanime.CatalogTime, _hanime.CatalogError),
            new("Hentai Haven", config.HentaiHavenEnabled, _hentaiHaven.CatalogCount, _hentaiHaven.CatalogTime, _hentaiHaven.CatalogError),
        ];

        /// <summary>
        /// Gets the merged catalog of the enabled sources.
        /// </summary>
        /// <remarks>
        /// A source that fails is left out if it never worked, so the library keeps what the
        /// other has; it then has nothing in the library to lose. A source that worked before
        /// gives its last catalog instead (see the clients).
        /// </remarks>
        /// <exception cref="InvalidOperationException">No source is enabled, or none could be read.</exception>
        public async Task<IReadOnlyList<HentaiVideo>> GetAsync(HentaiSettings config, CancellationToken cancellationToken)
        {
            var catalogs = new List<IReadOnlyList<HentaiVideo>>();
            var errors = new List<string>();
            if (config.HanimeEnabled)
            {
                try
                {
                    catalogs.Add(await _hanime.GetCatalogAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (HanimeException ex)
                {
                    // hanime.tv's catalog is kept in memory only: after a restart, leaving it out
                    // would delete its episodes from the library
                    throw new InvalidOperationException("hanime.tv: " + ex.Message, ex);
                }
            }

            if (config.HentaiHavenEnabled)
            {
                try
                {
                    catalogs.Add(await _hentaiHaven.GetCatalogAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (HentaiHavenException ex)
                {
                    _logger.LogWarning("Hentai Haven: left out: {Error}", ex.Message);
                    errors.Add("Hentai Haven: " + ex.Message);
                }
            }

            if (catalogs.Count == 0)
            {
                throw new InvalidOperationException(errors.Count > 0 ? string.Join("; ", errors) : "No source is enabled");
            }

            return Merge(catalogs);
        }

        /// <summary>
        /// Merges catalogs, the preferred first: an episode of a series that an earlier catalog
        /// has too is left out. Series are compared by name without case, spaces and punctuation.
        /// </summary>
        public static IReadOnlyList<HentaiVideo> Merge(IEnumerable<IReadOnlyList<HentaiVideo>> catalogs)
        {
            var result = new List<HentaiVideo>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var catalog in catalogs)
            {
                var added = new List<string>();
                foreach (var video in catalog)
                {
                    var key = video.SeriesKey() + "#" + video.SeriesInfo().Episode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (taken.Contains(key))
                    {
                        continue;
                    }

                    // Episodes of one source never exclude each other
                    added.Add(key);
                    result.Add(video);
                }

                taken.UnionWith(added);
            }

            return result;
        }
    }
}
