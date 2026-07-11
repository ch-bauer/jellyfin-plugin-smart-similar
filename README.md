# Jellyfin Smart Similar Plugin

Replaces the native **"More Like This"** section on movie and series detail pages with a
similar-items row that actually finds similar items. The section keeps the native look
(same title, position, cards, hover overlay, scrolling) — only the results get better.

## Why

Jellyfin's built-in similar-items algorithm regularly surfaces poor matches, and it pads
the row with the sequels/prequels of the movie you are looking at — which the collection
already covers. This plugin ranks with a proper similarity provider and drops items that
share a collection with the current item.

## Similarity providers (Dashboard → Plugins → Smart Similar)

- **Local smart scoring** (default): weighted score over your library's metadata — shared
  genres, tags, directors/writers/lead actors, studios, release-year proximity, rating
  similarity. Works instantly, fully offline.
- **TMDb recommendations**: uses TMDb's recommendation engine (the "Recommendations" you
  see on themoviedb.org) via the item's TMDb id, mapped back to items in your library.
  Needs a free TMDb API key; only items you own can appear.
- **Hybrid**: TMDb first, topped up with local scoring when TMDb maps to too few of your
  items. If TMDb is unavailable (no key, no TMDb id, network error), the plugin silently
  falls back to local scoring.

## Configuration

| Option | Default | Description |
|---|---|---|
| Similarity provider | Local | Local / TMDb / Hybrid |
| TMDb API key | – | v3 key or v4 read access token; a "Test key" button validates it |
| Maximum number of items | 16 | Length of the row |
| Hide items from the same collection | on | Excludes collection siblings (sequels, sagas) |
| Hide watched items | off | A series counts as watched when all episodes are played |
| Minimum match score | 15 | Local scoring only: stricter (higher) or fuller (lower) rows |

If the plugin has no results for an item (or an unsupported item type), the native
section is left untouched.

## Requirements

- Jellyfin **10.11.x**
- [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
  2.5.x or newer — install it from the repository `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
  (Dashboard → Plugins → Repositories), then restart Jellyfin.

## Install from plugin repository (recommended)

1. Dashboard → Plugins → Repositories → add
   `https://raw.githubusercontent.com/ch-bauer/jellyfin-plugin-smart-similar/main/manifest.json`
2. Install **Smart Similar** from the catalog and restart Jellyfin.
3. Hard-refresh the browser (Ctrl+F5) once after install/update.

## Manual install

1. Build: `dotnet publish src/Jellyfin.Plugin.SmartSimilar -c Release -o publish`
   (or download the zip from the GitHub releases).
2. Copy `Jellyfin.Plugin.SmartSimilar.dll` and `meta.json` into a folder
   `SmartSimilar_<version>/` inside your Jellyfin `plugins` directory
   (e.g. `/var/lib/jellyfin/plugins`, `/config/plugins` in Docker,
   `C:\ProgramData\Jellyfin\Server\plugins` on Windows).
3. Restart Jellyfin, then hard-refresh the browser (Ctrl+F5).

## Releasing a new version

1. Bump `AssemblyVersion`/`FileVersion` in the csproj and `version`/`changelog` in `meta.json`.
2. Commit, tag `v<version>` (e.g. `v1.0.0.0`), push the tag — the GitHub Actions workflow builds
   the zip and creates the release (it prints the zip's MD5 in the build log).
3. Add a new entry to `versions` in `manifest.json` (release asset URL + MD5 checksum) and push.

## How it works

- A startup task registers a transformation for `index.html` with the File Transformation plugin
  (via reflection, the documented integration path). The transformation inlines this plugin's
  JS/CSS into the page head — jellyfin-web itself is never modified on disk.
- The injected script watches for the item detail view and asks the plugin's API
  (`GET /SmartSimilar/Items?itemId=…&userId=…`) for the ranked similar item ids. While the
  plugin owns the page, the native "More Like This" section is hidden via a scoped CSS rule;
  if the plugin has nothing to show, the native section is restored untouched.
- The items are then fetched through the standard items API (so permissions, user data and
  image tags are respected), re-ordered to the ranking, and rendered with Jellyfin's own
  card markup and `emby-scroller` / `emby-itemscontainer` elements — including the localized
  native section title, read from the (hidden) native section.
- On the server, ranking comes from the configured provider. Collection membership is answered
  by an in-memory BoxSet reverse map, and TMDb ids are mapped to library items by an in-memory
  provider-id map — both caches are event-invalidated and warmed at startup. TMDb responses are
  cached for a day.
