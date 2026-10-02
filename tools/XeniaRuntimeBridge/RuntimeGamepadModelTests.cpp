#include "RuntimeGamepadModel.h"
#include <array>
#include <cstdio>
#include <initializer_list>
#include <limits>
#include <stdexcept>
#include <string>

using namespace bmt::gamepad;

namespace {
unsigned cases = 0, failures = 0;

void Check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

template <class Function>
void Case(const std::string& name, Function test) {
    ++cases;
    try { test(); }
    catch (const std::exception& error) {
        ++failures;
        std::fprintf(stderr, "%s: %s\n", name.c_str(), error.what());
    }
}

Context AdmittedContext() { return {{7, 11, 23, 41}, true, true, true, true, true, true}; }
Request Pulse(const Context& context) { return {context.token, 101, 0, 0x10, 100, 1000}; }

void Begin(Model& model, const Context& context, std::uint64_t now = 1000) {
    Check(model.Admit(context, Pulse(context), now) == Failure::None, "valid pulse admission");
}

GuestReturn Returned(const Model& model, std::uint64_t ordinal = 1) {
    const auto& snapshot = model.Current();
    return {Origin::GuestXam, snapshot.request.token, snapshot.request.requestId,
        ordinal, 0, 0, true, false, snapshot.desired};
}

void RequireCleared(const Model& model, Failure reason) {
    const auto& state = model.Current();
    Check(state.phase == Phase::Failed && state.failure == reason, "explicit failed outcome");
    Check(state.desired.buttons == 0 && state.hostButtonsCleared, "host state cleared");
    Check(!state.releaseObserved && !state.release.available, "no invented guest release");
}

void LegalMasksAndTimes() {
    for (const auto mask : std::array<std::uint16_t, 9>{0x1, 0x2, 0x4, 0x8, 0x10, 0x1000, 0x2000, 0x1005, 0x3010}) {
        Case("legal-mask-" + std::to_string(mask), [=] {
            const auto context = AdmittedContext();
            auto request = Pulse(context);
            request.buttons = mask;
            request.holdMilliseconds = 20;
            request.deadlineMilliseconds = 21;
            Model model;
            Check(model.Admit(context, request, 0) == Failure::None, "legal digital request");
            Check(model.Current().desired.buttons == mask, "requested state available");
            Check(!model.Current().press.available, "admission is not guest delivery");
        });
    }
    for (const auto mask : std::array<std::uint16_t, 8>{0, 0x3, 0xc, 0x20, 0x40, 0x400, 0x4000, 0xffff}) {
        Case("invalid-mask-" + std::to_string(mask), [=] {
            const auto context = AdmittedContext();
            auto request = Pulse(context);
            request.buttons = mask;
            Model model;
            Check(model.Admit(context, request, 0) == Failure::InvalidRequest, "mask refused");
            Check(model.Current().desired.buttons == 0 && model.Current().phase == Phase::Idle, "rejection stays neutral");
        });
    }
    struct Timing { std::uint32_t hold, deadline; bool accepted; };
    for (const auto row : std::array<Timing, 7>{{{19, 1000, false}, {20, 21, true},
        {500, 2000, true}, {501, 2000, false}, {100, 100, false},
        {100, 0, false}, {100, 2001, false}}}) {
        Case("timing-" + std::to_string(row.hold) + "-" + std::to_string(row.deadline), [=] {
            const auto context = AdmittedContext();
            auto request = Pulse(context);
            request.holdMilliseconds = row.hold;
            request.deadlineMilliseconds = row.deadline;
            Model model;
            Check((model.Admit(context, request, 0) == Failure::None) == row.accepted, "timing boundary");
        });
    }
    Case("unsupported-slot-and-zero-request", [] {
        const auto context = AdmittedContext();
        auto request = Pulse(context);
        Model model;
        request.slot = 1;
        Check(model.Admit(context, request, 0) == Failure::InvalidRequest, "slot 1 unavailable");
        request.slot = 0;
        request.requestId = 0;
        Check(model.Admit(context, request, 0) == Failure::InvalidRequest, "request zero refused");
    });
}

void EvidenceGates() {
    struct Gate { bool Context::*field; Failure reason; const char* name; };
    for (const auto row : std::array<Gate, 6>{{
        {&Context::connected, Failure::NotConnected, "connection"},
        {&Context::capturing, Failure::NotCapturing, "capture"},
        {&Context::profileVerified, Failure::ProfileUnavailable, "profile"},
        {&Context::guestIdentityVerified, Failure::GuestUnavailable, "guest"},
        {&Context::windowIdentityVerified, Failure::WindowUnavailable, "window"},
        {&Context::foreground, Failure::FocusLost, "foreground"}}}) {
        Case(std::string("evidence-") + row.name, [=] {
            auto context = AdmittedContext();
            const auto request = Pulse(context);
            context.*(row.field) = false;
            Model model;
            Check(model.Admit(context, request, 0) == row.reason, "missing evidence refuses admission");
            context.*(row.field) = true;
            Begin(model, context);
            context.*(row.field) = false;
            model.Tick(context, 1001);
            RequireCleared(model, row.reason);
        });
    }
    for (const auto field : std::array<std::uint64_t Token::*, 4>{
        &Token::connection, &Token::capture, &Token::profile, &Token::window}) {
        Case("token-component-" + std::to_string(cases), [=] {
            auto context = AdmittedContext();
            auto request = Pulse(context);
            request.token.*field = 0;
            Model model;
            Check(model.Admit(context, request, 0) == Failure::BindingChanged, "request bound to actual context");
            context.token.*field = 0;
            Check(model.Admit(context, request, 0) == Failure::InvalidToken, "zero context identity unavailable");
            context = AdmittedContext();
            Begin(model, context);
            ++(context.token.*field);
            model.Tick(context, 1001);
            RequireCleared(model, Failure::BindingChanged);
        });
    }
}

void DeliveryAndTime() {
    Case("actual-press-starts-hold-and-actual-neutral-completes", [] {
        const auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        model.Tick(context, 1500);
        Check(model.Current().phase == Phase::AwaitingPress, "delay before poll does not start hold");
        Check(model.Observe(context, Returned(model), 1500) == ObservationResult::PressObserved, "guest press observed");
        Check(model.Observe(context, Returned(model, 2), 1590) == ObservationResult::RepeatedPress, "repeat press retained");
        model.Tick(context, 1599);
        Check(model.Current().desired.buttons == 0x10, "held to observed press deadline");
        model.Tick(context, 1600);
        Check(model.Current().phase == Phase::AwaitingRelease && model.Current().desired.buttons == 0, "release requested on time");
        Check(!model.Current().releaseObserved, "host clear does not prove release");
        Check(model.Observe(context, Returned(model, 3), 1601) == ObservationResult::ReleaseObserved, "actual neutral observed");
        Check(model.Current().phase == Phase::Observed && model.Current().releaseObserved, "complete delivery");
        Check(model.Current().press.returned.ordinal == 1 && model.Current().release.returned.ordinal == 3, "exact observed identity retained");
        Check(model.Current().press.observedMilliseconds == 1500, "repeat does not restart hold");
        Check(model.Current().release.returned.state.packet > model.Current().press.returned.state.packet, "transition packet");
    });
    for (const bool pressed : {false, true}) {
        Case(pressed ? "no-release-poll" : "no-guest-poll", [=] {
            const auto context = AdmittedContext();
            Model model;
            Begin(model, context);
            if (pressed) model.Observe(context, Returned(model), 1001);
            model.Tick(context, 2000);
            RequireCleared(model, Failure::Deadline);
            Check(model.Current().press.available == pressed, "only observed press retained");
        });
    }
    Case("deadline-takes-precedence-over-neutral-at-boundary", [] {
        const auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        model.Observe(context, Returned(model), 1000);
        model.Tick(context, 1100);
        const auto neutral = Returned(model, 2);
        model.Observe(context, neutral, 2000);
        RequireCleared(model, Failure::Deadline);
    });
    Case("clock-regression-clears-and-overflow-safe-deadline", [] {
        const auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        model.Tick(context, 999);
        RequireCleared(model, Failure::ClockRegressed);
        Model nearEnd;
        const auto start = std::numeric_limits<std::uint64_t>::max() - 1000;
        Begin(nearEnd, context, start);
        nearEnd.Tick(context, start + 999);
        Check(nearEnd.Active(), "deadline addition does not overflow");
        nearEnd.Tick(context, start + 1000);
        RequireCleared(nearEnd, Failure::Deadline);
    });
}

void ObservationAdmission() {
    for (const auto origin : {Origin::Host, Origin::Unknown}) {
        Case("non-guest-origin-" + std::to_string(static_cast<int>(origin)), [=] {
            const auto context = AdmittedContext();
            Model model;
            Begin(model, context);
            State output;
            output.buttons = 0x55;
            Check(!model.StateForPoll(context, origin, 0, 1100, output), "host poll declines state");
            Check(output.buttons == 0x55, "declining does not write host buffer");
            auto returned = Returned(model);
            returned.origin = origin;
            Check(model.Observe(context, returned, 1100) == ObservationResult::IgnoredHost, "not guest receipt");
            Check(!model.Current().press.available, "host cannot begin hold");
        });
    }
    Case("supplying-state-is-not-observation-and-other-slot-is-ignored", [] {
        const auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        State supplied;
        Check(model.StateForPoll(context, Origin::GuestXam, 0, 1001, supplied), "selected guest state supplied");
        Check(supplied.buttons == 0x10 && !model.Current().press.available, "no inferred return");
        Check(!model.StateForPoll(context, Origin::GuestXam, 1, 1002, supplied), "other slot untouched");
        auto returned = Returned(model);
        returned.slot = 1;
        Check(model.Observe(context, returned, 1002) == ObservationResult::IgnoredOtherSlot, "other slot not attributed");
    });
    struct Unavailable { bool noOutput, suppressed; std::uint32_t result; Failure reason; };
    for (const auto row : std::array<Unavailable, 3>{{
        {true, false, 0, Failure::NoOutput}, {false, true, 0, Failure::UiSuppressed},
        {false, false, 1167, Failure::GuestError}}}) {
        Case("unavailable-return-" + std::to_string(static_cast<int>(row.reason)), [=] {
            const auto context = AdmittedContext();
            Model model;
            Begin(model, context);
            auto returned = Returned(model);
            returned.outputAvailable = !row.noOutput;
            returned.uiSuppressed = row.suppressed;
            returned.result = row.result;
            returned.state = {};  // No meaningful output exists in these cases.
            Check(model.Observe(context, returned, 1001) == ObservationResult::Failed, "unavailable return explicit");
            RequireCleared(model, row.reason);
        });
    }
    for (const bool axis : {false, true}) {
        Case(axis ? "altered-axis" : "altered-buttons", [=] {
            const auto context = AdmittedContext();
            Model model;
            Begin(model, context);
            auto returned = Returned(model);
            if (axis) returned.state.leftX = 1;
            else returned.state.buttons = 0x1000;
            model.Observe(context, returned, 1001);
            RequireCleared(model, Failure::ReturnedStateMismatch);
        });
    }
    Case("duplicate-and-stale-state-cannot-complete-release", [] {
        const auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        const auto press = Returned(model);
        model.Observe(context, press, 1001);
        Check(model.Observe(context, press, 1002) == ObservationResult::IgnoredDuplicate, "duplicate return ignored");
        model.Tick(context, 1101);
        auto latePress = press;
        latePress.ordinal = 2;
        Check(model.Observe(context, latePress, 1102) == ObservationResult::IgnoredStale, "older packet cannot confirm release");
        Check(!model.Current().releaseObserved && model.Active(), "release still pending");
        auto neutral = Returned(model, 1);
        Check(model.Observe(context, neutral, 1103) == ObservationResult::IgnoredDuplicate, "ordinal must progress");
        neutral.ordinal = 3;
        Check(model.Observe(context, neutral, 1104) == ObservationResult::ReleaseObserved, "fresh neutral succeeds");
    });
}

void Lifecycle() {
    Case("busy-and-stale-cancel-do-not-replace-current-request", [] {
        const auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        auto next = Pulse(context);
        ++next.requestId;
        Check(model.Admit(context, next, 1001) == Failure::Busy, "one action at a time");
        auto old = context.token;
        --old.capture;
        Check(!model.Cancel(old, 101) && !model.Cancel(context.token, 100), "stale cancel ignored");
        Check(model.Current().request.requestId == 101 && model.Current().desired.buttons == 0x10, "current request preserved");
        Check(model.Cancel(context.token, 101), "matched cancel accepted");
        RequireCleared(model, Failure::Cancelled);
        Check(!model.Cancel(context.token, 101), "cancel idempotent");
    });
    Case("capture-and-connection-changes-reject-old-observations", [] {
        auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        auto oldReturn = Returned(model);
        ++context.token.capture;
        model.Tick(context, 1001);
        RequireCleared(model, Failure::BindingChanged);
        auto next = Pulse(context);
        ++next.requestId;
        Check(model.Admit(context, next, 1002) == Failure::None, "new capture admitted");
        Check(model.Observe(context, oldReturn, 1003) == ObservationResult::IgnoredStale, "old capture ignored");
        Check(!model.Current().press.available && model.Active(), "new capture unaffected");
        const auto previous = Returned(model);
        context.connected = false;
        model.Tick(context, 1004);
        RequireCleared(model, Failure::NotConnected);
        context.connected = true;
        ++context.token.connection;
        ++context.token.capture;
        next = Pulse(context);
        next.requestId = 1;
        Check(model.Admit(context, next, 1005) == Failure::None, "new connection may restart request IDs");
        Check(model.Observe(context, previous, 1006) == ObservationResult::IgnoredStale, "old connection ignored");
        Check(!model.Current().press.available, "no inherited delivery");
    });
    Case("request-id-reuse-is-rejected-within-connection", [] {
        auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        model.Cancel(context.token, 101);
        ++context.token.capture;
        Check(model.Admit(context, Pulse(context), 1001) == Failure::ReusedRequest, "same connection cannot reuse request ID");
        Check(model.Current().failure == Failure::Cancelled, "failed admission retains prior outcome");
    });
    Case("shutdown-clears-and-seals-admission", [] {
        const auto context = AdmittedContext();
        Model model;
        Begin(model, context);
        model.Close();
        RequireCleared(model, Failure::Shutdown);
        model.Close();
        Check(model.Admit(context, Pulse(context), 1001) == Failure::Closed, "closed model cannot reacquire input");
    });
}
}

int main() {
    LegalMasksAndTimes();
    EvidenceGates();
    DeliveryAndTime();
    ObservationAdmission();
    Lifecycle();
    std::printf("{\"cases\":%u,\"passed\":%u,\"failed\":%u}\n", cases, cases - failures, failures);
    return failures ? 1 : 0;
}
