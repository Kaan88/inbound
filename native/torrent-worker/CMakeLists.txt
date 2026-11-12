cmake_minimum_required(VERSION 3.25)
project(inbound_torrent_worker LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION ON)

find_package(LibtorrentRasterbar CONFIG REQUIRED)
find_package(boost_json CONFIG REQUIRED)

add_executable(inbound-torrent src/main.cpp)
target_link_libraries(inbound-torrent PRIVATE LibtorrentRasterbar::torrent-rasterbar Boost::json)

add_executable(inbound-torrent-selftest tests/selftest.cpp)
target_include_directories(inbound-torrent-selftest PRIVATE src)

if(MSVC)
  set_property(TARGET inbound-torrent inbound-torrent-selftest PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
  target_compile_options(inbound-torrent PRIVATE /W4 /Gy /Gw /utf-8)
  target_link_options(inbound-torrent PRIVATE /OPT:REF /OPT:ICF /DEBUG:NONE)
else()
  target_compile_options(inbound-torrent PRIVATE -Wall -Wextra -ffunction-sections -fdata-sections)
  target_link_options(inbound-torrent PRIVATE -Wl,--gc-sections -static-libstdc++ -static-libgcc -s)
endif()

enable_testing()
add_test(NAME selftest COMMAND inbound-torrent-selftest)
