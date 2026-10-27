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
| .NET runtime and libraries (Native AOT, .NET 10) | MIT | https://github.com/dotnet/runtime |

Planned (add the entry when the code lands):

| Component | License | Use |
|---|---|---|
| System.CommandLine | MIT | Command-line parsing (plan M1) |
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
