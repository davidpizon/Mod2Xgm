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

## Hold-out (`holdout/`, plan T9.4l)

Twenty-eight 4-channel MODs from [The Mod Archive](https://modarchive.org), kept apart from the corpus: they are the
songs of the learned listening score's hold-out round ([ADR 0105](../../docs/adr/0105-hold-out-songs.md)) and are never
used for fitting. Every MOD the site lists as Public Domain or CC0 that isn't in the corpus was checked on its own page
(2026-11-11) and the same rules as the corpus applied (David). Downloaded 2026-11-11 from
`https://api.modarchive.org/downloads.php?moduleid=<id>`, unchanged.

| File | Title | Artist | License | Mod Archive ID | SHA-256 |
|---|---|---|---|---|---|
| `absolu.mod` | absolute xtc | m0d (Greg Tuby) | Public Domain | [32703](https://modarchive.org/index.php?request=view_by_moduleid&query=32703) | `7e8bcfb9e61219cca850c41627b538d5ea123c9d6b077056d2867dd08a16eaa6` |
| `allsort.mod` | allsorts! | m0d (Greg Tuby) | Public Domain | [33325](https://modarchive.org/index.php?request=view_by_moduleid&query=33325) | `a421cbb3c83b7d6f977075a8ca753cb031b77238cf5cba7dc32d7741c61f4fe5` |
| `beyond_-_remix.mod` | untitled | Beyond | Public Domain | [171888](https://modarchive.org/index.php?request=view_by_moduleid&query=171888) | `2137255e3bc4173dacb0f048c1fe5826a0779373e30b637a38c51218e2a08338` |
| `drozerix_-_air_ride.mod` | Air Ride | Drozerix | Public Domain | [213325](https://modarchive.org/index.php?request=view_by_moduleid&query=213325) | `3f32cc282dd6352b24cd1910d96eaf3de7ef5a4a448566792d566c863039082f` |
| `drozerix_-_may_is_4_her.mod` | May is 4 Her | Drozerix | Public Domain | [197296](https://modarchive.org/index.php?request=view_by_moduleid&query=197296) | `9286f646779b5831ab06a62ef9a4f27c422d110dd9c5b1f1ef3a2211565de03f` |
| `drozerix_-_silicon_dancer.mod` | Silicon Dancer | Drozerix | Public Domain | [209692](https://modarchive.org/index.php?request=view_by_moduleid&query=209692) | `e0827f97a981fe00cf721320a07dbc0ca557c924d6736b902d61fb28c1dff396` |
| `em2.mod` | everone go m21 | m0d (Greg Tuby) | Public Domain | [41778](https://modarchive.org/index.php?request=view_by_moduleid&query=41778) | `ebaec4276f0e23edbada34b70e9021c9a7b00459135f3c2e927d9a0ba7940dca` |
| `jam_-_highway_encounter.mod` | Highway Encounter | Jam | Public Domain | [211660](https://modarchive.org/index.php?request=view_by_moduleid&query=211660) | `ce7ef7ac156887e212cd18197f82a5cc32d3015428a9fc7ec762a011aa232fbd` |
| `loituma.mod` | Ievan Polkka | Jam | Public Domain | [195783](https://modarchive.org/index.php?request=view_by_moduleid&query=195783) | `af690866f0aaa33d523e570708f079f5e7ae2f2c5e6350dd48889fd911f26ceb` |
| `modmon.mod` | happy | m0d (Greg Tuby) | Public Domain | [52296](https://modarchive.org/index.php?request=view_by_moduleid&query=52296) | `d0ccf583a5cbd70a9b06aa842b980ea03596a3c2b1d3daf0e8fbd8c3a4d0b7ec` |
| `musix-after.mod` | spo | m0d (Greg Tuby) | Public Domain | [53261](https://modarchive.org/index.php?request=view_by_moduleid&query=53261) | `1f86bcd802eccdbf9492391d87e237b8b427600f320a5c54fd5f4542a4b94a37` |
| `musix-aj.mod` | spare-a-j | m0d (MuSiX) | Public Domain | [53265](https://modarchive.org/index.php?request=view_by_moduleid&query=53265) | `835381a96b07be9f7bade062d8459f41f4e0d34fa832ffd1c27aea627e3d6ae2` |
| `musix-burp.mod` | funny tune ((grt)) | m0d (MuSiX) | Public Domain | [53271](https://modarchive.org/index.php?request=view_by_moduleid&query=53271) | `0911ac0ac74ef961d4bddb56a2ca495526d5c14ed602a352a4987bb5f8e14ffd` |
| `musix-calling-for-dx.mod` | calling 4 dx?! | m0d (Greg Tuby) | Public Domain | [66153](https://modarchive.org/index.php?request=view_by_moduleid&query=66153) | `e6a02aa234e835dff55e15eb599cc61ba5c2190b54b099595ad74300f290b462` |
| `musix-heaven-rmx.mod` | heavens open | m0d (Amiga Music Workshop) | Public Domain | [53282](https://modarchive.org/index.php?request=view_by_moduleid&query=53282) | `53efbac132faba4fdada2e111e35eaa4151040c99bf5cbad3c8612142c3cb7bb` |
| `musix-icecream-vince.mod` | vince mix | m0d (MuSiX) | Public Domain | [53285](https://modarchive.org/index.php?request=view_by_moduleid&query=53285) | `d5f2db16a86ab96bf95852c3103f762ad47611da4251fab149b7bc8de345e9da` |
| `musix-pleasant.mod` | pleasant2 | m0d (Greg Tuby) | Public Domain | [53299](https://modarchive.org/index.php?request=view_by_moduleid&query=53299) | `4addb4477db0648933abcf13878c90c0a7d0a2363d59107e4a1af6a4947e64f5` |
| `musix-rm.mod` | rm 2 | m0d (Greg Tuby) | Public Domain | [53304](https://modarchive.org/index.php?request=view_by_moduleid&query=53304) | `da0e446131d83519ba05dd0f17afaf6962b633084c8b06b53ff9e72045abdd1c` |
| `musix-shine.mod` | sjhine | m0d (Greg Tuby) | Public Domain | [53315](https://modarchive.org/index.php?request=view_by_moduleid&query=53315) | `d8559038c54754554d35202a565e4521395100aa5c98bec86bf65cf6c5e85ab3` |
| `musix-spaceman-rmx.mod` | spaced out | m0d (Greg Tuby) | Public Domain | [53317](https://modarchive.org/index.php?request=view_by_moduleid&query=53317) | `9df5d638d9bc97f6938e5f51d7731dca212770f6d3981f2594552a5bc12ba173` |
| `musix-spider.mod` | insi spider | m0d (Greg Tuby) | Public Domain | [53321](https://modarchive.org/index.php?request=view_by_moduleid&query=53321) | `17c67f8cdc9bbbf888ac0f58ec05f76dd29528a6d193089d063021d0fe73b1bb` |
| `musix-the-messanger.mod` | the messageman | m0d (Greg Tuby) | Public Domain | [53324](https://modarchive.org/index.php?request=view_by_moduleid&query=53324) | `5cf96de7edb91e5fa61c81417b635a06d601667fb6242b6c8c9ce2c36a63b1e4` |
| `musix-wild-perspective.mod` | wild perspective | m0d (Greg Tuby) | Public Domain | [53329](https://modarchive.org/index.php?request=view_by_moduleid&query=53329) | `da6f0478f25435af20b2a512adbe3a842db305b35d706585c16a23b26e64078a` |
| `musix-wormology.mod` | wormology | m0d (Greg Tuby) | Public Domain | [53333](https://modarchive.org/index.php?request=view_by_moduleid&query=53333) | `d9032ed99d10f7c224c1acfc92eb3dc64d7a35476cc5e3c7bb562d2c1787219a` |
| `musix-x-out.mod` | x-out | m0d (Greg Tuby) | Public Domain | [53335](https://modarchive.org/index.php?request=view_by_moduleid&query=53335) | `c0e33097c1e09a3f8b54bdea3d24d9eb7a17980248778c823cfc702820ae6bd8` |
| `sharp_objects_insideoutside.mod` | insideoutsidest | Sharp Objects | Public Domain | [196630](https://modarchive.org/index.php?request=view_by_moduleid&query=196630) | `0e2e03f7374f014b33323ccde4e839fe602d2ccfca9aa128d85b12afd0d8b18a` |
| `tgm-alguemematapfvr.mod` | Que Porr'essa | The Goofy-Mouse | CC0 | [212613](https://modarchive.org/index.php?request=view_by_moduleid&query=212613) | `bbe58c0f50db2befd69eaf59fcc703a35d1723393bf55ffe81742ca970bfb4e3` |
| `tgm-lazycows.mod` | Lazy Cows (bad) | The Goofy-Mouse | CC0 | [215663](https://modarchive.org/index.php?request=view_by_moduleid&query=215663) | `20ca815fd02d66ff6e829b522445e5977a389fe9067b5cb5aa351a7190399508` |

"m0d" is the account that uploaded the MusiX collection. Where a song's text names its composer (Greg Tuby, of Amiga
Music Workshop), it is in brackets; otherwise the collection is. `loituma.mod` is Jam's arrangement of Ievan Polkka, a
traditional Finnish song. As in the corpus, several songs' texts say their samples came from various sources or were
recorded by the artist; they are test input only, never redistributed as part of the converter.

**Left out on purpose** (all marked Public Domain there, David): covers of Rainbow Factory, Drac's Night Out and Robert
Miles' "Children"; songs whose notes say their samples came from another song or from various sources
(`musix-retrospective`, `rexywood_-_momhometown`, `music-jump`); one whose text says "All Rights Reserved" and one with a
"(c)" line (`musix-the-red-one`, `musix-pointless`); and three files over 1.5 MB whose notes raise doubts too: Goa
Phantasm's "LSD" says "Track is copyrighted", and two Sharp Objects songs are a tribute to another work.
