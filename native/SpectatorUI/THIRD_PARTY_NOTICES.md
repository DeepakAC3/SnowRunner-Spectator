# Native UI dependency notices

The native renderer vendors two upstream projects at pinned commits:

* Dear ImGui `f5befd2d29e66809cd1110a152e375a7f1981f06` (v1.91.9b), MIT license in `third_party/imgui/LICENSE.txt`.
* MinHook `c3fcafdc10146beb5919319d0683e44e3c30d537` (v1.3.4), BSD-style license in `third_party/minhook/LICENSE.txt`.

The required upstream source files and license text are kept with the build inputs. The resulting `SpectatorUI.dll` is a separate native component and does not require installation of an ASI loader; the parent helper can load it with `LoadLibraryW`.

