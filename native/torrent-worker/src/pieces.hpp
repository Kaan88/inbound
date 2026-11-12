// Piece summary for the `pieces` command: the pieces the torrent has, as ranges when they are few, else as a bitmap.
#pragma once

#include "base64.hpp"

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace inbound::torrent {

struct PieceSummary {
    int count = 0;
    int have = 0;
    // [first, last] pairs of pieces the torrent has, inclusive; empty when `bitmap` is used instead.
    std::vector<std::pair<int, int>> ranges;
    // Base64 of one bit per piece, piece 0 in the highest bit of the first byte; empty when `ranges` is used.
    std::string bitmap;
};

// Up to this many ranges are sent as ranges; past it, the bitmap is smaller or not much larger.
constexpr std::size_t kMaxPieceRanges = 4096;

template <typename Has>
PieceSummary summarize_pieces(int count, Has&& has)
{
    PieceSummary s;
    s.count = count;
    bool too_many = false;
    for (int i = 0; i < count; ++i) {
        if (!has(i)) continue;
        ++s.have;
        if (too_many) continue;
        if (!s.ranges.empty() && s.ranges.back().second == i - 1) s.ranges.back().second = i;
        else if (s.ranges.size() < kMaxPieceRanges) s.ranges.emplace_back(i, i);
        else too_many = true;
    }
    if (too_many) {
        s.ranges.clear();
        std::string bits(static_cast<std::size_t>((count + 7) / 8), '\0');
        for (int i = 0; i < count; ++i)
            if (has(i)) bits[static_cast<std::size_t>(i / 8)] = char(static_cast<unsigned char>(bits[static_cast<std::size_t>(i / 8)]) | (0x80u >> (i % 8)));
        s.bitmap = base64_encode(bits.data(), bits.size());
    }
    return s;
}

} // namespace inbound::torrent
