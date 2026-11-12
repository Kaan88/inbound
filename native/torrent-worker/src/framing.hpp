// Length-prefixed frames over a byte stream: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.
// The Host and the worker both refuse a frame above kMaxFrame, so a broken peer cannot make either side allocate
// without bound.
#pragma once

#include <cstdint>
#include <cstdio>
#include <optional>
#include <string>
#include <string_view>

namespace inbound::torrent {

inline constexpr std::uint32_t kMaxFrame = 32u * 1024u * 1024u;

enum class ReadResult { ok, eof, too_large, truncated };

// Reads one frame. eof means the stream ended cleanly before a frame began.
inline ReadResult read_frame(std::FILE* in, std::string& out)
{
    unsigned char len[4];
    std::size_t got = std::fread(len, 1, 4, in);
    if (got == 0) return ReadResult::eof;
    if (got != 4) return ReadResult::truncated;
    std::uint32_t const n = std::uint32_t(len[0]) | (std::uint32_t(len[1]) << 8) | (std::uint32_t(len[2]) << 16)
        | (std::uint32_t(len[3]) << 24);
    if (n > kMaxFrame) return ReadResult::too_large;
    out.resize(n);
    if (n > 0 && std::fread(out.data(), 1, n, in) != n) return ReadResult::truncated;
    return ReadResult::ok;
}

inline bool write_frame(std::FILE* out, std::string_view payload)
{
    if (payload.size() > kMaxFrame) return false;
    auto const n = std::uint32_t(payload.size());
    unsigned char const len[4] = {
        static_cast<unsigned char>(n), static_cast<unsigned char>(n >> 8), static_cast<unsigned char>(n >> 16),
        static_cast<unsigned char>(n >> 24)};
    if (std::fwrite(len, 1, 4, out) != 4) return false;
    if (n > 0 && std::fwrite(payload.data(), 1, n, out) != n) return false;
    return std::fflush(out) == 0;
}

} // namespace inbound::torrent
