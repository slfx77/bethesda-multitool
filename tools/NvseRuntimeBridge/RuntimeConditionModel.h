#pragma once

// Pure PC CTDA evidence model. No engine calls, code writes, hooks or process APIs.
// Layout/ABI provenance: artifacts/prototype-runtime/pc/ctda-design-001/proof.json.
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

namespace bmt::ctda {
using Address = std::uint32_t;
using ReadFunction = bool (*)(void*, Address, void*, std::size_t);

enum class OwnerKind { Info, Quest };
enum class Failure {
    None, InvalidLimits, InvalidOwnerKind, AddressRange, Unreadable, ReadBudget,
    OwnerIdentity, OwnerTemporaryOrDeleted, NodeLimit, Cycle, NullItem,
    OwnerChanged, NodeChanged, ItemChanged,
    SnapshotIncomplete, ProfileProof, CodeProof, TableProof, OwnerLayoutProof,
    NamespaceProof, SubjectProof, ParameterProof, GlobalProof,
    NonDefaultMode, UnsupportedOperator, UnsupportedRunOn, NonfiniteLiteral
};

struct Limits {
    std::size_t maxNodes = 128;
    std::size_t maxReadBytes = 32768;
};

struct Item {
    std::array<std::uint8_t, 28> raw{};
    std::uint8_t flags = 0;
    std::uint8_t comparisonOperator = 0;
    bool orWithNext = false;
    bool globalFlag = false;
    bool usesGlobal = false;
    std::uint32_t comparisonBits = 0;
    std::uint32_t functionStorage = 0;
    std::uint16_t functionIndex = 0;
    std::uint32_t parameter1Raw = 0;
    std::uint32_t parameter2Raw = 0;
    std::uint32_t runOn = 0;
    Address reference = 0;
};

inline std::uint32_t U32(const std::uint8_t* value) noexcept {
    return std::uint32_t(value[0]) | (std::uint32_t(value[1]) << 8) |
        (std::uint32_t(value[2]) << 16) | (std::uint32_t(value[3]) << 24);
}

inline Item ParseItem(const std::array<std::uint8_t, 28>& raw) noexcept {
    Item value;
    value.raw = raw;
    value.flags = raw[0];
    value.comparisonOperator = (raw[0] >> 5) & 7;
    value.orWithNext = (raw[0] & 1) != 0;
    value.globalFlag = (raw[0] & 4) != 0;
    value.comparisonBits = U32(raw.data() + 4);
    // 00681769 tests the pointer/value for zero before the global flag.
    value.usesGlobal = value.globalFlag && value.comparisonBits != 0;
    value.functionStorage = U32(raw.data() + 8);
    value.functionIndex = static_cast<std::uint16_t>(value.functionStorage);
    value.parameter1Raw = U32(raw.data() + 12);
    value.parameter2Raw = U32(raw.data() + 16);
    value.runOn = U32(raw.data() + 20);
    value.reference = U32(raw.data() + 24);
    return value;
}

struct Node {
    Address address = 0;
    std::array<std::uint8_t, 8> raw{};
    Address itemAddress = 0;
    Address next = 0;
    bool hasItem = false;
    Item item;
};

struct OwnerRequest {
    OwnerKind kind = OwnerKind::Info;
    Address address = 0;
    std::uint32_t formId = 0;
};

struct Snapshot {
    Failure failure = Failure::None;
    OwnerRequest owner;
    std::array<std::uint8_t, 16> ownerHeader{};
    Address headAddress = 0;
    std::vector<Node> nodes; // Includes an observed empty sentinel; preserves physical occurrences.
    std::size_t readBytes = 0;
    bool stable = false;

    std::size_t ItemCount() const noexcept {
        return static_cast<std::size_t>(std::count_if(nodes.begin(), nodes.end(),
            [](const Node& node) { return node.hasItem; }));
    }
};

class BudgetReader {
    ReadFunction _read;
    void* _context;
    std::size_t _limit;
public:
    std::size_t bytes = 0;
    Failure failure = Failure::None;
    BudgetReader(ReadFunction reader, void* context, std::size_t limit)
        : _read(reader), _context(context), _limit(limit) {}

    bool Read(Address address, void* destination, std::size_t length) {
        if (!address || (address & 3) != 0 || !length ||
            length - 1 > std::uint64_t(UINT32_MAX) - address) {
            failure = Failure::AddressRange; return false;
        }
        if (bytes > _limit || length > _limit - bytes) {
            failure = Failure::ReadBudget; return false;
        }
        bytes += length;
        if (!_read || !_read(_context, address, destination, length)) {
            failure = Failure::Unreadable; return false;
        }
        return true;
    }
};

inline Snapshot ReadOwnerList(ReadFunction read, void* context, OwnerRequest request, Limits limits = {}) {
    Snapshot result;
    result.owner = request;
    if (!limits.maxNodes || limits.maxNodes > 256 || !limits.maxReadBytes || limits.maxReadBytes > 65536) {
        result.failure = Failure::InvalidLimits; return result;
    }
    std::uint8_t expectedType = 0;
    std::uint32_t headOffset = 0;
    switch (request.kind) {
    case OwnerKind::Info: expectedType = 0x46; headOffset = 0x18; break;
    case OwnerKind::Quest: expectedType = 0x47; headOffset = 0x54; break;
    default: result.failure = Failure::InvalidOwnerKind; return result;
    }
    BudgetReader reader(read, context, limits.maxReadBytes);
    auto fail = [&](Failure failure) {
        result.failure = failure; result.readBytes = reader.bytes; return result;
    };
    if (!reader.Read(request.address, result.ownerHeader.data(), result.ownerHeader.size())) return fail(reader.failure);
    if (!request.formId || result.ownerHeader[4] != expectedType || U32(result.ownerHeader.data() + 12) != request.formId)
        return fail(Failure::OwnerIdentity);
    if ((request.formId >> 24) == 0xFF || (U32(result.ownerHeader.data() + 8) & 0x4020) != 0)
        return fail(Failure::OwnerTemporaryOrDeleted);
    if (std::uint64_t(request.address) + headOffset > UINT32_MAX) return fail(Failure::AddressRange);
    result.headAddress = request.address + headOffset;
    Address next = result.headAddress;
    while (next) {
        if (result.nodes.size() >= limits.maxNodes) return fail(Failure::NodeLimit);
        if (std::any_of(result.nodes.begin(), result.nodes.end(),
            [next](const Node& node) { return node.address == next; })) return fail(Failure::Cycle);
        Node node;
        node.address = next;
        if (!reader.Read(next, node.raw.data(), node.raw.size())) return fail(reader.failure);
        node.itemAddress = U32(node.raw.data());
        node.next = U32(node.raw.data() + 4);
        if (!node.itemAddress && node.next) return fail(Failure::NullItem);
        if (node.itemAddress) {
            std::array<std::uint8_t, 28> raw{};
            if (!reader.Read(node.itemAddress, raw.data(), raw.size())) return fail(reader.failure);
            node.item = ParseItem(raw);
            node.hasItem = true;
        }
        result.nodes.push_back(node);
        next = node.next;
    }
    // Reread every consumed physical path and item, not only the head or item count.
    for (const auto& node : result.nodes) {
        std::array<std::uint8_t, 8> raw{};
        if (!reader.Read(node.address, raw.data(), raw.size())) return fail(reader.failure);
        if (raw != node.raw) return fail(Failure::NodeChanged);
        if (node.hasItem) {
            std::array<std::uint8_t, 28> item{};
            if (!reader.Read(node.itemAddress, item.data(), item.size())) return fail(reader.failure);
            if (item != node.item.raw) return fail(Failure::ItemChanged);
        }
    }
    std::array<std::uint8_t, 16> ownerAfter{};
    if (!reader.Read(request.address, ownerAfter.data(), ownerAfter.size())) return fail(reader.failure);
    if (ownerAfter != result.ownerHeader) return fail(Failure::OwnerChanged);
    result.readBytes = reader.bytes;
    result.stable = true;
    return result;
}

// Evidence inputs must come from the separately validated runtime/profile layer.
// A successful pure-model result neither installs a hook nor invokes an evaluator.
struct AdmissionEvidence {
    bool executableProfile = false;
    bool loadedFunctionsAndJumpTables = false;
    bool independentCommandTable = false;
    bool ownerLayout = false;
    bool pluginNamespace = false;
    bool subjectAndTarget = false;
    bool commandParameters = false;
    bool globalIdentities = false;
    bool mode = false;
};

inline Failure CheckAdmission(const Snapshot& snapshot, const AdmissionEvidence& evidence) noexcept {
    if (!snapshot.stable || snapshot.failure != Failure::None) return Failure::SnapshotIncomplete;
    if (!evidence.executableProfile) return Failure::ProfileProof;
    if (!evidence.loadedFunctionsAndJumpTables) return Failure::CodeProof;
    if (!evidence.independentCommandTable) return Failure::TableProof;
    if (!evidence.ownerLayout) return Failure::OwnerLayoutProof;
    if (!evidence.pluginNamespace) return Failure::NamespaceProof;
    if (!evidence.subjectAndTarget) return Failure::SubjectProof;
    if (!evidence.commandParameters) return Failure::ParameterProof;
    if (evidence.mode) return Failure::NonDefaultMode;
    for (const auto& node : snapshot.nodes) {
        if (!node.hasItem) continue;
        const auto& item = node.item;
        if (item.comparisonOperator > 5) return Failure::UnsupportedOperator;
        if (item.runOn > 2) return Failure::UnsupportedRunOn;
        if (item.usesGlobal) {
            if (!evidence.globalIdentities) return Failure::GlobalProof;
        } else if ((item.comparisonBits & 0x7F800000) == 0x7F800000) return Failure::NonfiniteLiteral;
    }
    return Failure::None;
}

enum class SpanKind { Call, MidFunction };
struct CodeSpan {
    Address address;
    std::uint8_t length;
    SpanKind kind;
    std::array<std::uint8_t, 6> bytes;
    Address targetOrResume;
};

inline constexpr std::array<CodeSpan, 6> RetainedSpans{{
    {0x00680D68, 5, SpanKind::Call, {0xE8,0x93,0x08,0x00,0x00,0}, 0x00681600},
    {0x00680DC9, 5, SpanKind::Call, {0xE8,0x32,0x08,0x00,0x00,0}, 0x00681600},
    {0x00680E1F, 5, SpanKind::Call, {0xE8,0xDC,0x07,0x00,0x00,0}, 0x00681600},
    {0x006817B4, 5, SpanKind::Call, {0xE8,0x97,0xF8,0xFF,0xFF,0}, 0x00681050},
    {0x00680EC4, 6, SpanKind::MidFunction, {0x0F,0xB6,0x55,0xF7,0x85,0xD2}, 0x00680ECA},
    {0x00680F42, 6, SpanKind::MidFunction, {0x0F,0xB6,0x55,0xF7,0x85,0xD2}, 0x00680F48}
}};

inline bool MatchesSpan(ReadFunction read, void* context, const CodeSpan& span) {
    if (!read || !span.address || !span.length || span.length > span.bytes.size() ||
        std::uint64_t(span.address) + span.length - 1 > UINT32_MAX) return false;
    std::array<std::uint8_t, 6> actual{};
    return read(context, span.address, actual.data(), span.length) &&
        std::equal(actual.begin(), actual.begin() + span.length, span.bytes.begin());
}
} // namespace bmt::ctda
