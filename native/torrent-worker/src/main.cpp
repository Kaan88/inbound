// Inbound torrent worker (T0, docs/plans/native-torrent-roadmap.md).
//
// A stateless native process the Host starts only while a torrent is active. It speaks length-prefixed JSON over
// stdin/stdout (framing.hpp) and logs to stderr only. The Host keeps every record: torrents, file choices, folders and
// the resume blobs this worker hands back. When stdin closes (the Host exited or died) the worker saves what it can and
// exits, so it never outlives its Host.

#include "base64.hpp"
#include "framing.hpp"
#include "numbers.hpp"
#include "paths.hpp"
#include "pieces.hpp"

#include <boost/json.hpp>

#include <libtorrent/add_torrent_params.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/announce_entry.hpp>
#include <libtorrent/error_code.hpp>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/magnet_uri.hpp>
#include <libtorrent/peer_info.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <libtorrent/session.hpp>
#include <libtorrent/session_stats.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/settings_pack.hpp>
#include <libtorrent/torrent_handle.hpp>
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/torrent_status.hpp>
#include <libtorrent/version.hpp>
#include <libtorrent/write_resume_data.hpp>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <algorithm>
#include <vector>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#endif

namespace json = boost::json;
namespace lt = libtorrent;
using namespace std::chrono_literals;
using clock_type = std::chrono::steady_clock;

namespace inbound::torrent {

constexpr int kProtocolVersion = 1;
constexpr char const* kWorkerVersion = "0.4.0-t3";

// Limits applied to metadata before it is accepted. The Host checks again (and checks free space).
struct Limits {
    int max_files = 100000;
    int max_metadata_bytes = 16 * 1024 * 1024;
    int max_pieces = 0x200000;
};

// ---------------------------------------------------------------------------------------------------------------
// Output: one writer, frames only on stdout.

class Output {
public:
    void send(json::object const& o)
    {
        std::string s = json::serialize(o);
        if (s.size() > kMaxFrame) {
            // Too big for one frame (a resume blob of a huge torrent, say): report that instead of the message,
            // rather than treating it as a broken pipe.
            json::object e;
            e["event"] = "error";
            if (auto const* id = o.if_contains("id")) e["id"] = *id;
            e["kind"] = "protocol";
            e["message"] = "a message was larger than a frame may be and was dropped";
            s = json::serialize(e);
        }
        std::lock_guard<std::mutex> lock(mutex_);
        if (!inbound::torrent::write_frame(stdout, s)) broken_ = true;
    }
    bool broken() const { return broken_; }

private:
    std::mutex mutex_;
    std::atomic<bool> broken_{false};
};

void wlog(char const* fmt, auto... args)
{
    std::fprintf(stderr, "[torrent-worker] ");
    if constexpr (sizeof...(args) == 0) std::fputs(fmt, stderr);
    else std::fprintf(stderr, fmt, args...);
    std::fputc('\n', stderr);
    std::fflush(stderr);
}

// ---------------------------------------------------------------------------------------------------------------
// Input: a reader thread turns frames into commands for the main loop.

struct Inbox {
    std::mutex mutex;
    std::condition_variable wake;
    std::deque<json::object> commands;
    bool stdin_closed = false;
    bool alerts_pending = false;
};

void reader_thread(Inbox& inbox)
{
    std::string frame;
    for (;;) {
        auto const r = read_frame(stdin, frame);
        if (r != ReadResult::ok) {
            if (r == ReadResult::too_large) wlog("frame above the limit; closing");
            else if (r == ReadResult::truncated) wlog("truncated frame; closing");
            break;
        }
        boost::system::error_code ec;
        json::value v = json::parse(frame, ec);
        std::lock_guard<std::mutex> lock(inbox.mutex);
        if (ec || !v.is_object()) {
            json::object bad;
            bad["cmd"] = "__invalid";
            inbox.commands.push_back(std::move(bad));
        } else {
            inbox.commands.push_back(std::move(v.as_object()));
        }
        inbox.wake.notify_one();
    }
    std::lock_guard<std::mutex> lock(inbox.mutex);
    inbox.stdin_closed = true;
    inbox.wake.notify_one();
}

// ---------------------------------------------------------------------------------------------------------------
// Helpers for reading fields.

struct BadRequest {
    std::string message;
};

std::string get_string(json::object const& o, char const* key, bool required = true)
{
    auto const* v = o.if_contains(key);
    if (!v || v->is_null()) {
        if (required) throw BadRequest{std::string("missing ") + key};
        return {};
    }
    if (!v->is_string()) throw BadRequest{std::string(key) + " must be a string"};
    return std::string(v->as_string());
}

std::int64_t get_int(json::object const& o, char const* key, std::int64_t fallback)
{
    auto const* v = o.if_contains(key);
    if (!v || v->is_null()) return fallback;
    if (v->is_int64()) return v->as_int64();
    std::optional<std::int64_t> whole;
    if (v->is_uint64()) whole = whole_number(v->as_uint64());
    else if (v->is_double()) whole = whole_number(v->as_double());
    else throw BadRequest{std::string(key) + " must be a number"};
    if (!whole) throw BadRequest{std::string(key) + " is out of range"};
    return *whole;
}

double get_double(json::object const& o, char const* key, double fallback)
{
    auto const* v = o.if_contains(key);
    if (!v || v->is_null()) return fallback;
    if (v->is_double()) return v->as_double();
    if (v->is_int64()) return double(v->as_int64());
    if (v->is_uint64()) return double(v->as_uint64());
    throw BadRequest{std::string(key) + " must be a number"};
}

bool get_bool(json::object const& o, char const* key, bool fallback)
{
    auto const* v = o.if_contains(key);
    if (!v || v->is_null()) return fallback;
    if (!v->is_bool()) throw BadRequest{std::string(key) + " must be true or false"};
    return v->as_bool();
}

std::vector<lt::download_priority_t> get_priorities(json::object const& o, char const* key)
{
    std::vector<lt::download_priority_t> out;
    auto const* v = o.if_contains(key);
    if (!v || v->is_null()) return out;
    if (!v->is_array()) throw BadRequest{std::string(key) + " must be a list"};
    for (auto const& e : v->as_array()) {
        if (!e.is_int64() && !e.is_uint64()) throw BadRequest{std::string(key) + " must hold numbers 0 to 7"};
        auto const n = e.is_int64() ? e.as_int64() : std::int64_t(e.as_uint64());
        if (n < 0 || n > 7) throw BadRequest{std::string(key) + " must hold numbers 0 to 7"};
        out.push_back(lt::download_priority_t(static_cast<std::uint8_t>(n)));
    }
    return out;
}

char const* state_name(lt::torrent_status const& s)
{
    if (s.errc) return "error";
    // T3: a seed that libtorrent's seeding queue holds back (more seeds than "seeding at once") waits its turn.
    // One that waits for its file check reads "checking", as it does when it runs.
    if ((s.flags & lt::torrent_flags::paused) && (s.flags & lt::torrent_flags::auto_managed)) {
        if (s.state == lt::torrent_status::checking_files || s.state == lt::torrent_status::checking_resume_data)
            return "checking";
        return "queued";
    }
    if (!(s.flags & lt::torrent_flags::paused)) {
        switch (s.state) {
        case lt::torrent_status::checking_files:
        case lt::torrent_status::checking_resume_data: return "checking";
        case lt::torrent_status::downloading_metadata: return "metadata";
        case lt::torrent_status::downloading: return "downloading";
        case lt::torrent_status::finished:
        case lt::torrent_status::seeding: return "seeding";
        default: return "downloading";
        }
    }
    return "paused";
}

// ---------------------------------------------------------------------------------------------------------------
// The worker.

class Worker {
public:
    Worker(Output& out, Inbox& inbox, std::string dht_state_path, bool test_hooks)
        : out_(out), inbox_(inbox), dht_state_path_(std::move(dht_state_path)), test_hooks_(test_hooks)
    {
    }

    int run()
    {
        start_session();
        json::object hello;
        hello["event"] = "hello";
        hello["protocol"] = kProtocolVersion;
        hello["worker"] = kWorkerVersion;
        hello["libtorrent"] = lt::version();
        out_.send(hello);

        auto next_update = clock_type::now() + progress_interval_;
        auto next_resume = clock_type::now() + resume_interval_;
        bool stdin_closed = false;
        while (!quit_) {
            std::deque<json::object> commands;
            bool alerts = false;
            {
                std::unique_lock<std::mutex> lock(inbox_.mutex);
                auto const deadline = std::min(next_update, next_resume);
                // Sleep until a command, an alert or the next timer. With no torrent there is no timer at all.
                auto ready = [&] { return !inbox_.commands.empty() || inbox_.alerts_pending || inbox_.stdin_closed; };
                if (handles_.empty()) inbox_.wake.wait(lock, ready);
                else inbox_.wake.wait_until(lock, deadline, ready);
                commands.swap(inbox_.commands);
                alerts = inbox_.alerts_pending;
                inbox_.alerts_pending = false;
                stdin_closed = inbox_.stdin_closed;
            }
            for (auto& c : commands) handle_command(c);
            if (alerts) drain_alerts();
            auto const now = clock_type::now();
            if (!handles_.empty() && now >= next_update) {
                session_->post_torrent_updates();
                next_update = now + progress_interval_;
            }
            if (!handles_.empty() && now >= next_resume) {
                save_resume_all(lt::torrent_handle::only_if_modified);
                next_resume = now + resume_interval_;
            }
            if (handles_.empty()) {
                next_update = clock_type::now() + progress_interval_;
                next_resume = clock_type::now() + resume_interval_;
            }
            if (stdin_closed || out_.broken()) {
                wlog(stdin_closed ? "stdin closed; shutting down" : "stdout broken; shutting down");
                quit_ = true;
            }
        }
        shutdown();
        return 0;
    }

private:
    // --- session -------------------------------------------------------------------------------------------------

    void start_session()
    {
        lt::session_params params;
        if (!dht_state_path_.empty()) {
            std::ifstream f(dht_state_path_, std::ios::binary);
            if (f) {
                std::vector<char> buf((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
                try {
                    params = lt::read_session_params(buf, lt::session_handle::save_dht_state);
                } catch (std::exception const& e) {
                    wlog("DHT state not usable (%s); starting without it", e.what());
                    params = lt::session_params{};
                }
            }
        }
        auto& s = params.settings;
        s.set_int(lt::settings_pack::alert_mask, base_alert_mask());
        // Outgoing-only and quiet until the Host sends setListen: no listen socket, no DHT, no UPnP.
        s.set_str(lt::settings_pack::listen_interfaces, "");
        s.set_bool(lt::settings_pack::enable_dht, false);
        s.set_bool(lt::settings_pack::enable_lsd, false);
        s.set_bool(lt::settings_pack::enable_upnp, false);
        s.set_bool(lt::settings_pack::enable_natpmp, false);
        s.set_int(lt::settings_pack::active_downloads, -1);
        s.set_int(lt::settings_pack::active_seeds, -1);
        s.set_int(lt::settings_pack::active_limit, -1);
        s.set_int(lt::settings_pack::alert_queue_size, 4000);
        session_ = std::make_unique<lt::session>(std::move(params));
        session_->set_alert_notify([this] {
            std::lock_guard<std::mutex> lock(inbox_.mutex);
            inbox_.alerts_pending = true;
            inbox_.wake.notify_one();
        });
    }

    void shutdown()
    {
        // Pause everything and collect a last resume blob for each torrent, up to a few seconds.
        if (!handles_.empty()) {
            session_->pause();
            // Every request ever made gets its answer before exit, so the last blob sent is the paused state.
            save_resume_all(lt::torrent_handle::save_info_dict);
            auto const deadline = clock_type::now() + 5s;
            while (!outstanding_saves_.empty() && clock_type::now() < deadline) {
                session_->wait_for_alert(200ms);
                drain_alerts();
            }
        }
        if (!dht_state_path_.empty()) {
            try {
                auto const state = session_->session_state(lt::session_handle::save_dht_state);
                auto const buf = lt::write_session_params_buf(state, lt::session_handle::save_dht_state);
                std::string const tmp = dht_state_path_ + ".tmp";
                {
                    std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
                    f.write(buf.data(), std::streamsize(buf.size()));
                    f.flush();
                    if (!f) throw std::runtime_error("write failed");
                }
                std::error_code ec;
                std::filesystem::rename(tmp, dht_state_path_, ec);
                if (ec) wlog("could not keep the DHT state: %s", ec.message().c_str());
            } catch (std::exception const& e) {
                wlog("could not keep the DHT state: %s", e.what());
            }
        }
        json::object bye;
        bye["event"] = "bye";
        out_.send(bye);
        session_->set_alert_notify([] {});
        session_.reset();
    }

    // Asks every torrent for resume data; returns how many requests went out.
    int save_resume_all(lt::resume_data_flags_t flags)
    {
        int n = 0;
        for (auto const& [id, h] : handles_) {
            if (!h.is_valid()) continue;
            if (!(flags & lt::torrent_handle::save_info_dict) && !h.need_save_resume_data()) continue;
            request_save(h, flags);
            ++n;
        }
        return n;
    }

    // --- commands ------------------------------------------------------------------------------------------------

    void reply(json::object const& cmd, bool ok, std::string const& message = {}, json::object extra = {})
    {
        json::object r;
        r["event"] = "reply";
        if (auto const* id = cmd.if_contains("seq")) r["seq"] = *id;
        r["ok"] = ok;
        if (!message.empty()) r["message"] = message;
        for (auto& kv : extra) r[kv.key()] = std::move(kv.value());
        out_.send(r);
    }

    // A reply sent later than its command (finalize waits for libtorrent).
    void send_reply(json::value const& seq, bool ok, std::string const& message = {})
    {
        json::object r;
        r["event"] = "reply";
        if (!seq.is_null()) r["seq"] = seq;
        r["ok"] = ok;
        if (!message.empty()) r["message"] = message;
        out_.send(r);
    }

    // The name a file gets while it downloads: the torrent's own path, with its first element replaced by rootName,
    // a Windows device name defused, and partSuffix added.
    static std::string planned_path(std::string path, std::string const& torrent_name, std::string const& root_name,
        std::string const& suffix)
    {
        if (!root_name.empty() && !torrent_name.empty() && path.compare(0, torrent_name.size(), torrent_name) == 0
            && (path.size() == torrent_name.size() || path[torrent_name.size()] == '/' || path[torrent_name.size()] == '\\'))
            path = root_name + path.substr(torrent_name.size());
#ifdef _WIN32
        if (auto renamed = windows_device_rename(path)) path = std::move(*renamed);
#endif
        return path + suffix;
    }

    // A path libtorrent gives (UTF-8, '/' or '\' separated) under save_path, for std::filesystem.
    static std::filesystem::path disk_path(std::string const& save_path, std::string const& relative)
    {
        auto const utf8 = [](std::string const& s) { return std::filesystem::path(std::u8string(s.begin(), s.end())); };
        return utf8(save_path) / utf8(relative);
    }

    std::string planned_for(std::string const& id, std::string const& path, std::string const& torrent_name, bool with_suffix) const
    {
        auto const s = part_suffix_.find(id);
        auto const r = root_name_.find(id);
        return planned_path(path, torrent_name, r == root_name_.end() ? std::string() : r->second,
            with_suffix && s != part_suffix_.end() ? s->second : std::string());
    }

    void handle_command(json::object& cmd)
    {
        std::string name;
        try {
            name = get_string(cmd, "cmd");
            if (name == "add") add(cmd);
            else if (name == "pause") with_torrent(cmd, [this](lt::torrent_handle& h) { stop_managing(h); h.pause(lt::torrent_handle::graceful_pause); request_save(h); });
            else if (name == "resume") with_torrent(cmd, [](lt::torrent_handle& h) { h.resume(); });
            else if (name == "recheck") recheck(cmd);
            else if (name == "announce") announce(cmd);
            else if (name == "sequential") set_sequential(cmd);
            else if (name == "details") details(cmd);
            else if (name == "setPeerLimits") set_peer_limits(cmd);
            else if (name == "setProxy") set_proxy(cmd);
            else if (name == "remove") remove(cmd);
            else if (name == "setFilePriorities") set_file_priorities(cmd);
            else if (name == "finalize") finalize(cmd);
            else if (name == "setLimits") set_limits(cmd);
            else if (name == "setTorrentLimits") set_torrent_limits(cmd);
            else if (name == "setSeedPolicy") set_seed_policy(cmd);
            else if (name == "setListen") set_listen(cmd);
            else if (name == "setProgressInterval") set_progress_interval(cmd);
            else if (name == "addPeer") add_peer(cmd);
            else if (name == "saveResume") { save_resume_all(lt::torrent_handle::save_info_dict); reply(cmd, true); }
            else if (name == "stats") stats(cmd);
            else if (name == "files") files(cmd);
            else if (name == "seed") set_seed(cmd);
            else if (name == "peers") peers(cmd);
            else if (name == "trackers") trackers(cmd);
            else if (name == "pieces") pieces(cmd);
            else if (name == "shutdown") { reply(cmd, true); quit_ = true; }
            else if (name == "debugAbort" && test_hooks_) { wlog("debugAbort: aborting as asked (test hook)"); std::abort(); }
            else reply(cmd, false, "unknown command");
        } catch (BadRequest const& e) {
            reply(cmd, false, e.message);
        } catch (lt::system_error const& e) {
            reply(cmd, false, e.what());
        } catch (std::exception const& e) {
            reply(cmd, false, e.what());
        }
    }

    template <typename F>
    void with_torrent(json::object const& cmd, F&& f)
    {
        auto const id = get_string(cmd, "id");
        auto it = handles_.find(id);
        if (it == handles_.end() || !it->second.is_valid()) {
            reply(cmd, false, "unknown torrent");
            return;
        }
        f(it->second);
        reply(cmd, true);
    }

    void add(json::object const& cmd)
    {
        auto const id = get_string(cmd, "id");
        if (handles_.count(id)) throw BadRequest{"a torrent with this id is already added"};
        auto const save_path = get_string(cmd, "savePath");
        if (save_path.empty()) throw BadRequest{"savePath is empty"};

        lt::load_torrent_limits limits;
        limits.max_buffer_size = limits_.max_metadata_bytes;
        limits.max_pieces = limits_.max_pieces;

        // Read every field before anything is added, so a bad field can't leave a torrent running behind a refusal.
        bool const paused = get_bool(cmd, "paused", false);
        bool const seed = get_bool(cmd, "seed", false);
        bool const sequential = get_bool(cmd, "sequential", false);
        auto prios = get_priorities(cmd, "filePriorities");
        if (prios.size() > static_cast<std::size_t>(limits_.max_files)) throw BadRequest{"more file priorities than files allowed"};
        bool const hold = get_bool(cmd, "hold", false);
        // The Host finalized this torrent earlier: its files already carry their own names.
        bool const finalized = get_bool(cmd, "finalized", false);
        auto const part_suffix = get_string(cmd, "partSuffix", false);
        if (!part_suffix.empty() && !is_safe_part_suffix(part_suffix)) throw BadRequest{"partSuffix must start with a dot and hold only letters, digits, dots, dashes and underscores (at most 64)"};
        auto const root_name = get_string(cmd, "rootName", false);
        if (!root_name.empty() && (root_name.find_first_of("/\\") != std::string::npos || !is_safe_relative_path(root_name)))
            throw BadRequest{"rootName must be one safe name"};
        // T3: this torrent's own caps (bytes/s, 0 = none) and a file check right after the add.
        auto const down_limit = int(std::clamp<std::int64_t>(get_int(cmd, "downloadLimit", 0), 0, INT32_MAX));
        auto const up_limit = int(std::clamp<std::int64_t>(get_int(cmd, "uploadLimit", 0), 0, INT32_MAX));
        bool const recheck_after = get_bool(cmd, "recheck", false);

        lt::add_torrent_params atp;
        lt::error_code ec;
        auto const resume = get_string(cmd, "resume", false);
        auto const torrent = get_string(cmd, "torrent", false);
        auto const magnet = get_string(cmd, "magnet", false);
        if (!resume.empty()) {
            auto bytes = base64_decode(resume);
            if (!bytes) throw BadRequest{"resume is not base64"};
            atp = lt::read_resume_data(*bytes, ec, limits);
            if (ec) throw BadRequest{"resume data not usable: " + ec.message()};
        } else if (!torrent.empty()) {
            auto bytes = base64_decode(torrent);
            if (!bytes) throw BadRequest{"torrent is not base64"};
            atp = lt::load_torrent_buffer(*bytes, limits);
        } else if (!magnet.empty()) {
            lt::parse_magnet_uri(magnet, atp, ec);
            if (ec) throw BadRequest{"magnet not usable: " + ec.message()};
        } else {
            throw BadRequest{"give resume, torrent or magnet"};
        }
        bool const has_metadata = static_cast<bool>(atp.ti);
        bool const from_resume = !resume.empty();
        if (has_metadata) {
            check_metadata(*atp.ti);
            // Every file gets its part name, unless resume data already gave it one. What is on disk never decides
            // (a user's file of the same name must not be taken for a finished one). A torrent the Host says was
            // finalized gets its final names instead, whatever stale renames its resume data holds.
            auto const& fs = atp.ti->layout();
            for (auto const i : fs.file_range()) {
                if (fs.pad_file_at(i)) continue;
                if (finalized) {
                    auto final_name = planned_path(fs.file_path(i), atp.ti->name(), root_name, std::string());
                    if (final_name == fs.file_path(i)) atp.renamed_files.erase(i);
                    else atp.renamed_files[i] = std::move(final_name);
                    continue;
                }
                if (from_resume && atp.renamed_files.count(i)) continue;
                auto planned = planned_path(fs.file_path(i), atp.ti->name(), root_name, part_suffix);
                if (planned != fs.file_path(i)) atp.renamed_files[i] = std::move(planned);
            }
        }

        atp.save_path = save_path;
        if (!prios.empty()) atp.file_priorities = std::move(prios);
        // The Host owns queueing: nothing is auto-managed, and a duplicate is an error, not a merge.
        atp.flags &= ~lt::torrent_flags::auto_managed;
        atp.flags |= lt::torrent_flags::duplicate_is_error;
        if (paused) atp.flags |= lt::torrent_flags::paused;
        else atp.flags &= ~lt::torrent_flags::paused;
        if (sequential) atp.flags |= lt::torrent_flags::sequential_download;
        else atp.flags &= ~lt::torrent_flags::sequential_download;
        // T3: peer exchange off for every torrent when the Host says so (libtorrent keeps it off for private ones).
        if (!pex_) atp.flags |= lt::torrent_flags::disable_pex;
        else atp.flags &= ~lt::torrent_flags::disable_pex;
        if (torrent_connections_ > 0) atp.max_connections = torrent_connections_;
        atp.download_limit = down_limit == 0 ? -1 : down_limit;
        atp.upload_limit = up_limit == 0 ? -1 : up_limit;
        // T3: a finished torrent added to seed joins libtorrent's seeding queue while "seeding at once" is set.
        if (seed && finalized && !paused && active_seeds_ > 0) atp.flags |= lt::torrent_flags::auto_managed;
        // Without metadata (a magnet), start in upload mode: the metadata is still fetched from peers, but no piece
        // is requested, so the file list is checked before anything is written. send_metadata lifts it.
        // (stop_when_ready would pause the magnet before it had its metadata.)
        // hold: stay in upload mode after the metadata until the Host sends setFilePriorities (the user chooses files).
        // Otherwise clear it: resume data saved while a torrent was held carries the flag, and the torrent would then
        // never request a piece.
        if (!has_metadata || hold) atp.flags |= lt::torrent_flags::upload_mode;
        else atp.flags &= ~lt::torrent_flags::upload_mode;
        // T3: a finished torrent (files with their final names) starts in upload mode too, so a file damaged or gone
        // since is never downloaded into; its check then lets it seed, or pauses it with an error.
        bool const check_finished = finalized && has_metadata;
        if (check_finished) atp.flags |= lt::torrent_flags::upload_mode;

        lt::torrent_handle h = session_->add_torrent(std::move(atp), ec);
        if (ec) throw BadRequest{"could not add: " + ec.message()};
        handles_[id] = h;
        ids_[h] = id;
        seed_[id] = seed;
        if (!part_suffix.empty()) part_suffix_[id] = part_suffix;
        if (!root_name.empty()) root_name_[id] = root_name;
        if (hold) held_.insert(id);
        if (finalized) finalized_.insert(id);
        if (check_finished) rechecking_finished_.insert(id);
        if (!has_metadata) awaiting_check_.insert(id);
        // A new torrent first checks its resume data, and libtorrent ignores force_recheck until that is done, so the
        // recheck waits for torrent_checked_alert. The reply says it is queued (a 0.3 worker ignored the field).
        json::object extra;
        if (recheck_after && has_metadata) {
            pending_recheck_.insert(id);
            extra["recheckQueued"] = true;
        }
        reply(cmd, true, {}, std::move(extra));
        if (h.torrent_file()) send_metadata(id, h);
    }

    void check_metadata(lt::torrent_info const& ti)
    {
        auto const& fs = ti.layout();
        if (fs.num_files() > limits_.max_files) throw BadRequest{"too many files in the torrent"};
        for (auto const i : fs.file_range()) {
            if (fs.pad_file_at(i)) continue;
            if (!is_safe_relative_path(fs.file_path(i))) throw BadRequest{"unsafe file path in the torrent"};
        }
    }

    void remove(json::object const& cmd)
    {
        auto const id = get_string(cmd, "id");
        auto it = handles_.find(id);
        if (it == handles_.end()) {
            reply(cmd, false, "unknown torrent");
            return;
        }
        lt::remove_flags_t flags{};
        if (get_bool(cmd, "deleteFiles", false)) flags |= lt::session_handle::delete_files;
        session_->remove_torrent(it->second, flags);
        forget(id);
        reply(cmd, true);
    }

    void forget(std::string const& id)
    {
        auto it = handles_.find(id);
        if (it == handles_.end()) return;
        ids_.erase(it->second);
        handles_.erase(it);
        seed_.erase(id);
        finished_.erase(id);
        manual_peers_.erase(id);
        outstanding_saves_.erase(id);
        awaiting_check_.erase(id);
        part_suffix_.erase(id);
        root_name_.erase(id);
        held_.erase(id);
        finalized_.erase(id);
        hash_failures_.erase(id);
        pending_recheck_.erase(id);
        rechecking_finished_.erase(id);
        if (auto f = finalizing_.find(id); f != finalizing_.end()) {
            send_reply(f->second.seq, false, "the torrent was removed before its files were renamed");
            finalizing_.erase(f);
        }
    }

    void set_file_priorities(json::object const& cmd)
    {
        auto prios = get_priorities(cmd, "priorities");
        auto const id = get_string(cmd, "id");
        with_torrent(cmd, [&](lt::torrent_handle& h) {
            h.prioritize_files(prios);
            // The choice a held torrent waited for: it may download now (unless its file list is still unchecked).
            if (held_.erase(id) && !awaiting_check_.count(id)) h.unset_flags(lt::torrent_flags::upload_mode);
        });
    }

    // Renames every wanted file from its part name back to its own name; replies when libtorrent has done them all.
    void finalize(json::object const& cmd)
    {
        auto const id = get_string(cmd, "id");
        auto it = handles_.find(id);
        if (it == handles_.end() || !it->second.is_valid()) {
            reply(cmd, false, "unknown torrent");
            return;
        }
        if (finalizing_.count(id)) throw BadRequest{"already finalizing"};
        auto& h = it->second;
        auto const ti = h.torrent_file();
        if (!ti) throw BadRequest{"the torrent has no file list yet"};
        // rootName here replaces the one given on add: the Host picks the final top-level name at the end, when it
        // knows which names are taken.
        auto const new_root = get_string(cmd, "rootName", false);
        if (!new_root.empty() && (new_root.find_first_of("/\\") != std::string::npos || !is_safe_relative_path(new_root)))
            throw BadRequest{"rootName must be one safe name"};
        auto const suffix_it = part_suffix_.find(id);
        auto const suffix = suffix_it == part_suffix_.end() ? std::string() : suffix_it->second;
        auto const prios = h.get_file_priorities();
        auto const save_path = h.status(lt::torrent_handle::query_save_path).save_path;
        auto const& fs = ti->layout();
        int pending = 0;
        if (!suffix.empty() && !finalized_.count(id)) {
            auto const old_root = root_name_.find(id) == root_name_.end() ? std::string() : root_name_[id];
            if (!new_root.empty()) root_name_[id] = new_root;
            // torrent_file() keeps the names from the metadata (libtorrent 2 does not write renames back into it), so
            // the final names are worked out again from those. Nothing is renamed over a file or folder that is
            // already there (a plain rename would replace a file on Linux); the reply then names it and nothing moves.
            std::vector<std::pair<lt::file_index_t, std::string>> targets;
            for (auto const i : fs.file_range()) {
                if (fs.pad_file_at(i)) continue;
                auto const idx = static_cast<std::size_t>(static_cast<int>(i));
                if (idx < prios.size() && prios[idx] == lt::dont_download) continue;
                auto target = planned_for(id, fs.file_path(i), ti->name(), false);
                std::error_code ec;
                if (std::filesystem::exists(disk_path(save_path, target), ec)) {
                    if (old_root.empty()) root_name_.erase(id);
                    else root_name_[id] = old_root;
                    reply(cmd, false, "already there: " + target);
                    return;
                }
                targets.emplace_back(i, std::move(target));
            }
            for (auto& [i, target] : targets) {
                h.rename_file(i, target);
                ++pending;
            }
        }
        finalized_.insert(id);
        if (pending == 0) {
            reply(cmd, true);
            return;
        }
        Finalizing f;
        if (auto const* seq = cmd.if_contains("seq")) f.seq = *seq;
        f.pending = pending;
        finalizing_[id] = std::move(f);
    }

    void finalize_step(std::string const& id, std::string const* failure)
    {
        auto f = finalizing_.find(id);
        if (f == finalizing_.end()) return;
        if (failure && f->second.failure.empty()) f->second.failure = *failure;
        if (--f->second.pending > 0) return;
        auto const seq = f->second.seq;
        auto const message = f->second.failure;
        finalizing_.erase(f);
        send_reply(seq, message.empty(), message);
        if (auto h = handles_.find(id); h != handles_.end()) request_save(h->second);
    }

    void set_limits(json::object const& cmd)
    {
        lt::settings_pack p;
        p.set_int(lt::settings_pack::download_rate_limit, int(std::clamp<std::int64_t>(get_int(cmd, "download", 0), 0, INT32_MAX)));
        p.set_int(lt::settings_pack::upload_rate_limit, int(std::clamp<std::int64_t>(get_int(cmd, "upload", 0), 0, INT32_MAX)));
        auto const connections = get_int(cmd, "connections", -1);
        // As setPeerLimits: past this it is no limit a person meant, and int(c) would wrap.
        if (connections > 100000) throw BadRequest{"limit out of range"};
        if (connections > 0) p.set_int(lt::settings_pack::connections_limit, int(connections));
        session_->apply_settings(std::move(p));
        reply(cmd, true);
    }

    void set_torrent_limits(json::object const& cmd)
    {
        auto const down = int(std::clamp<std::int64_t>(get_int(cmd, "download", 0), 0, INT32_MAX));
        auto const up = int(std::clamp<std::int64_t>(get_int(cmd, "upload", 0), 0, INT32_MAX));
        with_torrent(cmd, [&](lt::torrent_handle& h) {
            h.set_download_limit(down == 0 ? -1 : down);
            h.set_upload_limit(up == 0 ? -1 : up);
        });
    }

    void set_seed_policy(json::object const& cmd)
    {
        seed_ratio_ = get_double(cmd, "ratio", 0);
        seed_seconds_ = get_int(cmd, "seconds", 0);
        reply(cmd, true);
    }

    void set_listen(json::object const& cmd)
    {
        auto const port = get_int(cmd, "port", 0);
        if (port < 0 || port > 65535) throw BadRequest{"port must be 0 to 65535"};
        std::string interfaces;
        if (auto const* v = cmd.if_contains("interfaces"); v && v->is_array()) {
            for (auto const& e : v->as_array()) {
                if (!e.is_string()) throw BadRequest{"interfaces must be a list of addresses"};
                std::string addr(e.as_string());
                // Must be one IP address (IPv6 in brackets): this text is spliced into libtorrent's list.
                std::string bare = addr;
                bool const bracketed = bare.size() > 2 && bare.front() == '[' && bare.back() == ']';
                if (bracketed) bare = bare.substr(1, bare.size() - 2);
                boost::system::error_code aec;
                auto const parsed = boost::asio::ip::make_address(bare, aec);
                if (aec || parsed.is_v6() != bracketed) throw BadRequest{"interfaces must be IP addresses, IPv6 in brackets"};
                if (!interfaces.empty()) interfaces += ',';
                interfaces += addr + ':' + std::to_string(port);
            }
        } else {
            interfaces = "0.0.0.0:" + std::to_string(port) + ",[::]:" + std::to_string(port);
        }
        lt::settings_pack p;
        p.set_str(lt::settings_pack::listen_interfaces, interfaces);
        // Peer alerts only until the first incoming connection is seen (the port hint), then off again.
        if (!incoming_seen_) p.set_int(lt::settings_pack::alert_mask, base_alert_mask() | lt::alert_category::peer);
        dht_ = get_bool(cmd, "dht", true);
        lsd_ = get_bool(cmd, "lsd", false);
        bool const pex = get_bool(cmd, "pex", true);
        // With a proxy that carries no UDP (HTTP, SOCKS4), DHT and LSD stay off: they would go around it.
        p.set_bool(lt::settings_pack::enable_dht, dht_ && !proxy_blocks_udp_);
        p.set_bool(lt::settings_pack::enable_lsd, lsd_ && !proxy_blocks_udp_);
        p.set_bool(lt::settings_pack::enable_upnp, get_bool(cmd, "upnp", false));
        p.set_bool(lt::settings_pack::enable_natpmp, get_bool(cmd, "natpmp", false));
        auto const enc = get_string(cmd, "encryption", false);
        int policy = lt::settings_pack::pe_enabled;
        if (enc == "forced") policy = lt::settings_pack::pe_forced;
        else if (enc == "disabled") policy = lt::settings_pack::pe_disabled;
        else if (!enc.empty() && enc != "enabled") throw BadRequest{"encryption is enabled, forced or disabled"};
        p.set_int(lt::settings_pack::out_enc_policy, policy);
        p.set_int(lt::settings_pack::in_enc_policy, policy);
        session_->apply_settings(std::move(p));
        // New listen addresses mean new DHT nodes: the next count starts afresh.
        dht_nodes_by_.clear();
        dht_nodes_ = 0;
        if (pex != pex_) {
            pex_ = pex;
            for (auto& [tid, h] : handles_) {
                if (!h.is_valid()) continue;
                if (pex_) h.unset_flags(lt::torrent_flags::disable_pex);
                else h.set_flags(lt::torrent_flags::disable_pex);
            }
        }
        reply(cmd, true);
    }

    void set_progress_interval(json::object const& cmd)
    {
        auto const ms = get_int(cmd, "ms", 1000);
        if (ms < 100 || ms > 60000) throw BadRequest{"ms must be 100 to 60000"};
        progress_interval_ = std::chrono::milliseconds(ms);
        reply(cmd, true);
    }

    void add_peer(json::object const& cmd)
    {
        auto const address = get_string(cmd, "address");
        auto const port = get_int(cmd, "port", 0);
        if (port <= 0 || port > 65535) throw BadRequest{"port must be 1 to 65535"};
        boost::system::error_code ec;
        auto const ip = boost::asio::ip::make_address(address, ec);
        if (ec) throw BadRequest{"address is not an IP address"};
        lt::tcp::endpoint const endpoint(ip, static_cast<std::uint16_t>(port));
        with_torrent(cmd, [&](lt::torrent_handle& h) {
            // A peer given while the torrent is still checking its files is dropped by libtorrent, so it is kept
            // and connected again when the check ends (torrent_checked_alert).
            auto& peers = manual_peers_[get_string(cmd, "id")];
            if (peers.size() < 64) peers.push_back(endpoint);
            h.connect_peer(endpoint);
        });
    }

    // Bytes of each file on disk so far (whole pieces only), by file index; empty until the metadata is in.
    void files(json::object const& cmd)
    {
        auto const id = get_string(cmd, "id");
        auto it = handles_.find(id);
        if (it == handles_.end() || !it->second.is_valid()) {
            reply(cmd, false, "unknown torrent");
            return;
        }
        std::vector<std::int64_t> progress;
        it->second.file_progress(progress, lt::torrent_handle::piece_granularity);
        json::array done;
        done.reserve(progress.size());
        for (auto const bytes : progress) done.emplace_back(bytes);
        json::object extra;
        extra["done"] = std::move(done);
        reply(cmd, true, {}, std::move(extra));
    }

    // Keeps a finished torrent seeding (on) or stops it (off). A finished torrent that was paused because it had no
    // seeding goes on; stopping pauses it gracefully and asks for a resume blob.
    void set_seed(json::object const& cmd)
    {
        auto const id = get_string(cmd, "id");
        bool const on = get_bool(cmd, "on", true);
        with_torrent(cmd, [&](lt::torrent_handle& h) {
            seed_[id] = on;
            auto const st = h.status({});
            bool const done = st.is_finished || st.is_seeding || finished_.count(id) > 0;
            if (!done) return;
            if (on) {
                if (st.flags & lt::torrent_flags::paused) h.resume();
            } else {
                stop_managing(h);
                h.pause(lt::torrent_handle::graceful_pause);
                request_save(h);
            }
        });
    }

    lt::torrent_handle const* find_torrent(json::object const& cmd)
    {
        auto const id = get_string(cmd, "id");
        auto it = handles_.find(id);
        if (it == handles_.end() || !it->second.is_valid()) {
            reply(cmd, false, "unknown torrent");
            return nullptr;
        }
        return &it->second;
    }

    // The connected peers (at most 500): address, client, flags, rates and how much of the torrent each has.
    void peers(json::object const& cmd)
    {
        auto const* h = find_torrent(cmd);
        if (!h) return;
        std::vector<lt::peer_info> list;
        h->get_peer_info(list);
        json::array out;
        for (auto const& p : list) {
            if (out.size() >= 500) break;
            if (p.flags & (lt::peer_info::connecting | lt::peer_info::handshake)) continue;
            json::object o;
            o["address"] = p.remote_endpoint().address().to_string();
            o["port"] = int(p.remote_endpoint().port());
            o["client"] = p.client;
            json::array flags;
            if (p.flags & lt::peer_info::seed) flags.emplace_back("seed");
            flags.emplace_back((p.source & lt::peer_info::incoming) ? "incoming" : "outgoing");
            if (p.flags & (lt::peer_info::rc4_encrypted | lt::peer_info::plaintext_encrypted)) flags.emplace_back("encrypted");
            if (p.flags & lt::peer_info::utp_socket) flags.emplace_back("utp");
            if (p.flags & lt::peer_info::interesting) flags.emplace_back("interested");
            if (p.flags & lt::peer_info::remote_interested) flags.emplace_back("peerInterested");
            if (p.flags & lt::peer_info::choked) flags.emplace_back("choked");
            if (p.flags & lt::peer_info::remote_choked) flags.emplace_back("chokedByPeer");
            if (p.flags & lt::peer_info::snubbed) flags.emplace_back("snubbed");
            o["flags"] = std::move(flags);
            o["downRate"] = p.payload_down_speed;
            o["upRate"] = p.payload_up_speed;
            o["downloaded"] = p.total_download;
            o["uploaded"] = p.total_upload;
            o["progress"] = double(p.progress);
            out.push_back(std::move(o));
        }
        json::object extra;
        extra["peers"] = std::move(out);
        reply(cmd, true, {}, std::move(extra));
    }

    // The torrent's trackers: url, tier, status (notContacted, updating, working, error), message, seeds and peers
    // (-1 when the tracker did not say).
    void trackers(json::object const& cmd)
    {
        auto const* h = find_torrent(cmd);
        if (!h) return;
        json::array out;
        for (auto const& t : h->trackers()) {
            json::object o;
            o["url"] = t.url;
            o["tier"] = int(t.tier);
            bool updating = false, working = false, failed = false;
            std::string message;
            int seeds = -1, peers = -1;
            // T3: seconds until the next announce to this tracker (0: due now or under way; -1: none planned).
            std::int64_t next = -1;
            auto const now = lt::clock_type::now();
            for (auto const& ep : t.endpoints) {
                if (!ep.enabled) continue;
                for (auto const& ih : ep.info_hashes) {
                    if (ih.next_announce > lt::time_point32::min()) {
                        auto const left = std::chrono::duration_cast<std::chrono::seconds>(ih.next_announce - now).count();
                        auto const clamped = std::max<std::int64_t>(0, left);
                        next = next < 0 ? clamped : std::min(next, clamped);
                    }
                }
            }
            for (auto const& ep : t.endpoints) {
                for (auto const& ih : ep.info_hashes) {
                    if (ih.updating) updating = true;
                    if (ih.fails > 0 || ih.last_error) {
                        failed = true;
                        if (message.empty()) message = ih.last_error ? ih.last_error.message() : ih.message;
                    } else if (ih.start_sent) {
                        working = true;
                    }
                    if (message.empty() && !ih.message.empty()) message = ih.message;
                    seeds = std::max(seeds, ih.scrape_complete);
                    peers = std::max(peers, ih.scrape_incomplete);
                }
            }
            o["status"] = updating ? "updating" : working ? "working" : failed ? "error" : "notContacted";
            o["message"] = message;
            o["seeds"] = seeds;
            o["peers"] = peers;
            o["nextAnnounce"] = next;
            out.push_back(std::move(o));
        }
        json::object extra;
        extra["trackers"] = std::move(out);
        reply(cmd, true, {}, std::move(extra));
    }

    // Which pieces the torrent has: count, pieceLength, have, and ranges ([first, last] pairs) or a base64 bitmap.
    void pieces(json::object const& cmd)
    {
        auto const* h = find_torrent(cmd);
        if (!h) return;
        auto const st = h->status(lt::torrent_handle::query_pieces);
        auto const ti = h->torrent_file();
        int const count = ti ? ti->num_pieces() : 0;
        auto const& bits = st.pieces;
        auto const summary = summarize_pieces(count, [&](int i) {
            return i < bits.size() && bits.get_bit(lt::piece_index_t(i));
        });
        json::object extra;
        extra["count"] = summary.count;
        extra["pieceLength"] = ti ? ti->piece_length() : 0;
        extra["have"] = summary.have;
        if (summary.bitmap.empty()) {
            json::array ranges;
            for (auto const& [first, last] : summary.ranges) ranges.push_back(json::array{first, last});
            extra["ranges"] = std::move(ranges);
        } else {
            extra["bitmap"] = summary.bitmap;
        }
        reply(cmd, true, {}, std::move(extra));
    }

    void stats(json::object const& cmd)
    {
        json::object extra;
        extra["listenPort"] = session_->is_listening() ? int(session_->listen_port()) : 0;
        extra["torrents"] = handles_.size();
        // T3: connections in all, as in force (setPeerLimits connections; libtorrent's default after 0).
        extra["connectionsLimit"] = session_->get_settings().get_int(lt::settings_pack::connections_limit);
        // T3: DHT nodes known at the last count (0 before any; -1 with DHT off); each stats asks for a fresh count.
        extra["dhtNodes"] = dht_enabled() ? dht_nodes_ : -1;
        if (dht_enabled()) session_->post_dht_stats();
        reply(cmd, true, {}, std::move(extra));
    }

    bool dht_enabled() const { return dht_ && !proxy_blocks_udp_; }

    // A torrent the Host pauses or stops seeding leaves libtorrent's seeding queue, so the queue never resumes it.
    static void stop_managing(lt::torrent_handle const& h)
    {
        if (h.is_valid() && (h.flags() & lt::torrent_flags::auto_managed)) h.unset_flags(lt::torrent_flags::auto_managed);
    }

    // T3: checks every piece on disk again. The state reads "checking" until it is done.
    // A finished torrent (its files have their final names) checks in upload mode, so a piece found damaged is not
    // downloaded again into the finished file; torrent_checked_alert then lets it seed on, or pauses it and reports
    // an error.
    void recheck(json::object const& cmd)
    {
        auto const id = get_string(cmd, "id");
        with_torrent(cmd, [&](lt::torrent_handle& h) {
            if (!h.torrent_file()) throw BadRequest{"the torrent has no file list yet"};
            // Still in the add's own check: the full check asked for with the add follows it anyway.
            if (pending_recheck_.count(id)) return;
            if (finalized_.count(id)) {
                rechecking_finished_.insert(id);
                h.set_flags(lt::torrent_flags::upload_mode);
            }
            h.force_recheck();
        });
    }

    // T3: announces to every tracker now (minimum intervals ignored) and to the DHT when it is on.
    void announce(json::object const& cmd)
    {
        with_torrent(cmd, [this](lt::torrent_handle& h) {
            h.force_reannounce(0, -1, lt::torrent_handle::ignore_min_interval);
            if (dht_enabled()) h.force_dht_announce();
        });
    }

    // T3: download in order (streaming) or rarest first.
    void set_sequential(json::object const& cmd)
    {
        bool const on = get_bool(cmd, "on", true);
        with_torrent(cmd, [&](lt::torrent_handle& h) {
            if (on) h.set_flags(lt::torrent_flags::sequential_download);
            else h.unset_flags(lt::torrent_flags::sequential_download);
        });
    }

    template <typename Hash>
    static std::string hex_of(Hash const& hash)
    {
        std::ostringstream text;
        text << hash;
        return text.str();
    }

    // T3: the facts of the Overview tab that libtorrent knows cheaply.
    void details(json::object const& cmd)
    {
        auto const* h = find_torrent(cmd);
        if (!h) return;
        auto const id = get_string(cmd, "id");
        auto const st = h->status(lt::torrent_handle::query_distributed_copies);
        auto const ti = h->torrent_file();
        auto const hashes = h->info_hashes();
        json::object extra;
        extra["infoHashV1"] = hashes.has_v1() ? hex_of(hashes.v1) : std::string();
        extra["infoHashV2"] = hashes.has_v2() ? hex_of(hashes.v2) : std::string();
        extra["private"] = ti ? ti->priv() : false;
        extra["pieces"] = ti ? ti->num_pieces() : 0;
        extra["pieceLength"] = ti ? ti->piece_length() : 0;
        extra["wasted"] = st.total_redundant_bytes + st.total_failed_bytes;
        extra["failedBytes"] = st.total_failed_bytes;
        auto const failures = hash_failures_.find(id);
        extra["hashFailures"] = failures == hash_failures_.end() ? std::int64_t(0) : failures->second;
        extra["connections"] = st.num_connections;
        extra["connectionsLimit"] = st.connections_limit;
        extra["downloadLimit"] = std::max(0, h->download_limit());
        extra["uploadLimit"] = std::max(0, h->upload_limit());
        extra["sequential"] = bool(st.flags & lt::torrent_flags::sequential_download);
        extra["pex"] = !(st.flags & lt::torrent_flags::disable_pex) && !(ti && ti->priv());
        extra["distributedCopies"] = double(st.distributed_copies);
        // libtorrent gives 0 both when an announce is due and while paused; a paused torrent announces nothing (-1).
        extra["nextAnnounce"] = (st.flags & lt::torrent_flags::paused)
            ? std::int64_t(-1)
            : std::int64_t(std::chrono::duration_cast<std::chrono::seconds>(st.next_announce).count());
        extra["addedAt"] = std::int64_t(st.added_time);
        extra["activeSeconds"] = std::int64_t(st.active_duration.count());
        extra["dhtNodes"] = dht_enabled() ? dht_nodes_ : -1;
        if (dht_enabled()) session_->post_dht_stats();
        reply(cmd, true, {}, std::move(extra));
    }

    // T3: connections in all (connections, 0 = libtorrent's), per torrent (connectionsPerTorrent, 0 = no limit of its
    // own) and how many finished torrents seed at once (activeSeeds, 0 = all). A field left out is left as it is.
    void set_peer_limits(json::object const& cmd)
    {
        auto const connections = get_int(cmd, "connections", -1);
        auto const per_torrent = get_int(cmd, "connectionsPerTorrent", -1);
        auto const seeds = get_int(cmd, "activeSeeds", -1);
        if (connections > 100000 || per_torrent > 100000 || seeds > 10000) throw BadRequest{"limit out of range"};
        lt::settings_pack p;
        // 0 puts libtorrent's own default back, also on a worker that had another limit.
        if (connections > 0) p.set_int(lt::settings_pack::connections_limit, int(connections));
        else if (connections == 0)
            p.set_int(lt::settings_pack::connections_limit,
                lt::default_settings().get_int(lt::settings_pack::connections_limit));
        if (seeds >= 0) {
            active_seeds_ = int(seeds);
            p.set_int(lt::settings_pack::active_seeds, active_seeds_ > 0 ? active_seeds_ : -1);
            // Every seed counts, also one that uploads nothing now, so the number is what "at once" says.
            p.set_bool(lt::settings_pack::dont_count_slow_torrents, false);
        }
        session_->apply_settings(std::move(p));
        if (per_torrent >= 0) {
            torrent_connections_ = int(per_torrent);
            for (auto& [tid, h] : handles_)
                if (h.is_valid()) h.set_max_connections(torrent_connections_ > 0 ? torrent_connections_ : 0xffffff);
        }
        if (seeds >= 0) {
            for (auto& [tid, h] : handles_) {
                if (!h.is_valid() || !seed_[tid] || !finalized_.count(tid)) continue;
                auto const flags = h.flags();
                if (active_seeds_ > 0) {
                    // Only a seed that runs joins the queue; one the Host paused stays paused.
                    if (!(flags & lt::torrent_flags::paused)) h.set_flags(lt::torrent_flags::auto_managed);
                } else if (flags & lt::torrent_flags::auto_managed) {
                    h.unset_flags(lt::torrent_flags::auto_managed);
                    if (flags & lt::torrent_flags::paused) h.resume();
                }
            }
        }
        reply(cmd, true);
    }

    // T3: the proxy for peers, trackers and host names. type: none, http, socks4 or socks5. With http or socks4 (no
    // UDP through them) DHT, LSD and uTP are turned off, so nothing goes around the proxy over UDP.
    void set_proxy(json::object const& cmd)
    {
        auto const type = get_string(cmd, "type", false);
        lt::settings_pack p;
        bool blocks_udp = false;
        if (type.empty() || type == "none") {
            p.set_int(lt::settings_pack::proxy_type, lt::settings_pack::none);
        } else {
            auto const host = get_string(cmd, "host");
            auto const port = get_int(cmd, "port", 0);
            if (host.empty()) throw BadRequest{"host is empty"};
            if (port <= 0 || port > 65535) throw BadRequest{"port must be 1 to 65535"};
            auto const user = get_string(cmd, "username", false);
            auto const password = get_string(cmd, "password", false);
            int kind;
            if (type == "http") { kind = user.empty() ? lt::settings_pack::http : lt::settings_pack::http_pw; blocks_udp = true; }
            else if (type == "socks4") { kind = lt::settings_pack::socks4; blocks_udp = true; }
            else if (type == "socks5") kind = user.empty() ? lt::settings_pack::socks5 : lt::settings_pack::socks5_pw;
            else throw BadRequest{"type must be none, http, socks4 or socks5"};
            p.set_int(lt::settings_pack::proxy_type, kind);
            p.set_str(lt::settings_pack::proxy_hostname, host);
            p.set_int(lt::settings_pack::proxy_port, int(port));
            p.set_str(lt::settings_pack::proxy_username, user);
            p.set_str(lt::settings_pack::proxy_password, password);
            p.set_bool(lt::settings_pack::proxy_peer_connections, true);
            p.set_bool(lt::settings_pack::proxy_tracker_connections, true);
            p.set_bool(lt::settings_pack::proxy_hostnames, true);
        }
        proxy_blocks_udp_ = blocks_udp;
        p.set_bool(lt::settings_pack::enable_outgoing_utp, !blocks_udp);
        p.set_bool(lt::settings_pack::enable_incoming_utp, !blocks_udp);
        p.set_bool(lt::settings_pack::enable_dht, dht_ && !blocks_udp);
        p.set_bool(lt::settings_pack::enable_lsd, lsd_ && !blocks_udp);
        session_->apply_settings(std::move(p));
        // The address and password are never logged.
        wlog("proxy: %s", type.empty() ? "none" : type.c_str());
        reply(cmd, true);
    }

    // --- alerts --------------------------------------------------------------------------------------------------

    std::string const* id_of(lt::torrent_handle const& h) const
    {
        auto it = ids_.find(h);
        return it == ids_.end() ? nullptr : &it->second;
    }

    void request_save(lt::torrent_handle const& h, lt::resume_data_flags_t flags = {})
    {
        auto const* id = id_of(h);
        if (!id || !h.is_valid()) return;
        ++outstanding_saves_[*id];
        h.save_resume_data(flags | lt::torrent_handle::save_info_dict);
    }

    void save_done(std::string const& id)
    {
        auto it = outstanding_saves_.find(id);
        if (it != outstanding_saves_.end() && --it->second <= 0) outstanding_saves_.erase(it);
    }

    void send_metadata(std::string const& id, lt::torrent_handle const& h)
    {
        auto ti = h.torrent_file();
        if (!ti) return;
        try {
            check_metadata(*ti);
        } catch (BadRequest const& e) {
            send_error(&id, "metadata", e.message);
            // A magnet is in upload mode until here, so nothing should exist; delete anything libtorrent made anyway.
            session_->remove_torrent(h, lt::session_handle::delete_files);
            forget(id);
            return;
        }
        if (auto it = awaiting_check_.find(id); it != awaiting_check_.end()) {
            // A magnet's files get their download names now, before upload mode is lifted and anything is written.
            auto const& lay = ti->layout();
            for (auto const i : lay.file_range()) {
                if (lay.pad_file_at(i)) continue;
                auto planned = planned_for(id, lay.file_path(i), ti->name(), true);
                if (planned != lay.file_path(i)) h.rename_file(i, planned);
            }
            awaiting_check_.erase(it);
            if (!held_.count(id)) h.unset_flags(lt::torrent_flags::upload_mode);
        }
        json::object m;
        m["event"] = "metadata";
        m["id"] = id;
        m["name"] = ti->name();
        m["totalSize"] = ti->total_size();
        m["pieceLength"] = ti->piece_length();
        json::array files;
        auto const& fs = ti->layout();
        for (auto const i : fs.file_range()) {
            json::object f;
            f["index"] = static_cast<int>(i);
            // The name the file has once it is done: its current name without the part suffix, or (a magnet whose
            // renames are still on their way) the name planned for it.
            std::string path = fs.file_path(i);
            auto const sfx = part_suffix_.find(id);
            if (sfx != part_suffix_.end() && path.size() > sfx->second.size()
                && path.compare(path.size() - sfx->second.size(), sfx->second.size(), sfx->second) == 0)
                path.resize(path.size() - sfx->second.size());
            else path = planned_for(id, path, ti->name(), false);
            f["path"] = path;
            f["size"] = fs.file_size(i);
            if (fs.pad_file_at(i)) f["pad"] = true;
            files.push_back(std::move(f));
        }
        m["files"] = std::move(files);
        if (held_.count(id)) m["held"] = true;
        out_.send(m);
        // Hand the Host a blob that carries the metadata, so a restart needs no swarm to find it again.
        request_save(h);
    }

    // What the Host can tell the user in plain words, whatever language the system's message is in.
    static char const* error_reason(lt::error_code const& ec)
    {
        namespace errc = boost::system::errc;
        if (ec == errc::no_space_on_device) return "diskFull";
        if (ec == errc::permission_denied || ec == errc::operation_not_permitted || ec == errc::read_only_file_system)
            return "permission";
        if (ec == errc::filename_too_long) return "pathTooLong";
        return nullptr;
    }

    void send_error(std::string const* id, char const* kind, std::string const& message, lt::error_code const* ec = nullptr)
    {
        json::object e;
        e["event"] = "error";
        if (id) e["id"] = *id;
        e["kind"] = kind;
        e["message"] = message;
        if (ec) {
            if (auto const* reason = error_reason(*ec)) e["reason"] = reason;
        }
        out_.send(e);
    }

    void send_state(std::string const& id, char const* state)
    {
        json::object e;
        e["event"] = "state";
        e["id"] = id;
        e["state"] = state;
        out_.send(e);
    }

    void drain_alerts()
    {
        std::vector<lt::alert*> alerts;
        session_->pop_alerts(&alerts);
        json::array progress;
        for (lt::alert* a : alerts) {
            if (auto* su = lt::alert_cast<lt::state_update_alert>(a)) {
                for (auto const& s : su->status) {
                    auto const* id = id_of(s.handle);
                    if (!id) continue;
                    json::object p;
                    p["id"] = *id;
                    p["state"] = held_.count(*id) && !(s.flags & lt::torrent_flags::paused)
                        && s.state != lt::torrent_status::downloading_metadata ? "held" : state_name(s);
                    p["done"] = s.total_wanted_done;
                    p["wanted"] = s.total_wanted;
                    p["downRate"] = s.download_payload_rate;
                    p["upRate"] = s.upload_payload_rate;
                    p["peers"] = s.num_peers;
                    p["seeds"] = s.num_seeds;
                    p["uploaded"] = s.all_time_upload;
                    p["downloaded"] = s.all_time_download;
                    p["progress"] = s.progress;
                    // Seeding (T2): uploaded against what this torrent downloaded (its wanted size), and seconds spent seeding.
                    p["ratio"] = s.total_wanted > 0 ? double(s.all_time_upload) / double(s.total_wanted) : 0.0;
                    p["seedingSeconds"] = std::int64_t(s.seeding_duration.count());
                    progress.push_back(std::move(p));
                    check_seed_policy(*id, s);
                }
            } else if (auto* md = lt::alert_cast<lt::metadata_received_alert>(a)) {
                if (auto const* id = id_of(md->handle)) send_metadata(std::string(*id), md->handle);
            } else if (auto* fin = lt::alert_cast<lt::torrent_finished_alert>(a)) {
                auto const* idp = id_of(fin->handle);
                if (!idp) continue;
                std::string const id = *idp;
                // Added with recheck: this is the resume data talking, which the check is about to question. The check
                // that follows says "finished" again if it is.
                if (pending_recheck_.count(id)) continue;
                if (finished_.insert(id).second) {
                    json::object c;
                    c["event"] = "completed";
                    c["id"] = id;
                    out_.send(c);
                }
                // "Completed" means the download finished. Without seeding the torrent stops here.
                if (!seed_[id]) {
                    stop_managing(fin->handle);
                    fin->handle.pause();
                    send_state(id, "paused");
                }
                request_save(fin->handle);
            } else if (auto* rd = lt::alert_cast<lt::save_resume_data_alert>(a)) {
                auto const* id = id_of(rd->handle);
                if (!id) continue;
                save_done(*id);
                auto const buf = lt::write_resume_data_buf(rd->params);
                json::object b;
                b["event"] = "resumeBlob";
                b["id"] = *id;
                b["data"] = base64_encode(buf.data(), buf.size());
                out_.send(b);
            } else if (auto* rf = lt::alert_cast<lt::save_resume_data_failed_alert>(a)) {
                if (auto const* id = id_of(rf->handle)) save_done(*id);
            } else if (auto* te = lt::alert_cast<lt::torrent_error_alert>(a)) {
                send_error(id_of(te->handle), "torrent", te->error.message(), &te->error);
            } else if (auto* fe = lt::alert_cast<lt::file_error_alert>(a)) {
                send_error(id_of(fe->handle), "file", fe->error.message(), &fe->error);
            } else if (auto* lf = lt::alert_cast<lt::listen_failed_alert>(a)) {
                send_error(nullptr, "listen", lf->error.message());
            } else if (auto* ls = lt::alert_cast<lt::listen_succeeded_alert>(a)) {
                json::object s;
                s["event"] = "stats";
                s["listenPort"] = ls->port;
                out_.send(s);
            } else if (lt::alert_cast<lt::incoming_connection_alert>(a)) {
                if (!incoming_seen_) {
                    incoming_seen_ = true;
                    json::object s;
                    s["event"] = "stats";
                    s["incoming"] = true;
                    out_.send(s);
                    lt::settings_pack quiet;
                    quiet.set_int(lt::settings_pack::alert_mask, base_alert_mask());
                    session_->apply_settings(std::move(quiet));
                }
            } else if (auto* pm = lt::alert_cast<lt::portmap_alert>(a)) {
                json::object s;
                s["event"] = "stats";
                s["mappedPort"] = pm->external_port;
                out_.send(s);
            } else if (auto* tc = lt::alert_cast<lt::torrent_checked_alert>(a)) {
                if (auto const* idp = id_of(tc->handle)) {
                    std::string const id = *idp;
                    if (pending_recheck_.erase(id)) {
                        // The add's own check is done; now the full check the Host asked for.
                        tc->handle.force_recheck();
                        continue;
                    }
                    if (rechecking_finished_.erase(id)) finished_check_done(id, tc->handle);
                    if (auto it = manual_peers_.find(id); it != manual_peers_.end())
                        for (auto const& ep : it->second) tc->handle.connect_peer(ep);
                }
            } else if (auto* fr = lt::alert_cast<lt::file_renamed_alert>(a)) {
                if (auto const* id = id_of(fr->handle)) finalize_step(std::string(*id), nullptr);
            } else if (auto* ff = lt::alert_cast<lt::file_rename_failed_alert>(a)) {
                if (auto const* id = id_of(ff->handle)) {
                    auto const why = "could not rename a file: " + ff->error.message();
                    finalize_step(std::string(*id), &why);
                }
            } else if (auto* hf = lt::alert_cast<lt::hash_failed_alert>(a)) {
                if (auto const* id = id_of(hf->handle)) ++hash_failures_[*id];
            } else if (auto* ds = lt::alert_cast<lt::dht_stats_alert>(a)) {
                // One alert per DHT node (one per listen address, IPv4 and IPv6), or one empty alert when none runs:
                // each node's count is kept by its address and dhtNodes is their sum.
                std::int64_t nodes = 0;
                for (auto const& bucket : ds->routing_table) nodes += bucket.num_nodes;
                if (ds->routing_table.empty() && ds->local_endpoint == lt::udp::endpoint{}) dht_nodes_by_.clear();
                else dht_nodes_by_[ds->local_endpoint] = nodes;
                dht_nodes_ = 0;
                for (auto const& [ep, n] : dht_nodes_by_) dht_nodes_ += n;
            } else if (auto* sc = lt::alert_cast<lt::state_changed_alert>(a)) {
                (void)sc; // state is reported with progress; avoid a second channel for the same fact
            }
        }
        if (!progress.empty()) {
            json::object p;
            p["event"] = "progress";
            p["torrents"] = std::move(progress);
            out_.send(p);
        }
    }

    // T3: a finished torrent's recheck is done. All its wanted pieces good: it goes on as before. Some damaged or
    // missing: it stays paused (it never downloads into a finished file) and the Host hears an error.
    void finished_check_done(std::string const& id, lt::torrent_handle const& h)
    {
        auto const st = h.status({});
        if (st.is_finished) {
            if (!held_.count(id)) h.unset_flags(lt::torrent_flags::upload_mode);
            return;
        }
        stop_managing(h);
        h.pause();
        finished_.erase(id);
        request_save(h);
        send_error(&id, "torrent", "the file check found damaged or missing pieces");
    }

    void check_seed_policy(std::string const& id, lt::torrent_status const& s)
    {
        if (!seed_[id] || (s.flags & lt::torrent_flags::paused) || !s.is_seeding) return;
        bool stop = false;
        if (seed_ratio_ > 0 && s.total_wanted > 0
            && double(s.all_time_upload) >= seed_ratio_ * double(std::max<std::int64_t>(s.total_wanted, 1)))
            stop = true;
        if (seed_seconds_ > 0 && s.seeding_duration.count() >= seed_seconds_) stop = true;
        if (stop) {
            stop_managing(s.handle);
            s.handle.pause();
            request_save(s.handle);
            send_state(id, "seedLimitReached");
        }
    }

    Output& out_;
    Inbox& inbox_;
    std::string dht_state_path_;
    bool test_hooks_;
    Limits limits_;
    std::unique_ptr<lt::session> session_;
    std::unordered_map<std::string, lt::torrent_handle> handles_;
    std::unordered_map<lt::torrent_handle, std::string> ids_;
    std::unordered_map<std::string, bool> seed_;
    std::unordered_set<std::string> finished_;
    std::unordered_map<std::string, std::vector<lt::tcp::endpoint>> manual_peers_;
    std::chrono::milliseconds progress_interval_{1000};
    std::chrono::milliseconds resume_interval_{5 * 60 * 1000};
    double seed_ratio_ = 0;
    std::int64_t seed_seconds_ = 0;
    std::unordered_map<std::string, int> outstanding_saves_;
    std::unordered_set<std::string> awaiting_check_; // magnets in upload mode until their file list is checked
    std::unordered_map<std::string, std::string> part_suffix_; // per torrent: added to every file name while it downloads
    std::unordered_map<std::string, std::string> root_name_;   // per torrent: replaces the torrent's own top-level name
    std::unordered_set<std::string> held_;                     // in upload mode until setFilePriorities
    std::unordered_set<std::string> finalized_;                // finalize already renamed the files back
    struct Finalizing {
        json::value seq;
        int pending = 0;
        std::string failure;
    };
    std::unordered_map<std::string, Finalizing> finalizing_;
    bool quit_ = false;
    // T3 session choices: peer exchange, DHT and LSD as setListen asked, connections per torrent (0: no limit of its
    // own), seeds at once (0: all), and whether the proxy carries no UDP.
    bool pex_ = true;
    bool dht_ = false;
    bool lsd_ = false;
    int torrent_connections_ = 0;
    int active_seeds_ = 0;
    bool proxy_blocks_udp_ = false;
    std::int64_t dht_nodes_ = 0;
    std::unordered_map<std::string, std::int64_t> hash_failures_;
    std::map<lt::udp::endpoint, std::int64_t> dht_nodes_by_; // T3: DHT nodes known, per DHT node (listen address)
    std::unordered_set<std::string> pending_recheck_;     // added with recheck: checked fully after the add's own check
    std::unordered_set<std::string> rechecking_finished_; // finished torrents being checked again (in upload mode)
    // A peer connected in once (sent as stats.incoming, once per process).
    bool incoming_seen_ = false;

    static lt::alert_category_t base_alert_mask()
    {
        return lt::alert_category::error | lt::alert_category::status | lt::alert_category::storage
            | lt::alert_category::port_mapping;
    }
};

} // namespace inbound::torrent

int main(int argc, char** argv)
{
#ifdef _WIN32
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
#endif
    std::string dht_state;
    bool test_hooks = false;
    for (int i = 1; i < argc; ++i) {
        std::string_view a = argv[i];
        if (a == "--dht-state" && i + 1 < argc) dht_state = argv[++i];
        else if (a == "--version") {
            std::printf("inbound-torrent %s (libtorrent %s, protocol %d)\n", inbound::torrent::kWorkerVersion,
                libtorrent::version(), inbound::torrent::kProtocolVersion);
            return 0;
        } else {
            std::fprintf(stderr, "usage: inbound-torrent [--dht-state <file>] [--version]\n");
            return 2;
        }
    }
    // Test hooks (debugAbort) exist only when the test harness asks for them.
    if (char const* t = std::getenv("INBOUND_TORRENT_TEST_HOOKS"); t && std::string_view(t) == "1") test_hooks = true;

    inbound::torrent::Output out;
    inbound::torrent::Inbox inbox;
    std::thread reader(inbound::torrent::reader_thread, std::ref(inbox));
    reader.detach(); // blocked in fread on stdin; the process exit ends it
    inbound::torrent::Worker worker(out, inbox, dht_state, test_hooks);
    int const rc = worker.run();
    std::fflush(stdout);
    std::_Exit(rc);
}
