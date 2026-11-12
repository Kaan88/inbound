# Static libraries, static CRT (/MT), release only. Used by the T0 Windows build.
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_BUILD_TYPE release)
set(VCPKG_CXX_FLAGS "/Gy /Gw")
set(VCPKG_C_FLAGS "/Gy /Gw")
