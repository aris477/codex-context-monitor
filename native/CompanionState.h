#pragma once
#include <cstdint>
#include <string>

struct CompanionHost {
    virtual bool codexRunning() = 0;
    virtual bool paused() = 0;
    virtual void clearPause() = 0;
    virtual std::string health() = 0;
    virtual void start() = 0;
    virtual void stop(const std::string& identity) = 0;
    virtual ~CompanionHost() = default;
};

class CompanionState {
    CompanionHost& host;
    bool absent = false;
    uint64_t absentAt = 0, nextStart = 0;
    std::string managed;
public:
    explicit CompanionState(CompanionHost& host) : host(host) {}
    void tick(uint64_t now) {
        if (!host.codexRunning()) {
            if (!absent) { absent = true; absentAt = now; }
            if (now - absentAt < 30000) return;
            if (!managed.empty()) { host.stop(managed); managed.clear(); }
            host.clearPause(); nextStart = 0;
            return;
        }
        absent = false;
        if (host.paused()) { managed.clear(); return; }
        auto live = host.health();
        if (!live.empty()) { managed = live; return; }
        if (now < nextStart) return;
        nextStart = now + 60000;
        host.start();
    }
};
