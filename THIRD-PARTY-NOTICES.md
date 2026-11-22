# Third-Party Notices

Chamfer is MIT-licensed. It uses the following third-party components, which
keep their own licences.

## Open CASCADE Technology (OCCT) 7.9.3

- Website: https://dev.opencascade.org
- Source: https://github.com/Open-Cascade-SAS/OCCT (tag `V7_9_3`)
- Licence: GNU Lesser General Public License v2.1 with the Open CASCADE
  exception (`LICENSE_LGPL_21.txt`, `OCCT_LGPL_EXCEPTION.txt` in the OCCT
  source tree).

Chamfer links to OCCT dynamically through the `chamfer_kernel` shim and ships
the unmodified OCCT shared libraries next to the application, so they can be
replaced with any compatible build. The OCCT source used is the upstream tag
above; `build/occt/build-occt.sh` records the build configuration.

## .NET libraries

| Package | Licence |
|---|---|
| Avalonia | MIT |
| CommunityToolkit.Mvvm | MIT |
| xunit.v3 (tests only) | Apache-2.0 |
