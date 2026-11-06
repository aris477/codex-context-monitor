#define COMPANION_TEST
#include "../native/CodexCompanion.cpp"
#include <iostream>
#include <stdexcept>
struct FakeHost : CompanionHost {
    bool running = false, pause = false; int starts = 0, stops = 0; std::string live;
    bool codexRunning() override { return running; }
    bool paused() override { return pause; }
    void clearPause() override { pause = false; }
    std::string health() override { return live; }
    void start() override { ++starts; }
    void stop(const std::string& identity) override { if (live == identity) { ++stops; live.clear(); } }
};
void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); std::cout << "PASS " << message << "\n"; }
int main() {
    try {
        FakeHost h; CompanionState state(h);
        state.tick(0); check(h.starts == 0, "Codex absent keeps service stopped");
        h.running = true; state.tick(1); check(h.starts == 1, "Codex starts service");
        state.tick(15000); check(h.starts == 1, "failed starts are rate limited");
        h.live = "100:old"; state.tick(20000); check(h.starts == 1, "healthy service reused");
        h.running = false; state.tick(25000); check(h.stops == 0, "exit grace period");
        h.running = true; state.tick(30000); check(h.stops == 0, "brief restart preserves service");
        h.running = false; state.tick(40000); state.tick(70000); check(h.stops == 1, "full exit stops managed service");
        h.running = true; state.tick(71000); check(h.starts == 2, "new app launch resumes service");
        h.live = "100:new"; state.tick(72000); h.pause = true; h.live.clear(); state.tick(150000);
        check(h.starts == 2, "manual pause suppresses recovery");
        h.running = false; state.tick(160000); state.tick(190000); check(!h.pause, "exit clears manual pause");
        h.running = true; state.tick(191000); check(h.starts == 3, "next app session recovers");
        h.live = "100:before"; state.tick(200000); h.running = false; state.tick(210000); h.live = "200:other"; state.tick(240000);
        check(h.stops == 1, "identity changes prevent termination");
        check(officialPath(L"D:\\WindowsApps\\OpenAI.Codex_1\\app\\ChatGPT.exe"), "official store app accepted on other drive");
        check(!officialPath(L"C:\\Tools\\codex.exe") && !officialPath(L"C:\\WindowsApps\\OpenAI.ChatGPT_1\\ChatGPT.exe"), "CLI and ordinary ChatGPT excluded");
        const std::string valid = "{\"app\":\"codex-context-monitor\",\"pid\":123,\"port\":3765,\"instance\":\"033c9498-2a88-410a-8e4a-fd1455bef471\"}";
        Identity a; check(parseIdentity(valid, a) && a.pid == 123, "flat health identity parsed");
        Identity b; check(!parseIdentity(valid + "junk", b), "trailing input rejected");
        Identity c; auto duplicate = valid; duplicate.insert(1, "\"pid\":456,"); check(!parseIdentity(duplicate,c), "duplicate identity rejected");
        Identity d; check(!parseIdentity("{\"app\":\"codex-context-monitor\",\"pid\":4294967296,\"instance\":\"033c9498-2a88-410a-8e4a-fd1455bef471\"}",d), "PID overflow rejected");
        Identity e; check(!parseIdentity("{\"app\":\"codex-context-monitor\",\"pid\":123}",e), "missing identity rejected");
        return 0;
    } catch (const std::exception& e) { std::cerr << "FAIL " << e.what() << "\n"; return 1; }
}
