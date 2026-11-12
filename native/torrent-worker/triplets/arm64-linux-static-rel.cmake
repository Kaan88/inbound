# Static libraries, release only. Used by the T0 Linux arm64 build.
set(VCPKG_TARGET_ARCHITECTURE arm64)
set(VCPKG_CRT_LINKAGE dynamic)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_CMAKE_SYSTEM_NAME Linux)
set(VCPKG_BUILD_TYPE release)
set(VCPKG_CXX_FLAGS "-ffunction-sections -fdata-sections")
set(VCPKG_C_FLAGS "-ffunction-sections -fdata-sections")
