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

This plugin adds the metadata provider for [aniDB](https://anidb.net/).

## Mapping overrides

The plugin works out which AniDB entry fills which season from two downloaded sources, the
[AniBridge mappings](https://github.com/anibridge/anibridge-mappings) and the
[Anime-Lists](https://github.com/Anime-Lists/anime-lists) anime list. Both describe AniDB as it
is, so neither can describe a library that holds something AniDB does not list, or a show AniDB
keeps inside another entry. Writing

```
<jellyfin config>/plugins/configurations/anidb-mapping-overrides.json
```

states such a thing outright. What it names is used as it stands, ahead of both sources; what it
does not name is left to them. There is no setting to turn it on: the file being there is the
setting, it is read again within five minutes of being changed, and the plugin page's **Status**
section says where it goes, when it was last written and what has been read from it.

The format is the AniBridge schema, so anything already written for those mappings can be pasted
in. One key per AniDB entry, naming which of the entry's numberings it maps from, and under it
one key per season, naming ranges of the entry against ranges of the season:

```json
{
  "anidb:665:O": { "tvdb_show:70873:s3": { "1-13": "1-13" } },
  "anidb:4521:S": { "tvdb_show:79093:s0": { "1-6": "1-6", "7-12": "8-13" } },
  "anidb:7777:R": { "tvdb_show:441190:s4": { "1-12": "1-12" } }
}
```

- `anidb:<id>:R` numbers the entry's ordinary episodes, `:S` its specials and `:O` its other
  episodes. `tvdb_show:<id>:s<n>` is the season as your library numbers it, `s0` being the
  specials. Ranges are `first-last` or a single number, the entry's on the left and the season's
  on the right.
- The first line above is a show AniDB holds as another entry's *other* episodes - Berserk's
  Golden Age Arc Memorial Edition and Hellsing Ultimate Abridged are held that way. It both
  identifies the show, there being no entry of its own to match by name, and fills its season.
- The second is a specials season holding one special AniDB does not list, at position 7:
  everything after it is one out of step, which without this costs the whole season its
  numbering. Season specials named by no range - 7 here - are left to be matched by title and
  air date, as any other unplaced special is.
- The third corrects one season of a show the downloaded sources already place. The entry named
  need not be the one the show is identified as; the TVDB id is what ties the two together.

A season's side can also list several ranges, and can end with a ratio weighting its episodes
against the entry's, both of which the AniBridge schema writes and this reads:

- `"1-12": "1-6,8-13"` names two ranges: the entry's twelve episodes fill the season's 1-6 and
  8-13, leaving its episode 7 to be matched some other way. The schema lists several ranges on
  the season's side only.
- `"13-": "14-|2"` weights them two to one: each episode of the entry is two of the season's, so
  the entry's 13 is the season's 14 and 15, its 14 is the season's 16 and 17, and both halves of
  each are described by the one AniDB episode holding them. That is a library numbering a
  two-part episode as two where AniDB lists it as one.
- `"1-4": "1-2|-2"` weights them the other way about, a negative ratio being that many of the
  entry's episodes to one of the season's: the entry's 1 and 2 are the season's episode 1, its 3
  and 4 the season's 2. The season's episode is described by the first of the pair, AniDB
  recording two and a library holding them as one episode having one place to put them.

The ratio belongs to the season's side, and its sign says which way round the weighting goes, so
there is nothing to write on the entry's side: `"1-2": "1|-2"` is what `"1": "1-2|2"` would say
backwards. A run with no end written is weighted out as far as any season could run and no
further.

Two things to know when writing one:

- A placement is checked against AniDB, and dropped with a warning in the log if it reads past
  the end of the entry it names. Only ordinary episodes can be checked that way: AniDB publishes
  no count of an entry's specials or other episodes, so a range over those is taken at your word.
- A season the file places is not measured against how many episodes your library holds under
  it, which is what lets a deliberately partial placement stand. The episodes it leaves out get
  no metadata rather than being placed some other way.

### Movies

A movie is named by its own id with whichever provider rather than by a season, since your library
holds it as one item:

```json
{
  "anidb:7:R": { "tmdb_movie:128": { "1": "1" }, "imdb_movie:tt0119698": { "1": "1" } },
  "anidb:665:O": { "tmdb_movie:123456": { "3": "1" } }
}
```

`tmdb_movie:`, `imdb_movie:` and `tvdb_movie:` are all read, and TVDB numbers its movies apart
from its series. The left side is the episode of the entry the movie is; the right side is always
`1`, a movie having nothing to number.

- The first line is a movie AniDB registered in its own right - anime 7 is Princess Mononoke, and
  those are its real ids - so the movie is that entry's episode 1 and is described by the entry's
  own record.
- The second is a movie AniDB holds inside an entry registered for something else, under whatever
  id your own copy carries: Berserk's Memorial Edition, a theatrical cut listed among a series'
  other episodes. There the movie takes its name, date and running time from **that episode**
  rather than from the entry, so several such movies of one show no longer come out as several
  copies of the same title. Cast, studios, genres and rating still come from the entry: AniDB
  records none of those per episode.

Movies are also identified from the downloaded sources now, with no file of your own - AniBridge
maps 2,853 of them and the anime list 2,084 - so a movie another provider has already given a
TMDB, IMDb or TVDB id needs an override only where those two are wrong about it or silent.

## Installation

[See the official documentation for install instructions](https://jellyfin.org/docs/general/server/plugins/index.html#installing).

## Build

1. To build this plugin you will need [.Net 5.x](https://dotnet.microsoft.com/download/dotnet/5.0).

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
