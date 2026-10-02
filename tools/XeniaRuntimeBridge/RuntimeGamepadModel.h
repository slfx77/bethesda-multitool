#pragma once

// Pure bounded controller model. No driver, engine, window, clock or IPC calls.
// The adapter must serialize calls and supply independently verified current
// session evidence and final guest XAM return values. Supplying state is not
// proof that the guest received it.
#include <cstdint>

namespace bmt::gamepad {

constexpr std::uint16_t kAllowedButtons = 0x301f;  // D-pad, START, A, B.
constexpr std::uint16_t kDpadVertical = 0x0003;
constexpr std::uint16_t kDpadHorizontal = 0x000c;

struct Token {
    std::uint64_t connection = 0;
    std::uint64_t capture = 0;
    std::uint64_t profile = 0;
    std::uint64_t window = 0;

    bool Valid() const noexcept { return connection && capture && profile && window; }
    bool operator==(const Token& other) const noexcept {
        return connection == other.connection && capture == other.capture &&
            profile == other.profile && window == other.window;
    }
    bool operator!=(const Token& other) const noexcept { return !(*this == other); }
};

// Tokens identify the actual connection/capture and immutable profile/window
// evidence. A token alone does not establish any of these admission facts.
struct Context {
    Token token;
    bool connected = false;
    bool capturing = false;
    bool profileVerified = false;
    bool guestIdentityVerified = false;
    bool windowIdentityVerified = false;
    bool foreground = false;
};

struct Request {
    Token token;
    std::uint64_t requestId = 0;
    std::uint32_t slot = 0;
    std::uint16_t buttons = 0;
    std::uint32_t holdMilliseconds = 0;
    std::uint32_t deadlineMilliseconds = 0;
};

struct State {
    std::uint32_t packet = 0;
    std::uint16_t buttons = 0;
    std::uint8_t leftTrigger = 0, rightTrigger = 0;
    std::int16_t leftX = 0, leftY = 0, rightX = 0, rightY = 0;

    bool operator==(const State& other) const noexcept {
        return packet == other.packet && buttons == other.buttons &&
            leftTrigger == other.leftTrigger && rightTrigger == other.rightTrigger &&
            leftX == other.leftX && leftY == other.leftY &&
            rightX == other.rightX && rightY == other.rightY;
    }
};

enum class Phase { Idle, AwaitingPress, Holding, AwaitingRelease, Observed, Failed };
enum class Failure {
    None, Closed, Busy, InvalidToken, NotConnected, NotCapturing,
    ProfileUnavailable, GuestUnavailable, WindowUnavailable, FocusLost,
    BindingChanged, InvalidRequest, ReusedRequest, ClockRegressed,
    Deadline, Cancelled, Shutdown, GuestError, NoOutput, UiSuppressed,
    ReturnedStateMismatch
};
enum class Origin { Unknown, Host, GuestXam };
enum class ObservationResult {
    IgnoredHost, IgnoredStale, IgnoredOtherSlot, IgnoredDuplicate,
    Inactive, Failed, PressObserved, RepeatedPress, ReleaseObserved
};

// This is an explicit adapter input, not constructed by StateForPoll. Ordinal is
// a strictly increasing guest-return observation ID, not an invented engine
// frame or the event queue's eventual sequence number.
struct GuestReturn {
    Origin origin = Origin::Unknown;
    Token token;
    std::uint64_t requestId = 0;
    std::uint64_t ordinal = 0;
    std::uint32_t slot = 0;
    std::uint32_t result = 0;
    bool outputAvailable = false;
    bool uiSuppressed = false;
    State state;
};

struct Observation {
    bool available = false;
    std::uint64_t observedMilliseconds = 0;
    GuestReturn returned;
};

struct Snapshot {
    Phase phase = Phase::Idle;
    Failure failure = Failure::None;
    Request request;
    State desired;
    Observation press;
    Observation release;
    std::uint64_t admittedMilliseconds = 0;
    bool hostButtonsCleared = true;
    bool releaseObserved = false;
};

class Model {
public:
    const Snapshot& Current() const noexcept { return state_; }
    bool Active() const noexcept {
        return state_.phase == Phase::AwaitingPress || state_.phase == Phase::Holding ||
            state_.phase == Phase::AwaitingRelease;
    }

    static bool LegalRequest(const Request& request) noexcept {
        return request.token.Valid() && request.requestId != 0 && request.slot == 0 &&
            request.buttons != 0 && (request.buttons & ~kAllowedButtons) == 0 &&
            (request.buttons & kDpadVertical) != kDpadVertical &&
            (request.buttons & kDpadHorizontal) != kDpadHorizontal &&
            request.holdMilliseconds >= 20 && request.holdMilliseconds <= 500 &&
            request.deadlineMilliseconds > request.holdMilliseconds &&
            request.deadlineMilliseconds <= 2000;
    }

    // A rejected request cannot replace an active action or its retained outcome.
    Failure Admit(const Context& context, const Request& request, std::uint64_t now) noexcept {
        if (closed_) return Failure::Closed;
        if (Active()) return Failure::Busy;
        const auto admission = CheckContext(context);
        if (admission != Failure::None) return admission;
        if (request.token != context.token) return Failure::BindingChanged;
        if (!LegalRequest(request)) return Failure::InvalidRequest;
        if (lastConnection_ == request.token.connection && request.requestId <= lastRequest_)
            return Failure::ReusedRequest;
        if (hasClock_ && now < lastNow_) return Failure::ClockRegressed;
        const auto packet = state_.desired.packet;
        state_ = {};
        state_.desired.packet = packet;
        state_.request = request;
        state_.admittedMilliseconds = now;
        state_.phase = Phase::AwaitingPress;
        SetButtons(request.buttons);
        lastConnection_ = request.token.connection;
        lastRequest_ = request.requestId;
        lastOrdinal_ = 0;
        lastNow_ = now;
        hasClock_ = true;
        return Failure::None;
    }

    // Must also run from a host timer/lifecycle path when the guest never polls.
    // `context` is the CURRENT session, not evidence copied from an old callback.
    void Tick(const Context& context, std::uint64_t now) noexcept {
        if (!Active()) return;
        const auto admission = CheckContext(context);
        if (admission != Failure::None) { Fail(admission); return; }
        if (context.token != state_.request.token) { Fail(Failure::BindingChanged); return; }
        if (now < lastNow_) { Fail(Failure::ClockRegressed); return; }
        lastNow_ = now;
        // Subtraction avoids overflow at the upper end of a monotonic counter.
        if (now - state_.admittedMilliseconds >= state_.request.deadlineMilliseconds) {
            Fail(Failure::Deadline);
            return;
        }
        if (state_.phase == Phase::Holding &&
            now - state_.press.observedMilliseconds >= state_.request.holdMilliseconds) {
            SetButtons(0);
            state_.phase = Phase::AwaitingRelease;
        }
    }

    // Host UI polls never acquire controller state. The adapter supplies its
    // reserved-slot neutral state outside an action; false means no action state.
    // Capture request/token identity under the same adapter lock at poll entry;
    // never relabel a delayed guest return with the model's newer current action.
    bool StateForPoll(const Context& context, Origin origin, std::uint32_t slot,
        std::uint64_t now, State& output) noexcept {
        Tick(context, now);
        if (origin != Origin::GuestXam || slot != 0 || !Active()) return false;
        output = state_.desired;
        return true;
    }

    ObservationResult Observe(const Context& current, const GuestReturn& returned,
        std::uint64_t now) noexcept {
        Tick(current, now);
        if (returned.origin != Origin::GuestXam) return ObservationResult::IgnoredHost;
        if (returned.token != state_.request.token || returned.requestId != state_.request.requestId)
            return ObservationResult::IgnoredStale;
        if (returned.slot != state_.request.slot) return ObservationResult::IgnoredOtherSlot;
        if (!Active()) return ObservationResult::Inactive;
        if (returned.ordinal == 0 || returned.ordinal <= lastOrdinal_)
            return ObservationResult::IgnoredDuplicate;
        if (returned.result != 0) return FailObservation(Failure::GuestError);
        if (!returned.outputAvailable) return FailObservation(Failure::NoOutput);
        if (returned.uiSuppressed) return FailObservation(Failure::UiSuppressed);
        // A return for an older supplied state cannot satisfy the next transition.
        if (returned.state.packet != state_.desired.packet) return ObservationResult::IgnoredStale;
        if (!(returned.state == state_.desired)) return FailObservation(Failure::ReturnedStateMismatch);
        lastOrdinal_ = returned.ordinal;
        if (state_.phase == Phase::AwaitingPress) {
            state_.press = {true, now, returned};
            state_.phase = Phase::Holding;
            return ObservationResult::PressObserved;
        }
        if (state_.phase == Phase::Holding) return ObservationResult::RepeatedPress;
        state_.release = {true, now, returned};
        state_.releaseObserved = true;
        state_.phase = Phase::Observed;
        return ObservationResult::ReleaseObserved;
    }

    bool Cancel(const Token& token, std::uint64_t requestId) noexcept {
        if (!Active() || token != state_.request.token || requestId != state_.request.requestId) return false;
        Fail(Failure::Cancelled);
        return true;
    }

    void Close() noexcept {
        closed_ = true;
        if (Active()) Fail(Failure::Shutdown);
        else SetButtons(0);
    }

private:
    static Failure CheckContext(const Context& context) noexcept {
        if (!context.token.Valid()) return Failure::InvalidToken;
        if (!context.connected) return Failure::NotConnected;
        if (!context.capturing) return Failure::NotCapturing;
        if (!context.profileVerified) return Failure::ProfileUnavailable;
        if (!context.guestIdentityVerified) return Failure::GuestUnavailable;
        if (!context.windowIdentityVerified) return Failure::WindowUnavailable;
        if (!context.foreground) return Failure::FocusLost;
        return Failure::None;
    }
    void SetButtons(std::uint16_t buttons) noexcept {
        if (state_.desired.buttons != buttons) ++state_.desired.packet;
        state_.desired.buttons = buttons;
        state_.hostButtonsCleared = buttons == 0;
    }
    void Fail(Failure reason) noexcept {
        SetButtons(0);
        state_.phase = Phase::Failed;
        state_.failure = reason;
    }
    ObservationResult FailObservation(Failure reason) noexcept {
        Fail(reason);
        return ObservationResult::Failed;
    }

    Snapshot state_;
    std::uint64_t lastConnection_ = 0, lastRequest_ = 0, lastOrdinal_ = 0, lastNow_ = 0;
    bool hasClock_ = false, closed_ = false;
};

}  // namespace bmt::gamepad
