// Whole numbers read from a request: a JSON number becomes an int64 only when it is one, so a NaN, an infinity or a
// value past the int64 range is refused instead of cast (which is undefined behaviour for a double).
#pragma once

#include <cmath>
#include <cstdint>
#include <limits>
#include <optional>

namespace inbound::torrent {

// The double's whole part, or nothing when it is NaN, infinite or outside int64.
inline std::optional<std::int64_t> whole_number(double value)
{
    // 2^63 is exact as a double; every double below it (and at or above -2^63) truncates into int64.
    constexpr double limit = 9223372036854775808.0;
    if (std::isnan(value) || !(value >= -limit && value < limit)) return std::nullopt;
    return static_cast<std::int64_t>(value);
}

// The unsigned value, or nothing when it is past int64's largest.
inline std::optional<std::int64_t> whole_number(std::uint64_t value)
{
    if (value > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) return std::nullopt;
    return static_cast<std::int64_t>(value);
}

} // namespace inbound::torrent
