using System;
using System.Diagnostics;
using Jellyfin.Plugin.VidKing;
using Emby.Naming.Common;
using Emby.Naming.TV;
using MediaBrowser.Model.Entities;

// Smallest thing that fails if the .vking -> URL logic breaks.
// Run: dotnet run --project SelfCheck -c Debug
internal static class Program
{
    private static void Main()
    {
        const string Base = "https://vidking.example";

        Debug.Assert(
            Plugin.ShortcutContainer == "strm",
            ".vking shortcut items must use Jellyfin's .strm input behavior");

        // --- movies: bare id, no season/episode in scope
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("12345"), Base, Base, null, null) == Base + "/embed/movie/12345",
            "bare id -> movie");
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("12345\n"), Base + "/", Base + "/", null, null) == Base + "/embed/movie/12345",
            "trailing slash must not double up");
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("  603  "), Base, Base, null, null) == Base + "/embed/movie/603",
            "whitespace trimmed");
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("a b"), Base, Base, null, null) == Base + "/embed/movie/a%20b",
            "id escaped");

        // --- tv: season/episode from the filename parse
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("1396"), Base, Base, 2, 5) == Base + "/embed/tv/1396/2/5",
            "id + filename S/E -> episode");

        // --- tv: "id/season/episode" in the file overrides the filename
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("1396/3/7"), Base, Base, 2, 5) == Base + "/embed/tv/1396/3/7",
            "file overrides filename");
        var explicitTarget = VKingUrl.Parse("1396/3/7");
        Debug.Assert(explicitTarget!.Season == 3 && explicitTarget.Episode == 7, "override parsed");

        // Half an override is not an override
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("1396/3"), Base, Base, 2, 5) == Base + "/embed/tv/1396/2/5",
            "incomplete override falls back to filename");

        // Only one of season/episode known -> movie form, never a malformed tv URL
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("1396"), Base, Base, 2, null) == Base + "/embed/movie/1396",
            "season without episode");

        // --- explicit URL passes through untouched, in both library types
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("https://host.test/x.mp4"), Base, Base, 2, 5) == "https://host.test/x.mp4",
            "explicit URL wins");

        // --- nothing playable -> no item
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("# note\n\n999"), Base, Base, null, null) == Base + "/embed/movie/999",
            "comments skipped");
        Debug.Assert(VKingUrl.Parse("   \n\n") is null, "blank file -> null");
        Debug.Assert(VKingUrl.Parse(null) is null, "null -> null");
        Debug.Assert(VKingUrl.Resolve(null, Base, Base, null, null) is null, "null target -> null");
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("12345"), string.Empty, string.Empty, null, null) is null,
            "no base url -> null");

        // --- the two base URLs are independent
        const string MovieBase = "https://movies.test";
        const string TvBase = "https://shows.test";
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("27205"), MovieBase, TvBase, null, null)
                == MovieBase + "/embed/movie/27205",
            "movie form uses the movie base");
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("1396"), MovieBase, TvBase, 1, 2)
                == TvBase + "/embed/tv/1396/1/2",
            "episode form uses the tv base");
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("1396"), MovieBase, string.Empty, 1, 2) is null,
            "blank tv base disables episodes, movie base does not cover for it");
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("27205"), string.Empty, TvBase, null, null) is null,
            "blank movie base disables movies");
        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("https://host.test/x.mp4"), string.Empty, string.Empty, 1, 2)
                == "https://host.test/x.mp4",
            "explicit URL works with no bases configured");

        // --- provider id detection (drives metadata lookup by id, not filename)
        Debug.Assert(
            VKingUrl.TryGetProvider("603", out var p1) && p1 == MetadataProvider.Tmdb,
            "numeric id -> TMDb");
        Debug.Assert(
            VKingUrl.TryGetProvider("tt0133093", out var p2) && p2 == MetadataProvider.Imdb,
            "tt id -> IMDb");
        Debug.Assert(!VKingUrl.TryGetProvider("tt", out _), "bare tt is not an id");
        Debug.Assert(!VKingUrl.TryGetProvider("ttabc", out _), "tt + letters is not an id");
        Debug.Assert(!VKingUrl.TryGetProvider("some-slug", out _), "slug -> name matching");
        Debug.Assert(!VKingUrl.TryGetProvider(null, out _), "null -> no provider");
        Debug.Assert(!VKingUrl.TryGetProvider("  ", out _), "blank -> no provider");

        // --- season/episode parsing: Emby.Naming ignores the .vking extension, so the
        // parser must be handed a known video extension. This is what broke S/E once.
        var naming = new NamingOptions();
        var raw = new EpisodeResolver(naming)
            .Resolve("/x/01/Breaking Bad - S01E02.vking", false, null, null, null, true);
        Debug.Assert(raw is null, "parser refuses .vking - do not call it with the real path");

        var viaParserPath = new EpisodeResolver(naming)
            .Resolve(VKingUrl.ParserPath("/x/01/Breaking Bad - S01E02.vking"), false, null, null, null, true);
        Debug.Assert(
            viaParserPath?.SeasonNumber == 1 && viaParserPath?.EpisodeNumber == 2,
            "ParserPath must recover S01E02");

        Debug.Assert(
            VKingUrl.Resolve(VKingUrl.Parse("1396"), Base, Base, viaParserPath?.SeasonNumber, viaParserPath?.EpisodeNumber)
                == Base + "/embed/tv/1396/1/2",
            "parsed numbers must produce the tv URL, not the movie URL");

        // --- extension matching
        Debug.Assert(VKingUrl.IsVirtualFile("/m/Matrix.vking"), "lower case ext");
        Debug.Assert(VKingUrl.IsVirtualFile("/m/Matrix.VKING"), "upper case ext");
        Debug.Assert(!VKingUrl.IsVirtualFile("/m/Matrix.mkv"), "real video untouched");
        Debug.Assert(!VKingUrl.IsVirtualFile("/m/vking"), "no extension");

        // --- which targets go to the iframe instead of Jellyfin's own player
        Debug.Assert(VKingController.IsPageContentType("text/html"), "embed page -> iframe");
        Debug.Assert(VKingController.IsPageContentType("TEXT/HTML; charset=utf-8"), "case/params ignored");
        Debug.Assert(!VKingController.IsPageContentType("video/mp4"), "real media -> native player");
        Debug.Assert(!VKingController.IsPageContentType("application/vnd.apple.mpegurl"), "hls -> native player");
        Debug.Assert(!VKingController.IsPageContentType(null), "unknown type -> native player first");

        // --- player options appended to the embed URL
        const string Options = "color=f6b23a&autoPlay=true&autoplay=true&sub=en&controls=0";
        Debug.Assert(
            VKingController.WithPlayerOptions("https://v.test/embed/movie/1")
                == "https://v.test/embed/movie/1?" + Options,
            "colour, autoplay, subtitles and hidden controls appended");
        Debug.Assert(
            VKingController.WithPlayerOptions("https://v.test/embed/movie/1?x=1")
                == "https://v.test/embed/movie/1?x=1&" + Options,
            "existing query keeps its ?");
        Debug.Assert(
            VKingController.WithPlayerOptions("https://v.test/embed/movie/1?color=9146ff")
                == "https://v.test/embed/movie/1?color=9146ff&autoPlay=true&autoplay=true&sub=en&controls=0",
            "a colour set in the .vking file wins, the rest is still added");
        Debug.Assert(
            VKingController.WithPlayerOptions("https://v.test/embed/movie/1?controls=1")
                == "https://v.test/embed/movie/1?controls=1&color=f6b23a&autoPlay=true&autoplay=true&sub=en",
            "controls set in the file wins too");
        Debug.Assert(
            VKingController.WithPlayerOptions(VKingController.WithPlayerOptions("https://v.test/embed/movie/1"))
                == "https://v.test/embed/movie/1?" + Options,
            "applying twice changes nothing");

        // --- query keys match exactly: two sites, two spellings of autoplay
        Debug.Assert(VKingController.HasParameter("https://v.test/x?sub=en", "sub"), "key found");
        Debug.Assert(!VKingController.HasParameter("https://v.test/x?sub=en", "s"), "prefix is not a key");
        Debug.Assert(!VKingController.HasParameter("https://v.test/x?subtitle=en", "sub"), "longer key is not a match");
        Debug.Assert(!VKingController.HasParameter("https://v.test/x?autoPlay=true", "autoplay"), "case matters");
        Debug.Assert(!VKingController.HasParameter("https://v.test/sub/1", "sub"), "path is not a query");
        Debug.Assert(VKingController.HasParameter("https://v.test/x?a=1&sub=en", "sub"), "second key found");

        // --- runtime posted by the embed player
        Debug.Assert(VKingController.ToTicks(90) == 900000000L, "90s -> ticks");
        Debug.Assert(VKingController.ToTicks(0) is null, "zero is not a runtime");
        Debug.Assert(VKingController.ToTicks(-5) is null, "negative rejected");
        Debug.Assert(VKingController.ToTicks(double.NaN) is null, "NaN rejected");
        Debug.Assert(VKingController.ToTicks(90000) is null, "25h rejected");

        Console.WriteLine("SelfCheck OK");
    }
}
