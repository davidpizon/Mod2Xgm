# Third-party notices

Mod2Xgm is licensed under the [MIT License](LICENSE). It uses the third-party components below. Keep this file
current: every library added to `Directory.Packages.props`, and every ported or copied source, gets an entry with
its license, in the same pull request.

## Shipped in the Mod2Xgm executable

Versions are pinned in [`Directory.Packages.props`](Directory.Packages.props).

| Component | License | Source |
|---|---|---|
| Serilog | Apache-2.0 | https://github.com/serilog/serilog |
| Serilog.Sinks.Console | Apache-2.0 | https://github.com/serilog/serilog-sinks-console |
| System.CommandLine | MIT | https://github.com/dotnet/command-line-api |
| NWaves | MIT, notice below | https://github.com/ar1st0crat/NWaves |
| .NET runtime and libraries (Native AOT, .NET 10) | MIT | https://github.com/dotnet/runtime |
| pt2-clone: ProTracker 2.3 period tables (`src/pt2_tables.c`, in `src/Model/AmigaPitch.cs`) and replayer, ported to C# (`src/pt2_replayer.c` → `src/Mod/ModTickPlayer.cs`) | BSD-3-Clause, notice below | https://github.com/8bitbubsy/pt2-clone |
| ymfm: the YM3438 (OPN2) emulation, ported to C# (`src/ymfm_opn.cpp`, `src/ymfm_fm.ipp`, `src/ymfm.h` → `src/Render/Fm/`; [ADR 0056](docs/adr/0056-ym3438-model.md)) | BSD-3-Clause, notice below | https://github.com/aaronsgiles/ymfm |

## Used only for building and testing (not shipped)

| Component | License | Source |
|---|---|---|
| xunit.v3, xunit.runner.visualstudio | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | MIT | https://github.com/microsoft/vstest |
| Microsoft.Testing.Extensions.CodeCoverage | Microsoft .NET Library License (closed-source, free to use) | https://github.com/microsoft/codecoverage |
| libopenmpt (loaded at run time by `tools/Mod2Xgm.Harness` only; installed on the PC, not in the repository; [ADR 0047](docs/adr/0047-accuracy-harness.md)) | BSD-3-Clause | https://lib.openmpt.org/libopenmpt/ |
| R128Net (`tools/Mod2Xgm.Harness` and its tests only; [ADR 0053](docs/adr/0053-r128-loudness-in-the-harness.md)), a C# port of libebur128 (© 2011 Jan Kokemüller, MIT) | MIT | https://github.com/routersys/R128Net |
| Nuked-OPN2 (built and run only by `tools/ym3438-reference` to make the YM3438 reference outputs in `tests/golden/ym3438/`; downloaded at commit `335747d`, not in the repository, no code copied; [ADR 0056](docs/adr/0056-ym3438-model.md)), © 2017-2022 Alexey Khokholov (Nuke.YKT) | LGPL-2.1 | https://github.com/nukeykt/Nuked-OPN2 |
| ymfm, as C++ (built and run by `tools/ym3438-reference`, downloaded at commit `81aec25`) | BSD-3-Clause, notice below | https://github.com/aaronsgiles/ymfm |

## Reference documents included in `docs/formats/`

Kept verbatim for reference; they remain the property of their authors and are not covered by this
repository's MIT license.

| File | Author / source |
|---|---|
| `DMF_SPECS.txt` | DefleMask Module Format spec, © 2021 Delek (Leonardo Demartino), https://www.deflemask.com/ |
| `xgm.txt` | XGM file format v1.01, Stéphane Dallongeville, SGDK, https://github.com/Stephane-D/SGDK |

## Not used as code

Furnace (GPL-2.0, https://github.com/tildearrow/furnace, commit `bcba71d`) is read only as a reference for the DMF
format and DefleMask's behavior (`src/engine/fileOps/dmf.cpp` and the engine files named in
[`docs/formats/dmf.md`](docs/formats/dmf.md)): no code or tables are copied from it.

OpenMPT's Soundtracker loader (`soundlib/Load_stk.cpp`, BSD-3-Clause, https://github.com/OpenMPT/openmpt) is read
only as a reference for Ultimate Soundtracker's rules (`--player ust`): no code is copied from it.

## pt2-clone (BSD-3-Clause)

The period tables in `src/Model/AmigaPitch.cs` come from pt2-clone, and `src/Mod/ModTickPlayer.cs` is a C# port of
its replayer (`src/pt2_replayer.c`, commit 554bf79):

```
BSD 3-Clause License

Copyright (c) 2010-2026, Olav Sørensen
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## ymfm (BSD-3-Clause)

`src/Render/Fm/` is a C# port of ymfm's YM3438 emulation (`src/ymfm_opn.cpp`, `src/ymfm_fm.ipp`, `src/ymfm.h`, commit
81aec25):

```
BSD 3-Clause License

Copyright (c) 2021, Aaron Giles
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## NWaves (MIT)

`src/Audio/SampleProcessor.cs` uses NWaves (NuGet package `NWaves`) for smooth resampling:

```
MIT License

Copyright (c) 2017 Tim

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
