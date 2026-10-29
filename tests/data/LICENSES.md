# Test data licenses

Every file under `tests/data/` is listed here with its license and source. The repository is public, so only files
that anyone may share go in: **Public Domain** or **CC0** (David, 2026-10-29; [ADR 0046](../../docs/adr/0046-mod-corpus.md)).
`CorpusTests` fails when a file is missing from this list, a listed file is missing, or a checksum differs.

## Corpus (`corpus/`, plan T6.1)

Eighteen 4-channel MODs from [The Mod Archive](https://modarchive.org), each licensed there by the artist who
uploaded it, plus one 15-sample song made in code. Downloaded 2026-10-29 from
`https://api.modarchive.org/downloads.php?moduleid=<id>`, unchanged.

| File | Title | Artist | License | Mod Archive ID | SHA-256 |
|---|---|---|---|---|---|
| `10kdub.mod` | 10k Reggae Dub | Jam | Public Domain | [201827](https://modarchive.org/index.php?request=view_by_moduleid&query=201827) | `4ef12d71ee3d65b4a573ea68b0b6156fcf150781b347e38d2212ffe4ea9d11b7` |
| `8bit_castle.mod` | 8-bit Castle | Jam | Public Domain | [195782](https://modarchive.org/index.php?request=view_by_moduleid&query=195782) | `425d66d49effa4f26805df6418b3aecd3d4f01d0ae956e1470b1e5ad5fa1418c` |
| `cancan.mod` | Can Can | SoDa7 (CDE MusiX) | Public Domain | [35056](https://modarchive.org/index.php?request=view_by_moduleid&query=35056) | `042b1fb8a743683839a874e258e4adac25948d4802bb04d2639127f63cdbd74d` |
| `drozerix_-_ai_renaissance.mod` | AI Renaissance | Drozerix | Public Domain | [200847](https://modarchive.org/index.php?request=view_by_moduleid&query=200847) | `67bfa1af577da037ab6f781c6feae87006ca386d5491d8a6381816c4747121c7` |
| `drozerix_-_neon_techno.mod` | Neon Techno | Drozerix | Public Domain | [178172](https://modarchive.org/index.php?request=view_by_moduleid&query=178172) | `65113989b86c165a5087106d7b40c55255a4e7c2a7b109755f678f7e1d47c142` |
| `drozerix_-_stardust_jam.mod` | Stardust Jam | Drozerix | Public Domain | [201039](https://modarchive.org/index.php?request=view_by_moduleid&query=201039) | `e7266c3e78f8abeb7de93557abb0a3953afcb2787f8803b1b4287380e4cd6190` |
| `drozerix_-_the_hawks_claw.mod` | The Hawk's Claw | Drozerix | Public Domain | [197917](https://modarchive.org/index.php?request=view_by_moduleid&query=197917) | `940d09b2c0c6f76f74cf56e76ed5ec70589ac607f7eb7c87fc3a9fc3d35d3971` |
| `fx-poly1.mod` | FX-POLY1 | k0wax | CC0 | [168680](https://modarchive.org/index.php?request=view_by_moduleid&query=168680) | `c8b9ae9ab68d50efaf79e2429fab609bd5ca95fc0dbc6eea83260c04c44364a6` |
| `hit.mod` | HIT THE CHEEK | Digital Beat | Public Domain | [173931](https://modarchive.org/index.php?request=view_by_moduleid&query=173931) | `6f46c99359c38d95b4d198df8fc6d781201090c147967e5a2ad9cadc35e85020` |
| `jam_-_heatwave_cruise.mod` | heatwave cruise | Jam | Public Domain | [208535](https://modarchive.org/index.php?request=view_by_moduleid&query=208535) | `a4b79993c8b141aa18de375829312164ec83da2f86f471c620f0762668dbf9d9` |
| `jam_-_latenightcoffee.mod` | late night coffee | Jam | Public Domain | [208532](https://modarchive.org/index.php?request=view_by_moduleid&query=208532) | `5b0e0a3f3828301350d294e903a4844c473a467de7cb6b690a6483d679c6c533` |
| `jam_-_xlr8.mod` | XLR8 | Jam | Public Domain | [205968](https://modarchive.org/index.php?request=view_by_moduleid&query=205968) | `8437827a1195cb2c9192ad8990bdbfb9e01f6f8f0013858af8f806b3812536fd` |
| `k0w-rsnd.mod` | "robotsound" | k0wax | CC0 | [168066](https://modarchive.org/index.php?request=view_by_moduleid&query=168066) | `0c5cfb456c163a141bfa733792946eb7012c622ae79292c505c748a546c27e0b` |
| `kc-dancinonamiga.mod` | DancinOnAmiga | Katie Cadet | Public Domain | [175731](https://modarchive.org/index.php?request=view_by_moduleid&query=175731) | `a5e4b8507c2e0340680968bd8655234e3688ffe67f7004aa1dc336539304eaeb` |
| `musix-cute-october.mod` | cute october | m0d (RedHeat) | Public Domain | [53273](https://modarchive.org/index.php?request=view_by_moduleid&query=53273) | `660cc46958cdaffd7b6c5c0cd362208d80843a195b3ceb57c91687eb8b90d2dc` |
| `musix-omg-plz-help.mod` | musix-1234 | m0d (MuSiX) | Public Domain | [53296](https://modarchive.org/index.php?request=view_by_moduleid&query=53296) | `69893bf6f4efeb8369b0b7a23a2a59ca5f87bcbf639f186f0e33dd8e3f975932` |
| `musix-rubbish.mod` | rubbish2 | m0d (Greg Tuby) | Public Domain | [53308](https://modarchive.org/index.php?request=view_by_moduleid&query=53308) | `1e6be7acd3c35f1f8e30a52042f89950dc3d8bab9542e179c009e93362766de2` |
| `sharpojects_cantpleaseeveryone.mod` | Can't please everyone | Sharp Objects | CC0 | [198767](https://modarchive.org/index.php?request=view_by_moduleid&query=198767) | `4d7c59f29fec6b9a805bea64aa4b9fc2d9017f7ebec78969242d2c044bbc84a1` |
| `soundtracker15.mod` | mod2xgm ust tune | made in code (`CorpusSongs.Soundtracker15`) | CC0 | — | `a41183b5f9264d0479c87cd1eebee2f021764742d08f1bec6c5294b51f458550` |

**Samples.** As with most MODs, some of these songs use sounds from elsewhere: `jam_-_xlr8.mod` and
`kc-dancinonamiga.mod` name Amiga "ST" sample-disk sounds, `k0w-rsnd.mod` says its samples are partly from the
Basehead 3.9 library, and `hit.mod` and `fx-poly1.mod` name sample files without saying where they came from. The
license is the one each artist set on The Mod Archive; the sounds' own rights aren't stated. They are kept as test
input only, never redistributed as part of the converter.

**Left out on purpose** (all marked Public Domain there): covers of other people's tunes, songs whose notes say the
samples came from someone else's song, a song whose text says "all rights reserved", a 32-channel file, and files over
1.5 MB.
