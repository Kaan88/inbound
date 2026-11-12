// Standard base64 (RFC 4648, with padding). Binary fields (.torrent files, resume blobs) travel in JSON this way.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace inbound::torrent {

inline std::string base64_encode(char const* data, std::size_t size)
{
    static constexpr char table[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((size + 2) / 3 * 4);
    std::size_t i = 0;
    auto const* p = reinterpret_cast<unsigned char const*>(data);
    for (; i + 2 < size; i += 3) {
        std::uint32_t v = (std::uint32_t(p[i]) << 16) | (std::uint32_t(p[i + 1]) << 8) | p[i + 2];
        out += table[(v >> 18) & 63];
        out += table[(v >> 12) & 63];
        out += table[(v >> 6) & 63];
        out += table[v & 63];
    }
    if (i < size) {
        std::uint32_t v = std::uint32_t(p[i]) << 16;
        if (i + 1 < size) v |= std::uint32_t(p[i + 1]) << 8;
        out += table[(v >> 18) & 63];
        out += table[(v >> 12) & 63];
        out += (i + 1 < size) ? table[(v >> 6) & 63] : '=';
        out += '=';
    }
    return out;
}

inline std::optional<std::vector<char>> base64_decode(std::string_view in)
{
    auto value = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    };
    if (in.size() % 4 != 0) return std::nullopt;
    std::vector<char> out;
    out.reserve(in.size() / 4 * 3);
    for (std::size_t i = 0; i < in.size(); i += 4) {
        int a = value(in[i]), b = value(in[i + 1]);
        if (a < 0 || b < 0) return std::nullopt;
        bool const last = i + 4 == in.size();
        int c = (last && in[i + 2] == '=') ? -2 : value(in[i + 2]);
        int d = (last && in[i + 3] == '=') ? -2 : value(in[i + 3]);
        if (c == -1 || d == -1 || (c == -2 && d != -2)) return std::nullopt;
        std::uint32_t v = (std::uint32_t(a) << 18) | (std::uint32_t(b) << 12);
        out.push_back(static_cast<char>((v >> 16) & 0xff));
        if (c >= 0) {
            v |= std::uint32_t(c) << 6;
            out.push_back(static_cast<char>((v >> 8) & 0xff));
        }
        if (d >= 0) {
            v |= std::uint32_t(d);
            out.push_back(static_cast<char>(v & 0xff));
        }
    }
    return out;
}

} // namespace inbound::torrent
