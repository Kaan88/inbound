// File paths from torrent metadata. libtorrent already rewrites what the platform can't store (on Windows ':' becomes
// '_', trailing dots and spaces go). Two checks are the worker's own:
//  - is_safe_relative_path: every file must stay under the save folder; a torrent that fails it is refused.
//  - windows_device_rename: Windows opens a device instead of a file for names such as NUL or COM1 (also COM¹, COM²,
//    COM³ and the LPT forms); such an element gets a leading '_' so the data lands in a real file.
#pragma once

#include <optional>
#include <string>
#include <string_view>

namespace inbound::torrent {

// True when `path` (relative, '/' or '\\' separated) is safe to create under a save folder: not empty, not absolute,
// no "." or ".." or empty element, no control characters, and on Windows no ':' (drive letters, streams).
inline bool is_safe_relative_path(std::string_view path)
{
    if (path.empty() || path.size() > 4096) return false;
    if (path.front() == '/' || path.front() == '\\') return false;
    std::size_t start = 0;
    while (true) {
        std::size_t end = path.find_first_of("/\\", start);
        if (end == std::string_view::npos) end = path.size();
        std::string_view const e = path.substr(start, end - start);
        if (e.empty() || e == "." || e == "..") return false;
        for (char c : e) {
            if (static_cast<unsigned char>(c) < 0x20) return false;
#ifdef _WIN32
            if (c == ':') return false;
#endif
        }
        if (end == path.size()) return true;
        start = end + 1;
    }
}

// True when the element names a Windows device: CON, PRN, AUX, NUL, COM0-9, LPT0-9 and COM/LPT with a superscript
// 1, 2 or 3, in any case, with or without an extension or trailing spaces before it.
inline bool is_windows_device_name(std::string_view element)
{
    std::string_view stem = element.substr(0, element.find('.'));
    while (!stem.empty() && stem.back() == ' ') stem.remove_suffix(1);
    auto upper = [](char c) { return (c >= 'a' && c <= 'z') ? char(c - 32) : c; };
    auto starts = [&](std::string_view word) {
        if (stem.size() < word.size()) return false;
        for (std::size_t i = 0; i < word.size(); ++i)
            if (upper(stem[i]) != word[i]) return false;
        return true;
    };
    if (stem.size() == 3 && (starts("CON") || starts("PRN") || starts("AUX") || starts("NUL"))) return true;
    if (starts("COM") || starts("LPT")) {
        std::string_view const rest = stem.substr(3);
        if (rest.size() == 1 && rest[0] >= '0' && rest[0] <= '9') return true;
        // UTF-8 superscripts: U+00B9 (C2 B9), U+00B2 (C2 B2), U+00B3 (C2 B3).
        if (rest.size() == 2 && rest[0] == '\xC2' && (rest[1] == '\xB9' || rest[1] == '\xB2' || rest[1] == '\xB3'))
            return true;
    }
    return false;
}

// The path with every device-name element prefixed by '_', or nothing when no element needs it.
inline std::optional<std::string> windows_device_rename(std::string_view path)
{
    std::string out;
    bool changed = false;
    std::size_t start = 0;
    while (true) {
        std::size_t end = path.find_first_of("/\\", start);
        if (end == std::string_view::npos) end = path.size();
        std::string_view const e = path.substr(start, end - start);
        if (is_windows_device_name(e)) {
            out += '_';
            changed = true;
        }
        out += e;
        if (end == path.size()) break;
        out += path[end];
        start = end + 1;
    }
    if (!changed) return std::nullopt;
    return out;
}

// A suffix the Host may add to every file name while it downloads (".<job id>.part"): a dot, then at most 63 letters,
// digits, dots, dashes or underscores. Nothing that could change the folder or the file's type in a surprising way.
inline bool is_safe_part_suffix(std::string_view s)
{
    if (s.size() < 2 || s.size() > 64 || s.front() != '.') return false;
    for (char c : s.substr(1)) {
        bool const ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_';
        if (!ok) return false;
    }
    return true;
}

} // namespace inbound::torrent
