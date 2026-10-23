using System.Globalization;
using Linkarr.Api.Apps;
using Linkarr.Api.Data;

namespace Linkarr.Api.Catalog;

// Merges modularr's library and queue with visularr's items into one catalog with a state per title.
public static class CatalogBuilder
{
    public static IReadOnlyList<CatalogItem> Build(
        IReadOnlyList<ArrMovie> movies, IReadOnlyList<ArrSeries> series, IReadOnlyList<ArrQueueItem> queue, IReadOnlyList<JellyfinItem> items)
    {
        ArgumentNullException.ThrowIfNull(movies);
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(items);

        Dictionary<int, double> movieProgress = Progress(queue.Where(q => q.MovieId is not null).GroupBy(q => q.MovieId!.Value));
        Dictionary<int, double> seriesProgress = Progress(queue.Where(q => q.SeriesId is not null).GroupBy(q => q.SeriesId!.Value));
        List<JellyfinItem> unmatched = [.. items];
        List<CatalogItem> catalog = [];

        foreach (ArrMovie movie in movies)
        {
            JellyfinItem? item = Take(unmatched, "Movie", "Tmdb", movie.TmdbId, movie.Path);
            catalog.Add(Merge(new CatalogItem
            {
                Key = Key(TitleKind.Movie, movie.TmdbId), Kind = TitleKind.Movie, Title = movie.Title, Year = movie.Year, TmdbId = movie.TmdbId,
                Monitored = movie.Monitored, HasFile = movie.HasFile, ModularrId = movie.Id, ModularrPath = movie.Path, Poster = ArrImages.Poster(movie.Images),
            }, item, movieProgress.TryGetValue(movie.Id, out double progress) ? progress : null));
        }

        foreach (ArrSeries show in series)
        {
            JellyfinItem? item = Take(unmatched, "Series", "Tvdb", show.TvdbId, show.Path);
            catalog.Add(Merge(new CatalogItem
            {
                Key = Key(TitleKind.Series, show.TvdbId), Kind = TitleKind.Series, Title = show.Title, Year = show.Year, TvdbId = show.TvdbId,
                Monitored = show.Monitored, HasFile = show.Statistics?.EpisodeFileCount > 0, ModularrId = show.Id, ModularrPath = show.Path,
                Poster = ArrImages.Poster(show.Images),
            }, item, seriesProgress.TryGetValue(show.Id, out double progress) ? progress : null));
        }

        // In visularr only: modularr does not track them.
        foreach (JellyfinItem item in unmatched)
        {
            TitleKind kind = item.Type == "Movie" ? TitleKind.Movie : TitleKind.Series;
            int? tmdbId = ProviderId(item, "Tmdb");
            int? tvdbId = ProviderId(item, "Tvdb");
            int? id = kind == TitleKind.Movie ? tmdbId : tvdbId;
            catalog.Add(new CatalogItem
            {
                Key = id is int known ? Key(kind, known) : "visularr:" + item.Id, Kind = kind, Title = item.Name, Year = item.ProductionYear ?? 0,
                TmdbId = tmdbId, TvdbId = tvdbId, VisularrId = item.Id, State = CatalogState.Untracked, HasFile = true,
            });
        }

        // Two visularr items of one title (two versions) would share a key: keep the first.
        return [.. catalog.DistinctBy(c => c.Key).OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)];
    }

    public static string Key(TitleKind kind, int id) =>
        kind == TitleKind.Movie ? string.Create(CultureInfo.InvariantCulture, $"movie:tmdb:{id}") : string.Create(CultureInfo.InvariantCulture, $"series:tvdb:{id}");

    // Visularr's copy wins once it is there; until then the queue, then the file, then the monitored flag tell where the title stands.
    public static CatalogState StateOf(bool inVisularr, bool downloading, bool hasFile, bool monitored) =>
        inVisularr ? CatalogState.Available
        : downloading ? CatalogState.Downloading
        : hasFile ? CatalogState.NotIndexed
        : monitored ? CatalogState.Missing
        : CatalogState.Unmonitored;

    private static CatalogItem Merge(CatalogItem item, JellyfinItem? visularr, double? progress)
    {
        item.VisularrId = visularr?.Id;
        item.Progress = progress;
        item.State = StateOf(visularr is not null, progress is not null, item.HasFile, item.Monitored);
        return item;
    }

    // The visularr item of a modularr title: by provider id, else by path (Jellyfin's movie path is the file inside modularr's folder).
    // shortcut: a linear scan per title (titles x items), fine for a few thousand titles; index by provider id if libraries grow past that.
    private static JellyfinItem? Take(List<JellyfinItem> items, string type, string provider, int id, string path)
    {
        string folder = path.TrimEnd('/', '\\');
        JellyfinItem? match = items.FirstOrDefault(i => i.Type == type && ProviderId(i, provider) == id)
            ?? items.FirstOrDefault(i => i.Type == type && i.Path is string p && folder.Length > 0
                && (string.Equals(p.TrimEnd('/', '\\'), folder, StringComparison.Ordinal) || p.StartsWith(folder + "/", StringComparison.Ordinal)));
        if (match is not null)
        {
            items.Remove(match);
        }

        return match;
    }

    private static int? ProviderId(JellyfinItem item, string provider) =>
        item.ProviderIds?.FirstOrDefault(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase)).Value is string value
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null;

    // One title can have several downloads (season packs): their bytes together.
    private static Dictionary<int, double> Progress(IEnumerable<IGrouping<int, ArrQueueItem>> groups) => groups.ToDictionary(
        g => g.Key,
        g => g.Sum(q => q.Size) is long size and > 0 ? Math.Round((double)(size - g.Sum(q => q.Sizeleft)) / size, 3) : 0);
}
