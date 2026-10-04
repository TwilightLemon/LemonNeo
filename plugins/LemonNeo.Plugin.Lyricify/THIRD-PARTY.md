# Third-party software

LemonNeo.Plugin.Lyricify uses **Lyricify Lyrics Helper**, copyright its
contributors, under the Apache License 2.0.

- Project: https://github.com/WXRIW/Lyricify-Lyrics-Helper
- Fork: https://github.com/TwilightLemon/Lyricify-Lyrics-Helper
- Runtime: the locally built `Lyricify.Lyrics.Helper` project in the submodule
- License copy: `external/Lyricify-Lyrics-Helper/LICENSE`

The fork is included as the `external/Lyricify-Lyrics-Helper` git submodule and
referenced directly by the plugin. It preserves QQ Music's `contentroma` as
`QqLyricsResponse.Romaji`, and carries available catalog artwork through the
search results, including Apple Music URL templates and Spotify image lists.
The built assembly and its transitive dependencies
are copied into the dynamically loaded plugin directory during the build.
