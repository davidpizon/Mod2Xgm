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
| .NET runtime and libraries (Native AOT, .NET 10) | MIT | https://github.com/dotnet/runtime |
| ProTracker 2.3 period tables, from pt2-clone `src/pt2_tables.c` (data only, in `src/Model/AmigaPitch.cs`) | BSD-3-Clause, notice below | https://github.com/8bitbubsy/pt2-clone |

Planned (add the entry when the code lands):

| Component | License | Use |
|---|---|---|
| ymfm (C# port) | BSD-3-Clause | YM2612 emulation for in-app rendering (plan M7). The BSD-3 notice must be reproduced here in full |

## Used only for building and testing (not shipped)

| Component | License | Source |
|---|---|---|
| xunit.v3, xunit.runner.visualstudio | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | MIT | https://github.com/microsoft/vstest |
| Microsoft.Testing.Extensions.CodeCoverage | Microsoft .NET Library License (closed-source, free to use) | https://github.com/microsoft/codecoverage |

## Reference documents included in `docs/formats/`

Kept verbatim for reference; they remain the property of their authors and are not covered by this
repository's MIT license.

| File | Author / source |
|---|---|
| `DMF_SPECS.txt` | DefleMask Module Format spec, © 2021 Delek (Leonardo Demartino), https://www.deflemask.com/ |
| `xgm.txt` | XGM file format v1.01, Stéphane Dallongeville, SGDK, https://github.com/Stephane-D/SGDK |

## Not used as code

Furnace's DMF loader is read only as a format reference (GPL): no code or tables are copied from it.

OpenMPT's Soundtracker loader (`soundlib/Load_stk.cpp`, BSD-3-Clause, https://github.com/OpenMPT/openmpt) is read
only as a reference for Ultimate Soundtracker's rules (`--player ust`): no code is copied from it.

## pt2-clone (BSD-3-Clause)

The period tables in `src/Model/AmigaPitch.cs` come from pt2-clone:

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
