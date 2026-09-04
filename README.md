<h1 align="center">Jellyfin AniDB Plugin</h1>
<h3 align="center">Part of the <a href="https://jellyfin.org">Jellyfin Project</a></h3>

<p align="center">
<img alt="Plugin Banner" src="https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/plugins/SVG/jellyfin-plugin-anidb.svg?sanitize=true"/>
<br/>
<br/>
<a href="https://github.com/jellyfin/jellyfin-plugin-anidb/actions?query=workflow%3A%22Test+Build+Plugin%22">
<img alt="GitHub Workflow Status" src="https://img.shields.io/github/workflow/status/jellyfin/jellyfin-plugin-anidb/Test%20Build%20Plugin.svg">
</a>
<a href="https://github.com/jellyfin/jellyfin-plugin-anidb/blob/master/LICENSE">
<img alt="GPLv2 License" src="https://img.shields.io/github/license/jellyfin/jellyfin-plugin-anidb.svg"/>
</a>
<a href="https://github.com/jellyfin/jellyfin-plugin-anidb/releases">
<img alt="Current Release" src="https://img.shields.io/github/release/jellyfin/jellyfin-plugin-anidb.svg"/>
</a>
</p>

## About

This plugin makes [AniDB](https://anidb.net/) a metadata provider for anime in Jellyfin, for
shows, movies and the people who made them.

### What it provides

- Series, season, episode and movie metadata: titles in your choice of localized, Japanese or
  romaji, descriptions, air dates, runtimes and AniDB's own ratings.
- Genres and tags from AniDB's weighted tags, filtered by weight, by whether AniDB shows the tag
  in the infobox, and by a blacklist of your own. Anime AniDB flags as adult are rated so
  Jellyfin's parental controls act on them.
- Cast and crew - voice actors with the characters they play, directors, composers and writers -
  with the portraits AniDB holds for them.
- Posters for series, seasons and movies.
- Similar items, from the anime AniDB's own users hold to be alike, resolved against what your
  library actually holds.
- AniDB ids and links on series, seasons, episodes and people.

### Where the mappings come from

AniDB registers every season, OVA and film as an entry of its own, where a library holds one show
with numbered seasons. Bridging the two takes a mapping, and three sources are asked in order:

1. Your own [mapping overrides](docs/mapping-overrides.md), if you wrote any. The last word on
   whatever they name, and the only way to describe a library holding something AniDB does not
   list, or a show AniDB keeps inside another entry.
2. The [AniBridge mappings](https://github.com/anibridge/anibridge-mappings). They place more
   AniDB entries than the anime list, state both sides of every placement outright, and carry
   TMDB, TVDB and IMDb ids. Can be turned off in the settings.
3. The [Anime-Lists](https://github.com/Anime-Lists/anime-lists) anime list, which answers what
   AniBridge does not.

Both downloaded sources are cached, and refetched only when the publisher says the file has
changed. AniDB itself supplies the rest: its daily titles dump for matching by name, and the
sequel and prequel relations it records between entries.

### How it works

- **Identifying.** A TMDB, IMDb or TVDB id another provider already settled on is looked up in
  the mapping sources; with no id to go on, the name is matched against the titles dump.
- **Placing seasons.** Each season is filled from the entries a mapping source places it in,
  checked against the episodes AniDB records for them, so a season one source is wrong about is
  still filled by the other. Where no source places it, AniDB's sequel relations are walked from
  the series' own entry, allotting entries to seasons by the episode counts the library holds -
  which is how a season split across two entries, or two seasons inside one, comes out right.
  Unplaced specials are matched by title and air date.
- **Fetching.** One document per AniDB entry carries its description, episodes, cast, tags and
  similarity votes, so a whole season costs a single request.
- **Staying unbanned.** AniDB bans a client that asks too often. Requests are queued and spaced
  apart, documents are cached and reused rather than fetched per episode, refused ids are
  remembered rather than asked about again, and a detected ban is waited out with a backoff that
  survives a server restart. The spacing and the cache lifetime are settings, with floors the
  plugin will not go below. The plugin page's **Status** section shows the ban state, the request
  queue and how fresh each mapping source is.

## Installation

[See the official documentation for install instructions](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

## Build

1. To build this plugin you will need [.NET 10.x](https://dotnet.microsoft.com/download/dotnet/10.0).

2. Build plugin with following command
  ```
  dotnet publish --configuration Release --output bin
  ```

3. Place the dll-file in the `plugins/anidb` folder (you might need to create the folders) of your JF install

## Releasing

To release the plugin we recommend [JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager) that will build and package the plugin.
For additional context and for how to add the packaged plugin zip to a plugin manifest see the [JPRM documentation](https://github.com/oddstr13/jellyfin-plugin-repository-manager) for more info.

## Contributing

We welcome all contributions and pull requests! If you have a larger feature in mind please open an issue so we can discuss the implementation before you start.
In general refer to our [contributing guidelines](https://github.com/jellyfin/.github/blob/master/CONTRIBUTING.md) for further information.

## Licence

This plugins code and packages are distributed under the GPLv2 License. See [LICENSE](./LICENSE) for more information.
