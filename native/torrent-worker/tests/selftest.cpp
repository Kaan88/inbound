// Unit checks for the worker's own pieces that do not need libtorrent: framing, base64, path safety, the piece summary
// and whole numbers.
#include "base64.hpp"
#include "framing.hpp"
#include "numbers.hpp"
#include "paths.hpp"
#include "pieces.hpp"

#include <cstdio>
#include <string>

using namespace inbound::torrent;

static int failures = 0;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL %s:%d %s\n", __FILE__, __LINE__, #x); ++failures; } } while (0)

int main()
{
    // base64 round trips, including every padding length and all byte values.
    for (int len = 0; len < 260; ++len) {
        std::string s;
        for (int i = 0; i < len; ++i) s += char((i * 37 + len) & 0xff);
        auto enc = base64_encode(s.data(), s.size());
        auto dec = base64_decode(enc);
        CHECK(dec && std::string(dec->begin(), dec->end()) == s);
    }
    CHECK(base64_encode("foobar", 6) == "Zm9vYmFy");
    CHECK(base64_encode("fo", 2) == "Zm8=");
    CHECK(!base64_decode("Zm9"));
    CHECK(!base64_decode("Zm=v"));
    CHECK(!base64_decode("Z!8="));

    // Paths: only relative paths that stay under the folder.
    CHECK(is_safe_part_suffix(".0f8fad5bd9cb469fa165707ef7d3e1c1.part"));
    CHECK(!is_safe_part_suffix("part"));
    CHECK(!is_safe_part_suffix("."));
    CHECK(!is_safe_part_suffix(".a/b"));
    CHECK(!is_safe_part_suffix(".a b"));
    CHECK(!is_safe_part_suffix(".a:b"));
    CHECK(is_safe_relative_path("a.bin"));
    CHECK(is_safe_relative_path("dir/sub/a.bin"));
    CHECK(is_safe_relative_path(R"(dir\a.bin)"));
    CHECK(is_safe_relative_path("console.txt"));
    CHECK(!is_safe_relative_path(""));
    CHECK(!is_safe_relative_path("/etc/passwd"));
    CHECK(!is_safe_relative_path(R"(\x)"));
    CHECK(!is_safe_relative_path("a/../../b"));
    CHECK(!is_safe_relative_path(R"(a\..\b)"));
    CHECK(!is_safe_relative_path(".."));
    CHECK(!is_safe_relative_path("a/./b"));
    CHECK(!is_safe_relative_path("a//b"));
    CHECK(!is_safe_relative_path("a/"));
    CHECK(!is_safe_relative_path("a\x01" "b"));
    CHECK(is_safe_relative_path("..x"));
    CHECK(is_safe_relative_path("nul.txt")); // contained; renamed, not refused
#ifdef _WIN32
    CHECK(!is_safe_relative_path("C:/x"));
    CHECK(!is_safe_relative_path("file.txt:stream"));
#else
    CHECK(is_safe_relative_path("Title: Part 1"));
#endif

    // Windows device names get a leading '_'; everything else is left alone.
    CHECK(windows_device_rename("nul.txt") == std::optional<std::string>("_nul.txt"));
    CHECK(windows_device_rename("dir/CoM1/a.txt") == std::optional<std::string>("dir/_CoM1/a.txt"));
    CHECK(windows_device_rename(R"(a\lpt9 .x)") == std::optional<std::string>(R"(a\_lpt9 .x)"));
    CHECK(windows_device_rename("COM\xC2\xB9.txt") == std::optional<std::string>("_COM\xC2\xB9.txt"));
    CHECK(windows_device_rename("aux") == std::optional<std::string>("_aux"));
    CHECK(!windows_device_rename("console.txt"));
    CHECK(!windows_device_rename("COM10"));
    CHECK(!windows_device_rename("nul_x/auxiliary"));
    CHECK(!windows_device_rename("COM"));

    // Framing round trip through a temporary file, and the size limit.
    std::FILE* f = std::tmpfile();
    CHECK(f != nullptr);
    if (f) {
        CHECK(write_frame(f, "{\"cmd\":\"x\"}"));
        CHECK(write_frame(f, ""));
        unsigned char huge[4] = {0xff, 0xff, 0xff, 0x7f};
        std::fwrite(huge, 1, 4, f);
        std::rewind(f);
        std::string out;
        CHECK(read_frame(f, out) == ReadResult::ok && out == "{\"cmd\":\"x\"}");
        CHECK(read_frame(f, out) == ReadResult::ok && out.empty());
        CHECK(read_frame(f, out) == ReadResult::too_large);
        std::fclose(f);
    }
    std::FILE* g = std::tmpfile();
    if (g) {
        unsigned char partial[6] = {10, 0, 0, 0, 'a', 'b'};
        std::fwrite(partial, 1, 6, g);
        std::rewind(g);
        std::string out;
        CHECK(read_frame(g, out) == ReadResult::truncated);
        CHECK(read_frame(g, out) == ReadResult::eof);
        std::fclose(g);
    }

    // Piece summary: ranges while they are few, a bitmap past kMaxPieceRanges.
    {
        auto none = summarize_pieces(10, [](int) { return false; });
        CHECK(none.count == 10 && none.have == 0 && none.ranges.empty() && none.bitmap.empty());
        auto some = summarize_pieces(10, [](int i) { return i < 3 || i == 5 || i >= 8; });
        CHECK(some.have == 6 && some.bitmap.empty());
        CHECK(some.ranges.size() == 3 && some.ranges[0] == std::make_pair(0, 2) && some.ranges[1] == std::make_pair(5, 5)
            && some.ranges[2] == std::make_pair(8, 9));
        auto all = summarize_pieces(7, [](int) { return true; });
        CHECK(all.have == 7 && all.ranges.size() == 1 && all.ranges[0] == std::make_pair(0, 6));
        int const n = int(kMaxPieceRanges) * 2 + 10;
        auto odd = summarize_pieces(n, [](int i) { return i % 2 == 0; });
        CHECK(odd.ranges.empty() && !odd.bitmap.empty() && odd.have == (n + 1) / 2);
        auto bits = base64_decode(odd.bitmap);
        CHECK(bits && bits->size() == std::size_t((n + 7) / 8) && static_cast<unsigned char>((*bits)[0]) == 0xAA);
    }

    // Whole numbers: a double or an unsigned past int64 is refused, never cast.
    {
        CHECK(whole_number(42.9) == std::optional<std::int64_t>(42));
        CHECK(whole_number(-3.5) == std::optional<std::int64_t>(-3));
        CHECK(whole_number(-9223372036854775808.0) == std::optional<std::int64_t>(INT64_MIN));
        CHECK(!whole_number(9223372036854775808.0));
        CHECK(!whole_number(1e300));
        CHECK(!whole_number(-1e300));
        CHECK(!whole_number(std::numeric_limits<double>::quiet_NaN()));
        CHECK(!whole_number(std::numeric_limits<double>::infinity()));
        CHECK(!whole_number(-std::numeric_limits<double>::infinity()));
        CHECK(whole_number(std::uint64_t(INT64_MAX)) == std::optional<std::int64_t>(INT64_MAX));
        CHECK(!whole_number(std::uint64_t(INT64_MAX) + 1));
        CHECK(!whole_number(UINT64_MAX));
    }

    if (failures == 0) std::printf("selftest: all checks pass\n");
    return failures == 0 ? 0 : 1;
}
